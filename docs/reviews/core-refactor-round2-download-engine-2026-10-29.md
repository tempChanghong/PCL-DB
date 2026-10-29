# 第二轮重构第三批：下载引擎职责拆分

基线：`b894a2a30ab462974e71a5426f1ca7632d560c16`。将约 700 行的 DownloadEngine 按批次／状态、单文件事务、HTTP 传输拆为三个文件。仍为原 public 非 sealed IDisposable 类型，只增加 partial；没有新增服务、调度层或算法。

| 文件 | 职责 |
| --- | --- |
| `DownloadEngine.cs` | 全部字段、属性、初始化表达式、ResponseState 嵌套类型、构造器、缓存启用、批次／worker、进度报告及 Dispose。 |
| `DownloadEngine.Files.cs` | 单文件缓存独占、复用、校验与原子发布、来源重试、分段／单流选择、临时复制清理、续传条件。 |
| `DownloadEngine.Transport.cs` | 单流／Range 请求、分段调度适配、HTTP response 与 deadline、流复制、字节计数、带宽等待，以及原私有 SlowSourceException。 |

## 需要保持的边界

四个 AsyncLocal、HTTP clients、时钟、报告锁、统计对象和属性初值全部留在主文件，原顺序保持。Files 与 Transport 仍直接调用同一对象的方法，没有新增 Task.Run、异步包装或共享静态状态。ResponseState 的 previous 保存／恢复和内部时钟保持原样。

文件传输缓存的独占锁持续到校验、发布和清理结束；它不是网络连接许可。主机、总连接、镜像节流的取得与 finally 释放、活动计数、响应／取消注册／deadline 嵌套作用域完整搬迁。带宽等待仍暂挂无活动 deadline，避免将限速误判为网络停顿。来源顺序、慢源阈值、Range 回退、续传资格、摘要要求和统计时机均保持。

共享 DownloadBudget 的所有权留在主文件：批次 BeginBatch／EndBatch 配对、全部 worker join、原调用方取消身份及首个非取消错误保留；Dispose 顺序和仅释放自有预算的条件不变。SlowSourceException 仍为同一 DownloadEngine 的 private 嵌套类型，文件层的异常判断绑定不变。

## 固定源码依据

以 PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 为核心：`Plain Craft Launcher 2/Modules/Base/ModNet.vb:579,902,1163,1311` 分别承担单文件状态、传输、合并校验和批次；`:1474-1515` 的本地复用要求及 `ModDownload.vb:1219-1242` 的来源选择规则继续由现有实现保留。

C# 重点参考 PCL-CE `dev` `915ac29795efbdf6373417389e181e1504117048` 的 `Modules/Network/Loaders/LoaderDownload.cs:53-103`、`Downloader/FileDownloader.cs:45-118,154-178,254-268,314-330`、`AdaptiveRangeDownloader.cs:46-124` 与 `DownloadResourceManager.cs:49-71,105,249-267`。借鉴批次、文件事务、请求和配额的所有权边界，不引入现代 I/O、Channels 或全局资源管理器。精确审查见 `artifacts/core-refactor-round2/download-engine-boundary-review.md`。

## 等价性与验证

独立 Roslyn C# 9 探针从固定 Git 原文件读取基线，与三个候选文件比较：3 个类型、62 个手写成员、24 条私有依赖，零差异。只允许原 DownloadEngine 增加 partial；严格比较原类型／成员归属、签名／默认值、完整 token 方法体、10 项主类型初始化顺序及状态位置。ResponseState 与 SlowSourceException 完整声明保持。七个负控制全部检出，证明／工具在 `artifacts/download-engine-refactor-review/`，根验证另存 `artifacts/download-engine-refactor/member-equivalence/`。

第二份原文级只读复核确认主状态与批次、Files、Transport、报告与 Dispose 各完整代码块保持原文，using 均为原导入子集。未改原 VB 测试、App 界面或动画、项目／程序集／资源与配置格式。原始 DLL、PDB、MVID 或 metadata token 可随编译单元变化，不能代替行为／接口验证。

初步取消收尾 15、传输清理 43、共享预算 7、缓存别名 4 项通过。完整 VB 7468、原 Beta 8 已编译宿主 7305、公开／友元契约、资源／JSON、VS 2026 Debug 与 Portable PDB、最终单 EXE 冷启动和搜索，以及提交树绑定，统一见 `artifacts/download-engine-refactor/validation.json`。各套件重叠，不累加为独立场景数。

所有构建与 Core 验证结束后，再串行执行 WPF 单 EXE 检查；不改动画断言或超时，不把该安排当作更早动画复合断言失败原因已解决的证明。受控 HTTP、临时文件、模拟进程与隐藏 WPF 不代表完整在线安装、游戏启动、人工 F5 或帧率验收。未打包、推送或发版。
