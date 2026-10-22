# 在 Visual Studio 2026 中开发 PCL-DB

在完整源码目录中打开 `PclDownloadBridge.sln`。项目已经是 SDK 格式的 VB.NET / WPF / .NET Framework 4.8，三个项目都可加载；无需从现有代码创建新项目。解决方案中的主应用显示为 **PCL-DB**，源码文件夹和程序集名为 `PclDownloadBridge.App` / `PclDownloadBridge.exe`。便携包附带本说明供查阅。

本机已检测到 Visual Studio Community 2026 18.10.3，安装目录为 `C:\Program Files\Microsoft Visual Studio\18\Community`，并具备 .NET 桌面开发工作负载及 .NET Framework 4.8 目标包。项目通过 `global.json` 使用 .NET SDK 10.0.4xx；这台机器使用 10.0.401。项目路径与启动配置没有写死这台机器的目录。

## 打开与 F5

1. 在 VS 中选择「文件 → 打开 → 项目/解决方案」，打开 `PclDownloadBridge.sln`，等待 NuGet 还原完成。
2. 工具栏选择 **Debug / Any CPU**。
3. 右键 **PCL-DB → 设为启动项目**，启动配置选择 **PCL-DB**，按 **F5**；**Ctrl+F5** 为不附加调试器运行。
4. 若此前打开过同名解决方案，VS 可能保留旧的启动选择，按第 3 步重新指定即可，无需删除 `.vs`。

另附 `PclDownloadBridge.slnLaunch`，提供 **PCL-DB** 和 **Offline Checks** 两种可共享的解决方案启动方案，分别只启动主应用或控制台回归项目。若 VS 没有显示这些方案，可在「配置启动项目」中选择；部分安装需在「工具 → 选项 → 预览功能」启用多项目启动配置。普通的单启动项目方式仍可直接使用。

正常的 **PCL-DB** 配置读取现有用户偏好和真实版本列表；点击开始下载会写入所选游戏目录。调试下载引擎时，可先使用下面的本地回归配置。

## 主应用调试配置

配置文件：`src/PclDownloadBridge.App/My Project/launchSettings.json`。右键 PCL-DB 的调试属性可编辑这些配置。

| 配置 | 行为 | 输出位置 |
|---|---|---|
| PCL-DB | 正常启动可操作的主窗口，可在启动、交互和下载流程中断点 | 使用应用原有设置和日志路径 |
| UI Checks | 用示例数据执行全部界面检查，完成后退出 | 当前构建输出目录的 `vs-ui-checks.json` 和检查图像 |
| Appearance Checks | 单独检查主题切换、任务页首次显示以及任务显示/隐藏和终止状态 | 当前构建输出目录的 `vs-appearance-checks.json`、`appearance-task-states.json` 和 `appearance-revealed.png` |
| Card Checks | 短/长列表动画、组件摘要淡入/位移、反向切换、卸载恢复、视口及选择保留 | 当前构建输出目录的 `vs-card-checks.json`、`card-motion-plans.json`、`live-card-motion.json`、`component-summary-motion.json` 和检查图像 |
| Navigation Checks | 版本列表/安装选项转场、快速返回、设置/进度往返、卸载及焦点延后恢复 | 当前构建输出目录的 `vs-navigation-checks.json`、`page-switch-motion.json` 和检查图像 |
| Settings Compatibility | 更名后的程序集、旧偏好迁移、保存冲突与配置损坏保护 | 当前构建输出目录的 `vs-compatibility-checks.json`、`settings-compatibility.json` |
| Preview Components | 离屏渲染组件选择页，完成后退出；不会出现交互窗口 | 当前构建输出目录的 `vs-components.png` |

所有检查与预览配置沿用应用已有的预览隔离：不读取或保存用户偏好，不执行真实安装，也不触发 Shell 或剪贴板动作。它们仍会在构建输出目录和系统临时目录写入检查样本。

Debug 构建输出为 `src/PclDownloadBridge.App/bin/Debug/net48/`。启动配置使用 VS 支持的 `$(ProjectDir)` 和 `$(TargetDir)` 宏，工作目录设为对应项目目录，输出目录始终已由构建创建。

## 下载与组件调试配置

右键 **PclDownloadBridge.Tests → 设为启动项目**，然后选择对应配置并按 F5。配置文件为 `tests/PclDownloadBridge.Tests/My Project/launchSettings.json`。

