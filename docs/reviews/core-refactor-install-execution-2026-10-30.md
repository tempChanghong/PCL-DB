# 安装并行执行边界提炼

基线 `6420937bfca5163c44ab5552b621c107f29a2f68`。承接界面与生命周期基线，只提炼 C# Core 的私有安装执行边界；保留 VB WPF、原 VB 测试、net48、程序集／公开接口、配置和资源名称，不打包发版。

## 职责与行为

`MinecraftInstaller.InstallCoreAsync` 继续负责入口快照、目录锁、元数据与下载计划、三启动器原生隔离、overrides、配置保护、原子提交、回滚及成功清理。共享预算、linked token、五个下载引擎、支持库／资源／整合包分支汇合和统计迁入 `MinecraftInstaller.DownloadExecution.cs` 的私有 `ExecuteDownloadsAsync`。

20 个私有输入字段只借用原事务已持有的值／对象；它们不是新快照，不克隆、不验证、不提前枚举。helper 以纯字段别名恢复原局部名，包含原 native 对象，因此 `entry.Id`、`isolation.Isolated`、`pack.Files` 和 `publication.StagedFiles()` 仍在原位置求值。返回五项：最终 metadata、同一个 ComponentInstaller、网络字节、复用数和动态分块数。无组件时保留原 metadata 引用。

| 边界 | 保留规则 |
| --- | --- |
| 并行与组件前置 | 客户端／支持库完成后应用组件，资源与整合包继续并行；mapped 仍在 assets 完成后开始。 |
| 任务与错误 | 所有已启动分支汇合；调用方取消仍带原 token；异常继续按 branches 数组及 Flatten 顺序选取非取消项，不改成按墙钟时间抢占。 |
| 统计 | 原引擎和组件统计、`plan.AddRange(...extension.FileCount)` 均留在原 using 层级及读取位置。 |
| 释放 | pack、mapped、assets、support、client 引擎，再 linked token 和共享预算，严格逆序释放后返回结果。 |
| 组件最终保护 | ComponentInstaller 不是 IDisposable；冻结运行库描述随同一个实例交回，最终租约仍在外层提交前取得并持有至 Directory.Move。 |
| 外层交易 | 资源发布对象、根锁、JSON／INI／隔离 guard、提交、回滚和成功后的不可取消清理保留原位置。 |

metadata／extension 从分支内即时更新外层局部，改为 helper 成功后交回。原失败 finally 仅清理私有目录，不读取这两个局部；正常路径继续使用同一对象和冻结描述。新私有载体、结果分配及异步栈帧是结构差异，不声称任意内存耗尽行为或私有堆栈逐字等同。

## 源码依据与静态证明

以固定 PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 ModDownload.vb:54–63、118 为库／资源非阻塞任务阶段依据；C# 重点参考 PCL-CE `915ac29795efbdf6373417389e181e1504117048` 的 ModDownload.cs:76–101、190–193。上游会写入的配置行为不迁入本工具。16 份固定 Git 原文与摘要在本地 `artifacts/installer-execution-refactor/source/manifest.json`。

独立审查逐项核对原并行段 875 tokens 和每行非前导空白完全一致，外层替换可同时逆向恢复 Git LF 与原工作文件字节。完整 helper 模型核对 20 借用字段／别名、五结果字段及纯赋值构造器；14 个静态负控制覆盖 token、数组／释放顺序、异常选择、任务汇合、组件身份、提前枚举、外层及结果赋值，均应拒绝。新增类型与 helper 全为 private，不增加公开或 Friend 业务入口。

## 差分与最终验证

独立 VB/net48 探针复用原测试源，以同一个已编译 EXE 分别加载固定旧 DLL 和候选 DLL；调用真实 InstallAsync。覆盖原并行阶段／取消／预算／失败、失败通知、运行库最终变化／租约／取消／完整摘要，以及原版补全和 Fabric＋assets／pack 双门控。阶段关系用门控验证，不按并发事件的偶然先后排序。原测试正文、断言和时限保持。

冻结探针有 14 组、每侧 206 项断言。保存原始结果后再比较规范化输出：替换临时根路径、实例 JSON 的本地 HTTP 端口，以及两条 final-runtime-change 通过标签中含预期 IOException 私有栈的已知诊断后缀；不通用忽略任意异常。配置／元数据、文件内容、稳定统计、异常／原 token、清理及同名重试仍须相同。探针夹具构造和取样问题的早期记录保留，最终冻结探针须在新旧两侧重新运行；它们不是生产修复或放宽旧测试。

最终门槛为同探针新旧差分、当前 VB 全量 8284、原 Beta 8 编译宿主 7305、相对当前 C# 基线原始／规范 API 零差异（公开 979／Friend 1247）、资源 2／JSON 15、Release／VS2026 Debug 三 Portable PDB，以及同一冷目录单 EXE 启动 25／兼容 299、两依赖／七根文档摘要。相对原 Beta 8 的严格元数据汇总仍仅记录历史 Friend +4，不放宽比较或冒称原始元数据完全一致。旧宿主在主 VB 全量结束后使用默认 TEMP／TMP 串行执行。

实际计数、输入／输出／源码摘要、进程退出及提交树绑定集中于 `artifacts/installer-execution-refactor/validation.json`；源码审查在 audit，差分在 differential。报告只列验收范围，完成状态须以这些真实回执为准。

## 边界与后续

此前 UI 7166／motion 6011／搜索 3444 来自 [前序基线](core-refactor-lifecycle-baseline-2026-10-30.md)，不是本轮新 DLL 上的完整界面运行。本轮不改 VB UI／动画／测试或策略，不重跑完整 UI／motion／搜索；冷启动／兼容检查和安装行为差分不能替代真人桌面流畅度、F5、真实网络下载安装或 Minecraft 运行验收。未写用户实例或外部配置，未打包、推送、发版。

本轮是职责提炼，没有借机修改下载算法、错误优先级或安装格式。后续可单独审查 overrides、配置与原子发布交易的边界，先以实际冲突／失败复现判断是否继续提炼。成功后的必要清理保持不可取消；搜索允许平台部分失败，其生命周期应独立处理。
