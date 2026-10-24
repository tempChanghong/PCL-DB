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
| Developer Checks | 开发者开关、主题解锁、设置保存和迁移检查 | 当前构建输出目录的 `vs-developer-checks.json` |
| Card Checks | 短/长列表动画、组件摘要淡入/位移、反向切换、卸载恢复、视口及选择保留 | 当前构建输出目录的 `vs-card-checks.json`、`card-motion-plans.json`、`live-card-motion.json`、`component-summary-motion.json` 和检查图像 |
| Navigation Checks | 版本列表/安装选项转场、快速返回、设置/进度往返、卸载及焦点延后恢复 | 当前构建输出目录的 `vs-navigation-checks.json`、`page-switch-motion.json` 和检查图像 |
| Motion Checks | 页面、滚动、标题栏、单选框等控件动画及中断/输入归属检查 | 当前构建输出目录的 `vs-motion-checks.json` 和采样图像 |
| Pack Search Checks | 搜索、分页、项目详情、版本选择、名称确认、安装交接、取消重试和相关动画 | 当前构建输出目录的 `vs-pack-search-checks.json` 和检查图像 |
| Settings Compatibility | 更名后的程序集、旧偏好迁移、保存冲突与配置损坏保护 | 当前构建输出目录的 `vs-compatibility-checks.json`、`settings-compatibility.json` |
| Preview Components | 离屏渲染组件选择页，完成后退出；不会出现交互窗口 | 当前构建输出目录的 `vs-components.png` |

所有检查与预览配置沿用应用已有的预览隔离：不读取或保存用户偏好，不执行真实安装，也不触发 Shell 或剪贴板动作。它们仍会在构建输出目录和系统临时目录写入检查样本。

Debug 构建输出为 `src/PclDownloadBridge.App/bin/Debug/net48/`。启动配置使用 VS 支持的 `$(ProjectDir)` 和 `$(TargetDir)` 宏，工作目录设为对应项目目录，输出目录始终已由构建创建。

## 下载与组件调试配置

右键 **PclDownloadBridge.Tests → 设为启动项目**，然后选择对应配置并按 F5。配置文件为 `tests/PclDownloadBridge.Tests/My Project/launchSettings.json`。

| 配置 | 覆盖的流程 |
|---|---|
| Offline All | 全部本地下载、安装与处理器回归；当前 2175 项检查，处理器样本需要本机 Java 8 或更高版本 |
| Modrinth Import | 128 项 MRPACK 清单、双哈希、多地址/重定向、覆盖层、隔离、路径冲突及取消重试检查；使用回环 HTTP，不连接真实 CDN |
| CurseForge Import | 135 项 ZIP 清单、文件查询/身份/远端校验、可选项、隔离、路径冲突、取消与缓存重试检查；使用回环载荷和受控 HTTP 元数据，不连接真实 CDN |
| Install Inputs | 71 项安装参数检查：组件标量/嵌套 JSON 复制、安装中修改文件名/校验/加载器/版本与来源、一次性或变化的复用目录、损坏源回退、直接组件构造、取消后更新参数重试、已有实例补全及非法参数无副作用；仅使用临时文件、回环 HTTP 与同步进度回调 |
| Isolation Boundaries | 91 项隔离发布边界检查：暂存 Mod 校验/摘要之间的写保护，共享 Mod 发布期读取、改写/删除/重命名和取消，后续步骤失败释放/回收/重试，PCL DisplayType API 分类、优先级、并发变化及已有设置保留；使用临时文件、回环 HTTP 和测试进程内真实 SHA-1 回调，不执行真实 OptiFine 处理器或启动游戏 |
| Isolation Options | 231 项隔离与共享 Mods 检查：三态安装 API、PCL 五种默认策略、PCL-DB 全局默认覆盖与 metadata 分类、现代原版设置发布、已有设置保留、冲突/联接、只读同内容文件、LiteLoader 子目录、OptiFine 同尺寸冲突、分支并行、取消回收与零载荷重试；其中新增全局默认专项 116 项可用 `--regression install-mode-default-strategy` 单独运行。仅使用回环 HTTP、临时文件和测试进程哈希回调，不执行真实 OptiFine 处理器或启动游戏 |
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
# 使用本机 VS 2026 的 MSBuild，检查 Debug/PDB，并运行默认 32 个控制台配置和 9 个 App 检查/预览配置
.\tools\verify-vs2026.ps1