| 配置 | 覆盖的流程 |
|---|---|
| Offline All | 全部本地下载、安装与处理器回归；当前 1566 项检查，处理器样本需要本机 Java 8 或更高版本 |
| Isolation Options | 115 项三态隔离与共享 Mods 检查：五种全局策略与组件状态、显式覆盖、已有设置保留、冲突/联接、只读同内容文件、LiteLoader 子目录、OptiFine 同尺寸冲突、分支并行、取消回收与零载荷重试；仅使用回环 HTTP、临时文件和测试进程哈希回调，不执行真实 OptiFine 处理器或启动游戏 |
| Isolation Resources | 177 项旧资源与隔离检查：五种全局策略、实例设置/旧设置迁移、只读/BOM 配置、修复与设置变化、共享冲突回收、路径/联接、组件文件目录限制、并行取消、原子发布和重试；仅使用回环 HTTP、临时文件及测试进程哈希回调 |
| Repair Metadata | 36 项校验补全检查：提交前 JSON 改写/删除/写占用、同长度和格式变化、UTF-8 BOM/UTF-16/只读 JSON、提交期间读写共享、取消及保留客户端缓存后重试；只使用回环 HTTP、临时文件和测试进程内哈希回调 |
| Cache Boundaries | 30 项缓存复用检查：目标占用/目录冲突及解除后重试、损坏/锁定缓存回退到下一缓存或 HTTP、源及副本校验后取消与重试；使用真实临时文件锁、回环 HTTP 和测试进程内哈希回调，不操作用户目录 |
| Segment Responses | 33 项大文件分段响应检查：错误长度/单位/缺失区间回退、有效响应及无长度正文、保留分片后的取消/重试、坏哈希拒绝及实际客户端安装；可用 `--regression single-resume-segment-all` 单独运行，只使用回环 HTTP 与临时目录 |
| Asset Objects | 31 项资源对象检查：大写摘要归一、三种来源偏好及坏哈希回退、实际安装/修复、旧版映射、跨目录复用与损坏回退、严格字段/整数边界、下载前拒绝异常索引；只使用回环 HTTP 与临时目录 |
| Supplemental Versions | 111 项补充版本检查：20 个目标身份、显式别名、分类信息、重复冲突、官方优先、来源失败/超时/取消及编码恢复、实际安装与校验补全、错误元数据和文件拒绝；另含 21 项资源索引隔离、共享、旧实例兼容、取消/失败后重试检查，可用 `--regression supplemental-indexes` 单独运行；只使用回环 HTTP 与临时目录 |
| Metadata Transfers | 45 项元数据响应、大小上限、JSON 对象类型、备用源、哈希及取消回归，含 19 项 UTF-8 BOM、原始字节摘要、主清单选源、实际安装与修复检查；可用 `--regression metadata-transfer-bom` 单独运行新增组，仅使用本地 HTTP 与临时目录 |
| Component Catalogues | 234 项 Fabric / Fabric API / OptiFine / OptiFabric / LiteLoader 检查，覆盖兼容性、主文件/附件选择、发布时间、实际安装、SHA-1/MD5 校验与缓存、OptiFine 文件大小与损坏回退、合法空列表、损坏条目、重复分页、源偏好及取消；仅使用本地 HTTP 样本 |
| Processor Log Recovery | 12 项真实 Java 样本检查：日志只读/占用、输出进度、失败原因、取消、恢复后重试；不使用真实游戏目录 |
| Processor Streams | 22 项进程输出检查：双流收尾、末尾无换行、错误码、继承管道、取消、输出收尾超时和同名安装重试；用临时托管进程模拟 Java，不执行真实上游安装器 |
| Java Runtime Probe | 20 项 Java 检测检查：真实 JVM 长提示，以及模拟子进程的版本格式、双流大输出、错误码和超时；仅改测试进程的临时环境变量 |
| Folder Discovery | 14 项 PCL 目录读取检查：自定义名称/别名去重、盘符根路径、无效或占用的当前目录配置、空列表与后续有效条目；仅写临时样本，盘符根路径只读检查 |
| NeoForge Versions | 14 项 NeoForge 列表解析检查：经典/新编号、alpha/beta、快照、愚人节、旧 Maven 坐标及 BMCLAPI 包装数据；仅连接本机回环服务器 |
| Component Versions | 21 项版本排序及兼容边界检查：pre/rc/snapshot 的数字比较、Fabric/OptiFine 组合、Forge 排除区间及 OptiFabric 元数据请求；仅使用临时回环列表 |
| Forge Versions | 18 项 Forge 版本与安装器检查：官方/BMCLAPI 分支归一化、历史补正、校验字段、原版预发布坐标及实际本地安装；仅使用回环元数据和合成旧版安装器 |
| Forge Checksums | 22 项所选 Forge 安装器校验检查：SHA1/MD5、文件选源、坏响应回退、校验门控、取消、错误元数据及不匹配文件拒绝；仅使用回环端点和合成安装器 |
| NeoForge Checksums | 17 项所选 NeoForge 安装器校验检查：SHA1/MD5、文件选源、回退、取消、校验门控、不匹配文件及 1.20.1 旧 Maven 坐标；仅使用回环端点和合成安装器 |
| Component Dependencies | 24 项 Forge / NeoForge 依赖清单检查：重复校验合并、错误文件拒绝、内嵌文件、冲突提前停止、来源保持及重复安装缓存；回环 HTTP 和真实 Java 样本，不执行真实上游安装器 |
| Download Plan | 重复文件的校验合并、同大小损坏文件修复 |
| Native Libraries | 实际安装器中的普通/原生库合并、文件复用和最终配置写入 |
| Cleanup Cancellation | 占用暂存客户端后取消安装、保留取消状态、解除占用后重试 |
| Final Commit | 原版和组件安装在最终写入通知处取消、确认未发布，并复用已校验文件重试 |
| Transfer Completion | 单连接/分段下载完成后的分片占用、清理日志不可写、真实哈希错误和目标写入冲突；另含 19 项完整失败日志、并发追加、日志只读/占用、取消与重试检查 |
| PCL File Reuse | 跨目录复用的声明要求、旧版本内不同文件名的客户端、损坏回退和实际安装 |
| Copy Fallback | NTFS 辅助流占用导致复制失败后的本地/HTTP 回退，哈希校验结束处取消，以及真实目标写入错误 |
| Library Metadata | Maven 路径/仓库回退、空大小、PCL 扩展名路径与架构字段、实际安装/补全及第二实例复用 |
| Library Selection | 重复库的版本选择、规则优先、普通/原生库独立解析、同目标校验合并及真实安装 |

