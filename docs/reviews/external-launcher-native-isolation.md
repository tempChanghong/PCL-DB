# 外部启动器原生隔离与实例配置：第二批

基线 `c06899da0cfe7d0291c1a11ce87da10140e4fc6f`。本批开放 HMCL / PCL-CE 的组件、新建整合包及同版本原版补全，保持设置页统一隔离选项和已有页面动画。只在新实例的私有暂存区生成配置，外部全局配置与已有实例配置只读。不打包、推送或发版。

## 源码依据

| 来源 | 固定提交 | 核对内容 |
| --- | --- | --- |
| PCL | `5ef81e490ae46b1100d61e33d0183094c7a80ca8` | ModMinecraft.PathIndie、ModDownload 安装分支、ModModpack 的隔离导入与覆盖文件；保留已有固定快照和共享文件事务 |
| HMCL | `0d20910ea302a907d6a01fec9244ab10d2993d5e` | HMCLGameRepository.computeRunDirectory / shouldIsolateNewInstance、HMCLGameInstance.isModpack、仓库布局、GameSettings、SettingsManager、GameDirectory、LegacyGameSettingsMigrator、DownloadPage |
| PCL-CE | `915ac29795efbdf6373417389e181e1504117048` | McInstance.PathIndie、Config 中 Local / Instance 隔离键、ConfigService、YamlFileProvider 的 JSON / INI 回退 |

HMCL / CE 源文件副本、Git blob 标识、SHA-256 和工作区状态在 `artifacts/native-isolation/source.json` 与 `source/`。PCL 使用已固定的 `reference/PCL` 清单；三个相邻仓库没有业务修改。适配使用本项目 VB 实现，不调用上游会迁移或保存配置的服务。

## 行为与配置

- 默认隔离仍只在设置中统一选择。“跟随所选启动器”分别读取该绑定的原生策略；桥接程序的显式全局偏好只作用于新实例，补全保留实例原值。整合包强制使用新的隔离实例。
- PCL 保持原有 INI、默认策略和资源/Mods 事务。普通现代原版没有新增强制配置写入。
- CE 依次读取实例 YAML、同名 JSON、旧 Setup.ini；再考虑 V1、Mods / saves 推断与全局策略。新实例写 YAML 和兼容 INI；整合包已有 JSON 且没有 YAML 时在 JSON 内合并，避免新 YAML 遮蔽其他有效设置。仅替换隔离键，其他值保留。
- 新 HMCL 使用实例 `.hmcl/config/instance-game-settings.json`。`runningDirectory` 的本地覆盖标记与值共同决定目录：显式空值为实例目录；没有覆盖时继承预设，预设空值为游戏根目录。新安装的 NEVER / MODDED / ALWAYS 不直接套用到已有实例。
- 旧 HMCL 读取所选根目录对应的 profile/global 及 `hmclversion.cfg`，识别 usesGlobal、ROOT_FOLDER / VERSION_FOLDER / CUSTOM；新实例保留该全局对象的其他设置，只调整隔离字段。不会改写旧实例或自行迁移为新版格式。
- 新 HMCL 读取旧实例时考虑目录的 legacyGameSettings、历史预设是否有效和旧本地设置。`modpack.cfg` 按原生规则强制隔离。存在旧迁移回执却缺少新版实例配置时中止补全，提示先在 HMCL 保存设置；不猜测迁移结果。
- HMCL 单个 `game` patch 的原版清单可进行同版本补全。比较时读取该 patch，发布时保留原 JSON 字节；加载器、多 patch、继承关系或修改过的库/资源声明仍拒绝。

安装目标快照包含类型、根目录、工作目录、绑定 ID 和显式本地/共享配置路径。目录相同也保留启动器身份；开始下载后更换界面对象不会重定向任务。HMCL 旧 profile 同根却有不同配置、新版预设 ID 重复或历史来源不唯一时明确失败。

## 发布、冲突与重试

原版、组件、两种本地整合包和在线搜索取得的归档统一传递原生目标，复用同一校验下载及原子安装内核。共享 libraries/assets 和非隔离 Mods 继续使用游戏根目录锁、摘要校验与冲突拒绝。同名不同内容的 Mods 不覆盖；旧资源只发布到解析后的运行目录。

原生配置在组件和整合包 overrides 完成后写入暂存区。若覆盖了包内原生设置，更新覆盖清单的哈希，再进行最终校验。远程文件列表不得直接下载实例配置；生成路径和所有父路径一并检查文件/目录冲突。

读取的配置保存字节快照，缺失文件同样记录；发布时重新比对并持有只读句柄，阻止同时改写。再次检查缺失状态和 CE 的目录推断信号。新配置在最终重命名前比对暂存字节；不跨 Windows 目录重命名持有暂存文件句柄。取消或校验失败清理私有载荷并撤销本任务已发布的共享资源/Mods，已校验缓存供重试复用。

## 验证

新增 `PclDownloadBridge.Tests.exe --regression native-isolation`：覆盖三种配置分支的新建/组件/补全、隔离/共享、只读文件、未知设置保留、取消重试、共享 Mods 冲突、发布前配置变化、缺失配置后来出现、输入句柄锁、暂存配置篡改、CE 回退与不支持的 YAML、HMCL patch/继承及两平台在线归档交接。使用回环 HTTP 和受控文件夹，没有执行真实游戏。

完整结果及可执行文件摘要以 `artifacts/native-isolation/validation.json` 为准，Commit 绑定另见 `committed-validation.json`。本轮同时回归 PCL 隔离/资源、导入、目录迁移，运行完整 UI、动画、搜索、固定源码/资源和验证工具契约。当前策略 `PCL-DB-checks/2026-10-24.30` 要求 UI 1001、motion 913、pack-search 359；其中外部启动器 UI 28 项。

保留一份界面夹具失败报告：将 HMCL 绑定改为显式配置后，旧“第二份绑定”夹具实际变成同一来源，被正确去重。改回独立的自动发现绑定，仍验证多份位置和配置快照，未放宽断言或修改业务去重逻辑。

## 边界与后续

- 自定义运行目录若既不是游戏根目录也不是该实例目录，可保留其设置并补全不需要运行目录写入的现代原版；涉及 Mods 或旧 resources 写入时拒绝跨目录发布。支持任意外部运行目录需要独立设计多目录锁与回滚事务。
- 补全范围仍为同版本独立原版，不改造已有加载器或替换整合包组件；可安装的新组件范围沿用当前安装器。
- CE YAML 采用有界的目标标量读取，普通顶层键以外的隔离键写法、重复键和不支持的构造明确失败，不静默切换资源目录。它不是通用 YAML 编辑器。
- 这些是自动化文件/回环安装和 WPF 页面时钟证据，不能替代三种启动器实际启动 Minecraft、所有历史发行格式或桌面帧率的人工验收。下一步适合在用户副本目录中做真实启动器验收，再决定是否扩展自定义运行目录事务。
