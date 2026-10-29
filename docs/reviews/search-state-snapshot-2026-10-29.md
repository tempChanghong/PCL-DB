# 搜索会话状态的并发读取

基线：`5e83f8ae689386611d3ed61465e6cba460b5ac04`。本轮只修复 Core 搜索会话的公开状态读取，保留 VB UI、动画、net48、配置和现有下载／安装机制。

## 问题与适用范围

搜索请求由异步 gate 串行执行，但 `HasMore`、`HasNext`、`ErrorMessage`、`FailedPlatforms` 原来会直接枚举正在提交的 Dictionary。仅更新已有来源的 offset 值也会使字典枚举器失效。公开 API 的受控多线程探针已分别复现 InvalidOperationException，以及复制错误来源数组时的 ArgumentException；发生时搜索请求本身仍成功完成。

初始证据在 `artifacts/search-session-concurrency/REVIEW.md`，本轮当前基线与初步候选的独立复核在 `artifacts/search-state-snapshot-review/`：旧 Core 的四个入口均重新复现；初步候选四份探针各运行 250 会话，共 1000 会话、3,115,019 轮读取，未出现状态读取异常。初步探针与最终构建分别绑定摘要，不能当作同一候选回执。均为受控 Core 调用，不作为普通桌面操作崩溃的录像证据。现有 VB UI 大部分路径在 await 后、通过 request／session 身份校验再读取状态；本轮保留这些门控，不因 Core 修复而放宽快速切换、返回或加载动画的归属。

## 修复

新增一个私有状态快照，保存已完成轮次的 Count、HasMore、错误文本和失败来源。构造完成来源筛选后发布初态；每轮来源状态、去重和结果追加全部结束后，才通过 Volatile.Write 发布一个完整对象。公开 getter 用 Volatile.Read 获取快照，不再枚举可变集合。

`HasNext` 在一次调用内使用同一个快照计算数量与来源状态，保留最大页和“继续加载”的既有规则。`FailedPlatforms` 返回防御性副本，调用方改数组不会污染会话。分别调用不同 getter 仍可能跨越一次提交，不提供跨调用事务；这与提供单个一致状态读取是不同的保证。

可变集合仍由原异步 gate 的单写者使用；没有给网络 await、预览回调、取消通知或任务 join 加锁，也没有让同步 getter 等待异步 gate，避免合法进度观察者读取状态时发生死锁。预览展示新到的候选结果，公开状态继续代表已提交轮次。取消和异常清理不发布未完成轮次，此前有效轮次仍保留；原 offset、排序、平台部分失败、缓存和重试规则保持。

只新增 private 类型、字段和方法，未增加公开／友元接口，也未修改程序集名称、资源名、配置格式或依赖。

## 源码依据

- 以 PCL2 固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 为核心，核对 `Plain Craft Launcher 2/Modules/Resource/ResourceSearcher.vb:407-449`：等待来源结束，再处理结果与部分失败，退出时收尾仍存活的线程；`:522-524` 再检查取消后追加结果。沿用来源游标和已接受结果的边界，不改变原始分页语义。
- C# 重点参考 PCL-CE `dev` `915ac29795efbdf6373417389e181e1504117048`，`Modules/Minecraft/ModComp.cs:2595-2628` 附近：来源结果列表在短锁内合并，等待来源后检查中止，再处理排序、去重和分页；`:2667-2671,2718-2719` 在最终输出前再次检查中止再追加结果。PCL-DB 使用自身已有的异步单写 gate 加不可变发布，不照搬同步 WaitAll，也不声称 CE 的全部共享状态均已具备相同并发保证。精确来源与方案复核见 `artifacts/search-state-concurrency/upstream-snapshot-review.md`。

## 验证与限制

新增 44 项 VB 回归通过公开搜索入口检查 12 个并发会话、预览同步／跨线程回调读取状态、返回数组隔离、取消保留已提交的 40 条结果并重试取得完整 80 条，以及来源偏移／相关性排序／部分失败／缓存与分页边界。不把独立 getter 的多次读取误当成跨属性事务。相同 VB EXE 只替换旧 Core 的负控制在 HasMore 精确复现集合枚举异常，日志位于 `artifacts/search-session-concurrency/vb-state-regression/`。旧 Core 负控制与初步探针单独保存，最终候选使用重新编译的完整 VB 宿主；原有测试和断言保留。

最终完整新 VB 宿主 7468 项、接口比较、原 Beta 8 已编译宿主 7305 项、单 EXE 冷启动 25 项和完整搜索 3240 项，以及源码／二进制／提交树绑定，统一见 `artifacts/search-state-snapshot/validation.json`。构建在提交前完成，再核对提交树与已测源码；各套件重叠，不累计为独立场景数。

测试使用受控请求、临时实例和隐藏 WPF，不修改真实启动器配置，不代表完整在线安装、游戏启动或真人帧率验收。UI 与动画源码未修改，本轮不重复完整 UI／motion 套件。未打包、推送或发版。