这些配置使用回环 HTTP 和模拟元数据，游戏输出位于系统临时目录 `PclDownloadBridgeTests-<随机 ID>`，不使用用户的 `.minecraft`。测试项目是现有的控制台检查程序；从启动配置运行，`dotnet test` / VS 测试资源管理器不会自动执行这些控制台检查。

如需调试其他单项，在测试项目调试属性中将参数改为 `--regression <已有用例名>`，可从各 `*Checks.vb` 的 `RunAsync` 查看名称。保留离线配置作为默认，不在共享配置里填写真实游戏路径或自动运行 `--live`。

## 建议的断点与异常设置

| 目标 | 断点入口 |
|---|---|
| 设置迁移与冲突 | `src/PclDownloadBridge.App/DownloadSettingsFile.vb`：`LoadCompatible` |
| 页面切换、旧时钟与输入门控 | `src/PclDownloadBridge.App/Controls/PclPageSwitch.vb`：`SelectInstall`、`Finish` |
| 启动、预览参数 | `src/PclDownloadBridge.App/App.xaml.vb`：`OnStartup` |
| 点击开始下载 | `src/PclDownloadBridge.App/MainWindow.xaml.vb`：`StartInstall` |
| 安装分支、最终写入 | `src/PclDownloadBridge.Core/MinecraftInstaller.vb`：`InstallAsync` |
| 计划、复用、重试与分段 | `src/PclDownloadBridge.Core/DownloadEngine.vb`：`DownloadAsync`、`DownloadOneExclusiveAsync` |
| 自动选源与列表 | `src/PclDownloadBridge.Core/SourcePolicy.vb`：`GetVersionManifestAsync` |
| 加载器配置与 Java | `src/PclDownloadBridge.Core/ComponentInstaller.vb`：`MergeProfile`、`ApplyAsync`、`JavaRuntime.RunAsync` |
| Java 版本检测 | `src/PclDownloadBridge.Core/ComponentInstaller.vb`：`JavaRuntime.Major`、`ReadMajorAsync`、`ParseMajor` |
| 处理器日志不可写 | `src/PclDownloadBridge.Core/ComponentInstaller.vb`：`ProcessorLine`；预期的 IO/访问异常不会阻止安装及进度 |
| 完整失败诊断 | `src/PclDownloadBridge.Core/InstallLog.vb`：`Failure` / `Append`；汇总异常在展开前记录，日志不可写不改变返回的具体异常 |
| 任务结果路径变化 | `src/PclDownloadBridge.App/MainWindow.TaskResults.vb`：`TaskLogPath` / `TaskFolderPath`；UI Checks 用隔离 NTFS 目录联接检查路径失效、恢复及未结束任务，不实际打开 Shell |
| 清理异常与日志 | `src/PclDownloadBridge.Core/InstallCleanup.vb`：`DeleteOwnedDirectory` |
| 下载成功后的分片清理 | `src/PclDownloadBridge.Core/TransferCleanup.vb`：`DeleteCommitted` |

