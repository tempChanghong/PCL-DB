# 偏好 JSON 重复字段处理

基线 c3ae36001d4c44d5f6fe8691f70a8e25bf773c75。本轮修复 App 的偏好解析，不改 Core、公开接口、配置字段、资源、布局或动画参数，保留 VB 界面、VB 测试和 .NET Framework 4.8。

## 复现与修复

重复 gameRoot 原本由 JObject.Parse 静默采用最后一个值，重复 format 也能把未知格式伪装为已知格式。后续保存会丢失另一份值，既无报错也无法判断哪份是用户意图。新增回归在原实现的真实 LoadCompatible 路径失败：duplicate current settings reject ambiguous fields without using obsolete fallback data: 0，见 baseline-compatibility.json。此为既有 App 默认解析策略的问题，不归因于 C# 迁移。

现有目标、迁移源、迁移竞争后产生的目标这三个解析位置统一使用私有 ParseSettings，设置 DuplicatePropertyNameHandling.Error。保存仍在原有每路径锁内加载最新对象，重复键错误会在合并和发布前退出；原字节、时间戳、窗口基线与旧配置保留，目标文件存在但损坏时不回退到旧配置。迁移源不合法时不创建目标目录。

既有 replaceMalformed 仅由显式保存按钮路径使用，允许用户检查可见设置后修复歧义 JSON。它与自动保存有别：自动保存始终拒绝；仍可正常解析的未知绑定格式继续受上一轮保护，不能通过普通显式保存降级。

日期、注释、BOM、大小写及字段作用域的原解释不变；重复是同一对象内解码后相同的键，两个对象各有一个 id 或 Keep／keep 并不冲突。没有新增文件大小限制、默认值回写或静默迁移。

## 源码依据

以 PCL2 5ef81e490ae46b1100d61e33d0183094c7a80ca8 的 Settings.vb:278–294 和 ModMinecraft.vb:17–21 的设置键与目录值保存边界为主，参考 CE 915ac29795efbdf6373417389e181e1504117048 的 JsonFileProvider:32–60 根对象解析与初始化失败处理。

严格重复字段直接对齐本工具 C# LauncherDirectoryReader.ParseConfiguration 和原生实例配置快照的既有 DuplicatePropertyNameHandling.Error，不将其描述为 PCL／CE 存在同名偏好协议。九份固定源码及 SHA-256 见 artifacts/settings-duplicate-keys/source/manifest.json。

## 验证与边界

新增 50 项：九类重复值各验证当前读取、自动保存、旧源迁移、显式修复及修复后重试。覆盖目录、来源、格式号、绑定 ID、未知对象、数组内对象、Unicode 转义键、中文键和完全相同的重复值。另三个合法控制核对 UTF-16 BOM、注释、尾逗号、大小写不同键、不同对象同名键及未知值往返；两项核对等待保存锁期间引入重复字段与后续锁释放。

既有 131 项兼容测试正文保留，专项 181 项；完整 UI 由 6748 增至 6798。策略仅升至 PCL-DB-checks/2026-10-30.16 并增加这两个计数，不降低原断言或放宽超时。竞争迁移目标的特殊分支未单独动态触发，由源码检查确认复用同一严格解析器，不将其列为独立复现。

提交门槛：最终 Release／VS2026 Debug、VB 7984 项、当前 Core 原始／规范 API 零差异（979／1245，资源 2／JSON 15）；同一冷目录单 EXE 的启动 25／兼容 181／完整 UI 6798，以及嵌入两依赖／七根文档摘要和验证契约。完成状态、实际字节与提交树见 artifacts/settings-duplicate-keys/validation.json。

原编译 VB 宿主、完整 motion 和搜索专项本轮不另跑，不将历史结果写成新验证。全部为临时配置和受控窗口，无真实用户配置修改、手工 F5 或实际游戏验收。主代理自查，无独立代理审查。未打包、推送或发版。

保存锁仍要求写入者协作，不承诺任意外部编辑器绕过锁后的改写隔离。下一步可检查偏好标量类型损坏时的逐项恢复，避免一个无关坏项中断整页恢复。