# 改为执行完整控制台回归，再运行同样的界面配置
.\tools\verify-vs2026.ps1 -Full
```

报告写入 `artifacts/vs2026/report.json`，编译和各配置日志在同一目录。脚本读取上面的真实启动配置，并按其工作目录、参数启动 Debug 产物；只执行离线配置。它验证 VS 所用 MSBuild、符号文件和运行入口，**不等于已在 IDE 中按 F5 并命中断点**，这一项保留为人工验收。

当前源码中全量界面配置包含 1014 项检查，Appearance Checks 单独运行其中 191 项，Card Checks 单独运行其中 104 项（含 Forge 新编号与预发布版本的显示、选择和空状态），Navigation Checks 单独运行其中 68 项，Settings Compatibility 单独运行其中 49 项，不能相加为独立用例数。独立 Motion Checks 为 956 项，Pack Search Checks 为 382 项；系统开启或关闭客户端动画时，搜索套件分别检查动态或静态状态，数量相同。当前门槛集中在 `tools/VerificationTools.psm1`，默认构建、VS 与候选验证共用。主题检查不依赖此前显示过任务页；动画检查使用可暂停/定位的实际 WPF 时钟，不以固定睡眠时间判定动画是否完成。失败返回非零退出码，JSON 保留 `Succeeded=false`、`Count`、已经通过的 `Passed`、`FailedCheck` 和异常堆栈；成功回执还核对 Scope、Passed 数量及实际 EXE 摘要。检查不自动重跑，调试时可直接在失败位置设置断点。

Copy Fallback 的文件占用样本使用 Windows NTFS 辅助数据流，默认临时目录需支持 NTFS 数据流。取消样本只在控制台进程内暂时注册受控 SHA-1 提供程序，在真实哈希计算结束处请求取消，并在 Finally 恢复原映射；应用程序与便携包不包含这个提供程序。

当前版本为 Beta 3.0（`3.0.0-beta.1`），包含历史特供版本、统一隔离设置、两平台整合包导入与在线搜索，以及下载界面、返回导航和动画的后续修复；Beta 1.0 / 2.0 的历史标签及工件保留。已有 `.vs` / `.suo` 用户状态继续保留；共享配置使用源码中的启动配置文件。`build.ps1` 默认构建和验证；明确要求打包时才使用 `build.ps1 -Package`，发布另行处理。

单 EXE 验证单独执行 `tools/verify-single-exe.ps1`。该工具以 `PclDbSingleExecutable=true` 构建 App，并使用本次独立的 MSBuild artifacts 路径，不覆盖默认 bin/obj 或现有 VS 输出；候选只含一个 EXE，仍依赖系统 .NET Framework 4.8。它运行 25 项单文件、1014 项界面、956 项动画与 382 项搜索检查，验证资源、字体、文档回退、程序集加载、长路径、回环下载及界面流程。普通 F5 继续使用多文件 Debug 与便携 PDB，不需要手动移动 DLL 或修改启动项目；验证器不会生成发布 ZIP 或更新 dist。

显式 `build.ps1 -Package` 接入上述独立候选，生成单 EXE、九文件 ZIP 和 v3 构建清单。候选报告包含完整 Git 提交、源码摘要、工作区状态、SDK、EXE ProductVersion 及四组完整回执；源码或文档变化后必须重新验证。`-SkipTests -Package` 仍执行 25/1014/956/382 四组候选验收。旧 Beta v2 清单可独立核对历史载荷完整性，不能充当当前候选。普通构建/F5 不增加打包动作，具体资产核对命令见 README。

## 配置依据

- [Microsoft：SDK 项目启动配置与 Visual Basic 的 My Project 路径](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-run#launch-profiles)
- [Microsoft：共享解决方案启动方案 .slnLaunch](https://learn.microsoft.com/en-us/visualstudio/ide/how-to-set-multiple-startup-projects)
- [dotnet/project-system：SDK 格式 VB 项目由新项目系统加载](https://github.com/dotnet/project-system/blob/main/docs/opening-with-new-project-system.md)

这些 `launchSettings.json` 面向 Visual Studio。当前 .NET 10 CLI 不会像 VS 一样展开其中的 MSBuild 路径宏；命令行验证使用附带的脚本，普通构建也可继续运行 `dotnet build PclDownloadBridge.sln -c Debug`。

开发者设置独立配置 **Developer Checks** 使用 `--developer-checks`，包含 36 项设置、主题隔离、迁移与布局检查。它们属于完整界面及 Appearance Checks 的子集，不重复累计。

页面与滚动动画独立配置 **Motion Checks** 使用 `--motion-checks "$(TargetDir)vs-motion-checks.json"`，额外执行 913 项检查，包含实时运行及暂停/定位时钟、快速切页、滚轮叠加、边界截断、输入归属、卡片变换/折叠共存、逐项退出、滚动条模板重建、缩放、像素定位、异步代次、关闭清理与合成触控路由，以及关于页交接、高频滚轮偏移连续性、卡片范围变化和缩放换行的高度衔接。它不包含在原有 999 项静态及操作检查中；`verify-vs2026.ps1` 同时运行并单独记录。输出目录包含三段共 90 帧的 `motion-frames/`、30 帧 `catalogue-motion-frames/` 及 15 帧 `card-refresh-frames/`，可逐帧检查入场、退场和异步衔接，不代表桌面帧率或触控设备实测。普通预览仍关闭这层页面与滚动动画，保持截图确定性。

按钮动效检查另覆盖六种源码曲线、冻结副本、长按微缩、并行回弹、连续重按，以及页面屏蔽输入、隐藏、禁用和卸载后的按压取消；输出 13 帧 `press-motion-frames/`，使用合成鼠标路由事件，不操作真实桌面指针。

列表与顶部选项检查覆盖相同状态不重播、未完成点击的保留、悬停淡入/淡出与延后背景复位、过期时钟和中断清理；额外输出 13 帧 `row-hover-frames/`。当前独立动画共 913 项，完整界面共 999 项；两份报告分别记录。动画另覆盖受限正文的按键、组合文本、焦点请求、跨切页空格释放、恢复编辑和 Escape 返回；滚动条只在入场允许交互后接管键盘操作。使用实际 WPF 预览/冒泡路由和时钟，仍不代表人工桌面输入或设备帧率实测。

选中标记检查覆盖并行弹出曲线、延迟淡出、文字渐变、反向衔接、行高重定向、悬停共存和主题/卸载清理；额外输出 13 帧 `selected-marker-frames/`。原悬停检查改为确认独立选中时钟，不再要求已加载交互行在选中的第一帧立即跳到目标文字颜色，检查数量保持不变。

组件收起刷新检查调用实际列表接受和选择入口，验证旧正文保留显示、旧输入取消、只构建最新响应、提前展开、过期回调、隐藏/卸载和祖先禁用；额外输出 13 帧 `fold-refresh-frames/`，并在 `disabled-retirement.json` 记录恢复后的父级禁用关系。

`build.ps1` 默认验证和单 EXE 候选验证也执行该 913 项验收；单 EXE 候选另有 25 项内容验收、999 项完整界面检查及 359 项搜索检查，候选报告分别记录。普通默认构建不生成正式包或发布版本。

### 局部控件动画定位（当前源码）

Motion Checks 现含 913 项，其中 263 项覆盖标题栏、滚动条本体、个性化单选框与加载动画生命周期。需要只调试这一组时，可将启动参数改为 `--control-motion-checks "$(TargetDir)vs-control-motion-checks.json"`；使用隔离预览与隐藏呈现源，不改用户设置。它包含蓝/深色、快速反向、模板重建、输入与辅助功能门控、系统减少动画分支、源曲线采样和父容器布局计数。加载动画额外模拟本次检查进程的 WPF 偏好缓存及通知，核对运行中关闭/恢复、隐藏、输入门控、反复卸载和静态事件解绑；不更改 Windows 的持久偏好。该组已包含在完整 Motion Checks 内，不单独触发打包或发行。

构建、VS 验证和单 EXE 验证脚本共用 `VerificationProcessTools.psm1`：只清除本次明确要求的旧回执/预览，要求进程成功退出并重新生成非空输出，保留两路日志，核对执行前后 EXE 摘要。App 每个配置限时 180 秒，核心检查限时 900 秒；超时只终止本次创建的进程，不按名称清理其他应用。`verify-vs2026.ps1` 开始时还会移除旧的总报告，失败不会留下上次的成功总报告。以上时限只用于验证脚本，IDE F5 调试及正常下载/安装不增加时限。`tools/verify-verification-process.ps1` 可单独检查这些边界。

原版安装按钮独立动效另含 79 项检查（已计入 Motion Checks），参照 MyExtraTextButton.Show 验证回弹、快速反向、输入门控及页面接管；输出 install-action-090/250/hide-075/dark-250.png 四张采样图。

原版具体版本切换另新增 53 项卡片检查：可见单元错峰、原变换/绑定/输入恢复、快速反向与过期时钟、260 ms 后回弹尾段、浏览滚动和组件请求保留、设置/整合包/进度交接以及隐藏/卸载/缩放。输出六张 `vanilla-card-*.png` / `vanilla-cards-*.png` 与 `vanilla-card-motion-observations.json`。这组包含在 913 项 Motion Checks 内，不另计数、不触发打包。

全量 UI Checks 现为 999 项，其中新增 35 项布局检查（列表度量 15、Minecraft 摘要和侧栏联动 20）。使用最小窗口和长文本、明暗、实际 WPF 时钟及模拟 125% LayoutTransform，输出 row-layout-blue/dark.png、summary-layout-long-light/dark/entry-140.png。聚焦的 Card/Navigation/Appearance 配置数量不变；该组仅纳入完整 UI Checks，不触发打包或真实联网安装。

任务页切换另新增 31 项，包含在 913 项 Motion Checks 内：真实安装/完成/取消处理入口、任务入场与返回错峰、隐藏输入门控、返回中重试的逐像素冻结帧、过期回调、设置交接、尺寸变化及卸载。输出 task-page-entry-exit-70.png、task-page-entry-175.png、task-page-return-exit-70.png、task-page-return-175.png。安装器使用隔离的可控任务，正常 F5 和本组检查均不触发打包；细节见 docs/reviews/task-page-motion.md。

任务返回焦点另新增 24 项，包含在 913 项 Motion Checks 内：明暗/动画与静态模式下，最小窗口中实际滚动 200 DIP，验证交接后的视口焦点、用户主动选择优先、进行中任务返回、设置/过期时钟、新选版本及卸载。输出 task-return-focus-animated.png、task-return-focus-static.png 与 task-return-focus-observations.json。隐藏窗口的逻辑焦点结果不作为真实桌面焦点或自动跳顶实测；详见 docs/reviews/task-return-focus.md。

浮动按钮交接另新增 26 项，包含在 913 项 Motion Checks 内：任务/设置/侧栏返回等待正文入场、按钮原缩放淡入与独立输入门控、主题和正文刷新、快速反向/旧时钟、尺寸变化、静态模式及卸载。输出 install-action-handoff-task-70.png、install-action-handoff-task-230.png 和 install-action-handoff-root-230.png，既有页面退出逐像素检查仍保留。详见 docs/reviews/install-action-handoff.md。

浮动按钮内部按压另新增 63 项，包含在 913 项 Motion Checks 内：独立于外部入场的按压/释放/移出曲线、冻结副本、早期微缩、实际模板、过期时钟、门控/主题/模板/卸载清理、Space 释放、静态模式及原生 Invoke。输出 install-button-held-800.png、install-button-release-150.png。没有操作桌面指针或开始真实安装；详见 docs/reviews/install-button-press.md。

整合包搜索界面另新增 15 项完整滚轮路由检查，包含在 359 项 Pack Search Checks 内：嵌套横向溢出的预览/冒泡归属、内部视觉节点、正反方向/边界、内容与根页面入场、窗口变宽后的竖向恢复及原有项目/任务状态保留。输出 filter-wheel-detail-entry.png、filter-wheel-root-entry.png 与 filter-wheel-nested-routes.json；使用离线版本组夹具和真实 WPF 时钟，不访问远程项目接口。详见 docs/reviews/filter-wheel-routing.md。

整合包详情操作另新增 24 项检查，包含在 359 项 Pack Search Checks 内：复制四次限次/异步等待、浏览器失败回退、已接受的名称/网址、输入门控、旧动作失效、真实卡片入场和设置往返。使用系统操作注入，不改动用户剪贴板或打开浏览器。详见 docs/reviews/project-action-recovery.md。

外部启动器 UI 现有 28 项检查，包含在 1014 项完整界面验收内；Core 专用回归使用 `PclDownloadBridge.Tests.exe --regression external-launchers` 与 `--regression native-isolation`，覆盖目录迁移、三种原生配置分支、组件、两种整合包、补全和取消重试。隔离仅在设置页统一选择。详见 docs/reviews/external-launcher-native-isolation.md；所有安装验证均使用独立夹具目录，未启动真实游戏。