F9 切换断点，F10 逐过程，F11 逐语句。`Directory.Build.props` 明确为 Debug 启用 Portable PDB 并关闭优化，三个项目都会生成对应 `.pdb`；本机 CLI 构建已经核对这些实际设置。断点灰色时先确认当前是 Debug，并在「调试 → 窗口 → 模块」检查加载路径和符号，避免误运行 `dist` 中的 Release 程序。

默认使用托管调试，启动配置关闭本机代码调试。需要查看被捕获的校验错误时，可在「调试 → 窗口 → 异常设置」（Ctrl+Alt+E）中搜索 `System.IO.InvalidDataException` / `System.IO.IOException`，临时勾选“引发时中断”。下载器会捕获部分异常进行重试，取消也会使用 `OperationCanceledException`，因此日常调试可保持 VS 默认的未处理异常设置。

## 本机复查

在项目根目录执行：

```powershell
# 使用本机 VS 2026 的 MSBuild，检查 Debug/PDB，并运行九个短回归及界面配置
.\tools\verify-vs2026.ps1

# 改为执行完整控制台回归，再运行同样的界面配置
.\tools\verify-vs2026.ps1 -Full
```

报告写入 `artifacts/vs2026/report.json`，编译和各配置日志在同一目录。脚本读取上面的真实启动配置，并按其工作目录、参数启动 Debug 产物；只执行离线配置。它验证 VS 所用 MSBuild、符号文件和运行入口，**不等于已在 IDE 中按 F5 并命中断点**，这一项保留为人工验收。

当前源码中全量界面配置包含 668 项检查，Appearance Checks 单独运行其中 129 项，Card Checks 单独运行其中 104 项（含 Forge 新编号与预发布版本的显示、选择和空状态），Navigation Checks 单独运行其中 31 项，Settings Compatibility 单独运行其中 49 项，不能相加为独立用例数。独立 Motion Checks 包含 328 项。主题检查不依赖此前显示过任务页；动画检查使用可暂停/定位的实际 WPF 时钟，不以固定睡眠时间判定动画是否完成。失败仍返回非零退出码，JSON 保留 `Succeeded=false`、`Count`、已经通过的 `Passed`、`FailedCheck` 和异常堆栈。检查不自动重跑，调试时可直接在失败位置设置断点。

Copy Fallback 的文件占用样本使用 Windows NTFS 辅助数据流，默认临时目录需支持 NTFS 数据流。取消样本只在控制台进程内暂时注册受控 SHA-1 提供程序，在真实哈希计算结束处请求取消，并在 Finally 恢复原映射；应用程序与便携包不包含这个提供程序。

当前源码基于 Beta 1.0（`1.0.0-beta.1`），另含尚未发布的标题按压交接、取消回调状态、元数据类型错误及组件列表字段损坏时的选源回退修复；已发布标签及工件保持原样。已有 `.vs` / `.suo` 用户状态继续保留；共享配置使用源码中的启动配置文件。`build.ps1` 默认构建和验证；明确要求打包时才使用 `build.ps1 -Package`，发布另行处理。

