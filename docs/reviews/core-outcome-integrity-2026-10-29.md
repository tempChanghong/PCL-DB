# Core 取消身份与安装失败首因

基线：`c8d8f151d5cc076b2dd754b38df91758dccf797e`。继续检查 Core 迁移后的异常与重试边界；本轮两处问题也存在于原 VB 路径，不认定为 C# 迁移新引入的回归。保留 VB 界面、动画、net48、现有配置、实例隔离与原子发布。

## 已复现的问题

1. CurseForge 文件信息批量查询使用 linked token 并限制四个并发请求。调用方取消后，`WhenAll` 会抛出携带内部 token 的取消异常，跳过方法末尾对原调用方 token 的检查。公开客户端探针以五条必需引用复现四个活动请求和一个排队引用：资源已正确等待，任务为 Canceled，同 resolver 重试成功，但返回的取消 token 与调用方不相等。
2. Minecraft 安装失败后的通知可能再次报错，覆盖真实下载或组件故障。公开 `InstallAsync` 的本地 HTTP 夹具复现：下载策略先抛出原 IOException，同步进度观察者在收到“安装失败”后抛出另一个异常，调用方最终只收到后者。原实现仍然释放文件、保留已有配置并允许重试，不将该问题描述成资源泄漏或实例损坏。

探针与负控制分别保留在 `artifacts/core-cancel-identity/` 和 `artifacts/core-cancellation-identity/`。它们只作为问题证据，最终候选验证另有回执。

## 修复边界

CurseForge 查询在完整 `WhenAll` 结束后检查原调用方 token；若调用方确已取消，则返回原 token 的取消异常，否则原样重抛普通故障。四路并发、排队、来源回退、文件顺序、摘要校验与错误聚合保持。没有提前交还资源，也没有新增后台收尾任务。

安装器仅在操作已经失败的两处出口隔离次要失败通知：下载分支及组件安装分支。保留原异常实例、原抛出堆栈和原调用方取消优先级；诊断 Trace 本身异常也不能改写首因。正常进度回调、ComponentsComplete、IO、校验和发布仍沿原路径执行，未全局吞掉观察者错误。新增方法为 private，不增加公开或友元接口。

## 源码依据

- PCL2 固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`，`Plain Craft Launcher 2/Modules/Base/ModLoader.vb:381` 及 `:549`：先保存 Error，再调整终态、通知／中止子任务，避免后续动作遮蔽父级应读取的故障。
- C# 重点参考 PCL-CE `dev` `915ac29795efbdf6373417389e181e1504117048`，`Modules/Network/Loaders/LoaderDownload.cs:64-97`：各任务记录失败，等待整个任务组，仅释放已进入的信号量；`Modules/Network/Downloader/AdaptiveRangeDownloader.cs:252` 附近：先保存 `_failure`，再完成队列、取消同伴并重抛。
- 这些来源用于核对首因与生命周期顺序，不意味着上游所有通知都具有异常隔离。本轮异步实现继续使用 await／ConfigureAwait(false)，没有照搬同步阻塞，也没有引入 CE 的新运行时依赖。

## 回归与兼容性

新增两个 VB 模块，原测试与断言全部保留：

- `CurseForgeCancellationIdentityChecks`：26 项。预取消、四活动一排队、取消通知、延迟清理 join、原 token、同 resolver 重试、引用顺序／文件名／摘要、精确失败项目与文件 ID、来源回退及普通进度首因。旧 Core 的相同模块在原 token 断言处失败。
- `InstallFailureNotificationChecks`：19 项。通过真实公开安装入口注入下载及 Fabric 组件故障；检查次要通知报错后原异常实例与堆栈、无实例发布、用户配置原字节、文件独占已释放、私有 payload 清理、同名重试，以及通知中发生调用方取消。普通进度首错仍传播，不能变成成功。旧 Core 负控制在原故障身份／堆栈断言处失败。

复验专项可使用本轮构建的 VB 测试 EXE：

```powershell
& 'D:\CodeProjects\Minecraft\PCL-DB\artifacts\core-outcome-integrity\final-build\bin\PclDownloadBridge.Tests\release\PclDownloadBridge.Tests.exe' --regression curseforge-cancellation-identity
& 'D:\CodeProjects\Minecraft\PCL-DB\artifacts\core-outcome-integrity\final-build\bin\PclDownloadBridge.Tests\release\PclDownloadBridge.Tests.exe' --regression install-failure-notification
```

最终验证与提交绑定见 `artifacts/core-outcome-integrity/validation.json`：完整新 VB 宿主 7424 项、原 Beta 8 已编译宿主 7305 项、979 条公开／1244 条友元契约、两份资源和 15 组配置／JSON 样本，以及同一最终单 EXE 的冷启动 25 项和搜索 3240 项。套件存在重叠，不能相加为独立场景数。构建发生在提交前，保存待提交树并与最终 commit tree 核对；不以提交后的重建替代已测字节。

使用受控请求、本地 HTTP、临时实例／文件与隐藏 WPF；不修改真实启动器配置，不作为完整在线游戏安装、实际启动或人工帧率验收。UI、动画与资源文件没有修改，本轮未重复完整 UI／motion 套件。没有打包、推送或发版。

## 后续候选

只读审查另发现搜索会话的公开状态 getter 在外部并发读取时可能枚举正在更新的 Dictionary；受控公开 API 探针已复现。当前 VB UI 正常调用主要受 await 和请求身份门控，尚未证明正常桌面流程会因此崩溃。该项单独保留在 `artifacts/search-session-concurrency/REVIEW.md`，下一轮可评估短锁或不可变状态快照，避免 getter 同步等待异步 gate；本轮没有混入该改动。
