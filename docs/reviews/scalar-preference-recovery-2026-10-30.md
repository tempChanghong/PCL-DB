# 基础偏好标量的逐项恢复

基线 2c437906b7c00c8998db55da22675cc552928b67。本轮修复 VB 启动恢复，保留 C# Core、VB 界面与测试、.NET Framework 4.8、程序集、配置和资源名称。

## 问题与修复

WindowLoaded 原先直接转换 pclBase、connectionsIndex、speed、reuse、java。结构值、错误布尔字符串或整数溢出会进入外层失败处理，阻止剩余有效偏好、启动器目录和基线恢复。先把原正文提炼为私有 RestorePreferenceValuesAsync，保持原转换供新增回归复现；首个失败为 one invalid scalar does not stop restoring native targets and the preference baseline: pclBase/1/0（1 为 VB 拼接的 Object 枚举值），见 baseline-compatibility.json。

新的私有 ReadScalarPreference 只捕获 ArgumentException、InvalidCastException、FormatException、OverflowException，按项回退为原缺省值并收集中文字段名称。有效标量继续使用原转换，保留可兼容字符串、数值、线程索引钳制；不可用的速度字符串仍留在输入框，由原校验提示纠正，不静默转成有效速度。

恢复仍先应用偏好，再等待目录读取，最后采集界面有效值基线；读取代次、输入门控、安装目标身份和清理路径不改。仅无关字段变化的自动保存不把回退值当作用户编辑，因此原坏值保留；显式保存可以修复可见值并保留未知字段。当前单项错误通过已有启动状态提示汇总，不增加窗口或改变动画。

文件解析、目录 I/O 和其他非转换错误仍向 WindowLoaded 原外层失败处理传播，保持暂停自动保存的行为；不以宽泛 catch 掩盖读取失败。本轮只处理以上五个直接标量转换，未扩展其他偏好对象协议。

## 源码依据

以 PCL2 5ef81e490ae46b1100d61e33d0183094c7a80ca8 的 Settings.Get:327–361 为核心：单项读取和转换失败时使用单项默认值。C# 参考 CE 915ac29795efbdf6373417389e181e1504117048 的 FileConfigStorage:106–135 对类型、格式及溢出异常的隔离；不移入其错误项删除、迁移或同步写回。

本工具的 JSON、界面差量基线和异步目录等待仍用自身既有协议。固定源码、调用链与 SHA-256 见 artifacts/scalar-preference-recovery/source/manifest.json，共十一份 Git 原字节。

## 验证

新增 87 项：16 种坏值分别检查完整恢复、仅坏项回退、无关自动保存保留原数据、显式修复；十个缺省／兼容控制验证字符串与数值转换、钳制和可纠正的速度文本；两个目录等待用例验证输入门控与基线归属；一个 I/O 用例验证非转换错误继续传播。加载器为临时目录的受控三启动器发现，实际生产入口共用同一私有恢复函数，没有读取真实偏好。

既有兼容检查 181 项保留，专项为 268；完整 UI 由 6798 增至 6885。策略仅升至 PCL-DB-checks/2026-10-30.17 并增加这两个计数，不减断言或放宽超时。新夹具的只读集合、Nullable 判断和枚举标记已按 VB 规则修正；未据此改变产品或放宽断言。

提交门槛：最终 Release／VS2026 Debug、VB 7984 项、当前 Core 原始／规范 API 零差异（979／1245，资源 2／JSON 15）；同一冷目录单 EXE 的启动 25／兼容 268／完整 UI 6885、嵌入两依赖／七根文档摘要和验证契约。实际进程、二进制与提交树见 artifacts/scalar-preference-recovery/validation.json。

原编译 VB 宿主、完整 motion 和搜索专项本轮不另跑，不列为新验证。无手工 F5、真实用户配置改写、游戏或性能验收。主代理自查，无独立代理审查。未打包、推送或发版。

后续可检查保存基线与异步关闭的生命周期；本轮不扩大路径解释、下载或外观模型的兼容规则。
