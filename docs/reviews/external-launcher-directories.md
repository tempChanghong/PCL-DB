# 外部启动器目录接入：第一批

基线 `fe6af17`。本批交付独立设置卡片、PCL / HMCL / PCL-CE 目录读取、多份绑定、旧偏好迁移与任务目标快照。外部配置只读；验证后本地 Commit，不打包或发版。

## 源码依据

| 来源 | 本地提交 | 读取的主要实现 |
| --- | --- | --- |
| PCL | `5ef81e490ae46b1100d61e33d0183094c7a80ca8` | ModMinecraft.McFolderListLoadSub、Settings.vb，既有固定快照与 PclFolders 适配 |
| HMCL | `0d20910ea302a907d6a01fec9244ab10d2993d5e` | Metadata、SettingsManager、GameDirectoryManager、GameDirectory / GameDirectories、LegacyConfigMigrator、PortablePath、JsonSchema、DefaultGameRepositoryLayout |
| PCL-CE | `915ac29795efbdf6373417389e181e1504117048` | Paths、States、ConfigService、JsonFileProvider / YamlFileProvider、ModFolder、McInstance |

以上是本地源码基线，不能据此宣称所有公开发行版本均已验证。规范化源码、SHA-256 及相邻仓库状态存于 `artifacts/external-launchers/source/`、`source.json`。没有修改三个启动器仓库。

## 目录与偏好

设置新增“外部启动器”卡片，三行分别提供多份位置选择、“选择位置”“配置文件”“读取目录”。位置指启动器工作目录；HMCL 相对路径按此解析。配置文件可显式绑定本地或共享文件，适用于自定义配置位置。游戏卡片仍统一选择目标，目录名称显示启动器来源。

- PCL：保留当前 Setup.ini、注册表及自定义路径规则；显式绑定 INI 时在同一工作目录上下文中读取。
- HMCL：优先新 `.hmcl/config/game-directories.json`，兼容旧 `.hmcl/hmcl.json`、`hmcl.json`、`.hmcl.json` 中 `configurations.*.gameDir`；旧选择字段为 `last`。新格式合并用户目录文件，工作目录的 `launcher-settings.json` 决定当前目录 ID，本地目录 ID 优先于用户文件的同 ID 条目。
- PCL-CE：优先本地 `PCL/config.v1.yml`，兼容同名 JSON 与旧 INI；从共享 `config.v1.json` / 旧 Config.json 读取 LaunchFolders。本地选择与共享目录分开读取。

新 HMCL 配置只接受已知的目录 schema 主版本 1；CE 识别本地/共享文件版本 1。文件读取限 2 MiB，HMCL/CE 目录列表限 2048 项。CE YAML 支持目标键的普通、单引号和 JSON 兼容双引号标量；重复键、多行、锚点/别名和不支持的写法返回说明，保留既有目标，并允许手动选目录。适配器不调用上游会创建、迁移或保存配置的存储类。

PCL-DB 自身增加 `externalLaunchers` 版本 1 偏好，记录绑定、各行选择及目标目录来源。旧 `pclBase` + `gameRoot` 迁移为 PCL 绑定；兼容键继续保存。未知成员、其他窗口新加的绑定和无法识别的记录保留；未知偏好格式不自动猜测目标或覆写原值。

同一个物理目录可以对应多份启动器绑定。去重只在同一读取上下文内进行，任务身份包含绑定 ID、启动器类型、工作目录和根路径。刷新失败、过期响应、切换绑定或页面、进入下载及关闭后，旧读取不能替换已接受目标。下载时禁用两张目录相关卡片，事件处理器也拒绝排队的旧操作。

## 安装范围

PCL 的既有组件、整合包和补全流程继续使用对应绑定的 PCL 位置；任务创建时冻结该位置和下载策略。后台安装不再临时读取可变的 `_pclBase`。

HMCL / PCL-CE 本批支持标准原版新安装，沿用官方 `versions/<name>/<name>.json/.jar` 和共享 libraries/assets 布局。此模式不读取 PCL 隔离配置、不生成 PCL 实例偏好；需要旧 `resources/` 重定向的版本在发布前拒绝。组件、整合包、历史特供、已有实例补全以及原生隔离配置留待第二批，因此这些流程有明确拦截提示。现有 PCL 隔离偏好保留，选中外部目标时不套用。

## 验证与边界

- 新增 63 项 Core 检查：三种目录格式、共享目录身份、相对路径、主版本、坏邻居、缺失/过大配置、YAML 回退、只读字节比对、迁移/未知字段、多份绑定、任务快照及阶段能力限制。回环服务器验证 HMCL / CE 标准原版完整发布、无 PCL 实例偏好、取消后同名重试及旧资源映射拒绝。
- 新增 26 项 WPF 检查：独立卡片、迁移保存、并发偏好合并、同目录多来源选择、失败/过期/跨 busy 周期响应、实际安装入口冻结目标、取消解锁、输入门控、最小宽度与蓝/深色。页面入场使用实际 PCL 适配时钟，245 ms 时取样卡片淡入。
- 当前门槛 `PCL-DB-checks/2026-10-24.29`：UI 999 / motion 913 / pack-search 359；固定源码/资源、偏好与安装相关 Core、97 项验证契约和 19 项进程工具另查。最终回执与源码/程序摘要在本轮目录，Commit 绑定为 `committed-validation.json`。

WPF 任务使用受控安装器，发布/取消使用回环下载夹具；没有启动真实游戏，也没有操作用户的启动器配置。没有据此宣称 HMCL / PCL-CE 的真实游戏启动、所有历史格式或桌面帧率均已验收。第二批应按各自源码接入原生隔离与实例配置，再开放相应组件、整合包和补全流程。
