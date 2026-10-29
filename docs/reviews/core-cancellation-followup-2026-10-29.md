# Core 迁移后的取消收尾与查询重试检查

基线：`675c1f501748a7526977a2caa33a8f352ba3827b`。继续使用 C# Core、VB WPF、VB 测试和 .NET Framework 4.8。本轮不改界面布局、动画、外部启动器配置或下载／安装格式。

## 复现与修复

### 取消通知不能跳过 worker 收尾

`CancellationTokenSource.Cancel()` 同步执行注册回调，默认继续执行其他回调，最后可能抛出 `AggregateException`。原分片调度器在 `Cancel()` 后才等待 worker，回调报错会跳过等待，覆盖已经保存的原始下载故障。隔离探针在 49 ms 返回取消回调异常，此时仍有一个 sender 活跃，直到探针另行等待后才退出。

新增内部 `InstallCleanup.NotifyCancellation`，仅用于六处同伴取消通知和四处响应关闭回调。通知仍同步执行，取消源仍采用默认的 `Cancel()`；通知异常单独作尽力日志记录，不替换原始故障、取消或竞速赢家。日志和 Trace 的异常也各自隔离。原有 `WhenAll`、许可释放与资源所有权顺序不变，读取、校验、提交及普通进度观察者不放进此保护。

分片调度、下载批次和安装批次选择首因异常后，改用 `ExceptionDispatchInfo` 重抛，保留异常实例和最初抛出位置。原先的 `throw failure` 会重置堆栈。

### 响应关闭不能使调用方 Cancel 抛错

Core 原来直接注册 `response.Dispose()`。通过公开目录客户端的 handler 注入一个关闭流后再抛 IO 异常的内容，即可让调用方的 `Cancel()` 收到嵌套聚合异常。该故障注入说明生命周期边界可达，不代表系统默认 HttpClient 内容关闭经常失败。

四处取消关闭回调统一进入上述保护；仍由原有 using 作用域释放响应，不引入后台清理、额外锁或新的 Dispose 所有权。另用私有 net48 子进程检查 `CancelAfter` 从计时器线程触发的相同边界。

### 用户取消与平台查询故障分开

真实回环 HTTP 响应暂停 body，随后取消。在 net48 上，关闭响应使待决读取抛 `IOException`，原目录任务表现为 `Faulted`，尽管调用方 token 已取消。原 VB 和迁移后的 C# 都可复现，因此这是本次复核发现的既有缺陷，不标为 C# 迁移新引入的回归。

`ModpackCatalogClient.FetchTokenAsync` 增加与现有元数据读取一致的调用方取消归一化：仅当调用方 token 已取消时，返回携带原 token、保留原异常的 `OperationCanceledException`。普通 IO、HTTP、非法 JSON 和局部超时仍遵循原有故障／重试规则。

## PCL2 与 PCL-CE 依据

- PCL2 固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`：`ModNet.vb` 的下载线程 Finally 释放请求、响应、流及线程计数；`ModDownload.vb` 的首选源和延迟回退；`ModLoader.vb` 的执行身份与取消状态。目标是旧任务收尾后才交还所有权。
- PCL-CE 固定 `dev` `915ac29795efbdf6373417389e181e1504117048`：`LoaderDownload.cs:64-97` 先记录主故障、仅释放取得的许可，并等待全部 worker；`AdaptiveRangeDownloader.cs` 先记录 `_failure` 再取消同伴。CE 自身也有直接调用 Cancel 的位置，本轮借鉴其故障与收尾结构，没有将其视为取消回调不可能出错的证明。

## 验证

新增两个 VB 模块，原有测试和断言继续保留，默认全量入口追加执行：

- `CancellationCleanupChecks`：15 项。事件门控验证 grow/send 故障、原异常实例与堆栈、取消后必须等待 sender、返回时活跃数为零、缓存独占、相同名称重试、原 token、共享连接／host／transfer 名额释放。相同新测试 EXE 换回旧 Core 时按预期失败。
- `CatalogCancellationChecks`：30 项。Modrinth 和 CurseForge 各自覆盖真实 HTTP body 取消、关闭抛错、原 token、任务取消状态和立即重试；未取消的 IO／JSON 仍为故障，原来源回退和重试次数保持。旧 Core 负控制失败。

复验专项：

```powershell
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression core-cancellation-cleanup
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression catalog-cancellation
```

最终验证以 `artifacts/core-migration-followup/validation.json` 为准，包含准确 DLL／EXE 摘要、7350 项完整 VB 测试、旧 Beta 8 已编译宿主的 7305 项、单 EXE 冷启动与搜索回执，以及提交绑定。套件存在重叠，不累计成独立场景数。

程序集名、公开接口、六个 Module 形状、资源名和资源字节保持。仅新增一个内部取消收尾方法；旧的严格迁移契约工具会报告这一条有意新增的友元声明。验证逐条核对该唯一新增项和其余零差异，不放宽原工具的公开接口或资源检查。

纯逻辑差分探针另检查 3214 组 VB/C# 返回值和异常类型／消息，未发现差异；单 EXE 边界及 Java 子进程生命周期的初步独立探针也未发现新问题。这些初步探针绑定各自的旧构建，不冒充最终候选的运行结果。

## 边界

验证使用本地 HTTP、受控异常、临时文件／缓存、私有子进程和隐藏 WPF；不改真实实例或外部启动器配置，不进行新的完整在线游戏安装或真人帧率验收。本轮只构建验证候选，不打包、不推送、不发版。