单 EXE 验证单独执行 `tools/verify-single-exe.ps1`。该工具以 `PclDbSingleExecutable=true` 构建 App，并使用本次独立的 MSBuild artifacts 路径，不覆盖默认 bin/obj 或现有 VS 输出；候选只含一个 EXE，仍依赖系统 .NET Framework 4.8。它运行 25 项单文件检查及 668 项预览界面检查，验证实际资源和字体加载、文档回退、程序集身份与并发加载、长路径及回环下载。普通 F5 继续使用多文件 Debug 与便携 PDB，不需要手动移动 DLL 或修改启动项目；验证器不会生成发布 ZIP 或更新 dist。

显式 `build.ps1 -Package` 现已接入上述独立候选，生成单 EXE、九文件 ZIP 和构建清单。候选报告包含完整 Git 提交、源码摘要、工作区改动状态、SDK 及 EXE ProductVersion 校验；源码或文档变化后必须重新验证。`-SkipTests -Package` 仍执行 25/668/328 项单文件、界面及动画候选验收。v2 清单保留 Beta 发布标识和三组验收结果。普通构建/F5 不增加打包动作，具体资产核对命令见 README。

## 配置依据

- [Microsoft：SDK 项目启动配置与 Visual Basic 的 My Project 路径](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-run#launch-profiles)
- [Microsoft：共享解决方案启动方案 .slnLaunch](https://learn.microsoft.com/en-us/visualstudio/ide/how-to-set-multiple-startup-projects)
- [dotnet/project-system：SDK 格式 VB 项目由新项目系统加载](https://github.com/dotnet/project-system/blob/main/docs/opening-with-new-project-system.md)

这些 `launchSettings.json` 面向 Visual Studio。当前 .NET 10 CLI 不会像 VS 一样展开其中的 MSBuild 路径宏；命令行验证使用附带的脚本，普通构建也可继续运行 `dotnet build PclDownloadBridge.sln -c Debug`。

开发者设置独立配置 **Developer Checks** 使用 `--developer-checks`，包含 36 项设置、主题隔离、迁移与布局检查。它们属于完整界面及 Appearance Checks 的子集，不重复累计。

页面与滚动动画独立配置 **Motion Checks** 使用 `--motion-checks "$(TargetDir)vs-motion-checks.json"`，额外执行 328 项检查，包含实时运行及暂停/定位时钟、快速切页、滚轮叠加、边界截断、输入归属、卡片变换/折叠共存、逐项退出、滚动条模板重建、缩放、像素定位、异步代次、关闭清理与合成触控路由，以及关于页交接、高频滚轮偏移连续性、卡片范围变化和缩放换行的高度衔接。它不包含在原有 668 项静态及操作检查中；`verify-vs2026.ps1` 同时运行并单独记录。输出目录包含三段共 90 帧的 `motion-frames/`、30 帧 `catalogue-motion-frames/` 及 15 帧 `card-refresh-frames/`，可逐帧检查入场、退场和异步衔接，不代表桌面帧率或触控设备实测。普通预览仍关闭这层页面与滚动动画，保持截图确定性。

按钮动效检查另覆盖六种源码曲线、冻结副本、长按微缩、并行回弹、连续重按，以及页面屏蔽输入、隐藏、禁用和卸载后的按压取消；输出 13 帧 `press-motion-frames/`，使用合成鼠标路由事件，不操作真实桌面指针。

列表与顶部选项检查覆盖相同状态不重播、未完成点击的保留、悬停淡入/淡出与延后背景复位、过期时钟和中断清理；额外输出 13 帧 `row-hover-frames/`。328 项为当前动画总数，668 项完整界面和各界面子集计数不变。

选中标记检查覆盖并行弹出曲线、延迟淡出、文字渐变、反向衔接、行高重定向、悬停共存和主题/卸载清理；额外输出 13 帧 `selected-marker-frames/`。原悬停检查改为确认独立选中时钟，不再要求已加载交互行在选中的第一帧立即跳到目标文字颜色，检查数量保持不变。

组件收起刷新检查调用实际列表接受和选择入口，验证旧正文保留显示、旧输入取消、只构建最新响应、提前展开、过期回调、隐藏/卸载和祖先禁用；额外输出 13 帧 `fold-refresh-frames/`，并在 `disabled-retirement.json` 记录恢复后的父级禁用关系。

`build.ps1` 默认验证和单 EXE 候选验证也执行该 328 项验收；单 EXE 的 25 项内容验收和 668 项完整界面检查保持原计数，候选报告另记 MotionChecks。本轮不生成正式包或发布版本。
