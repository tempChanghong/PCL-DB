# 第四轮第三批：控件动画策略的 C# 实现

基线 `831c40ec935437193d0942365dfc941574cf8655`，包含上一批 Mod 更新缓存与反馈修复。本批仅迁移 `PclControlMotionPolicy` 的生命周期实现；VB 类型、构造参数及可选默认值、Friend 属性和页面调用入口保留。17 个生产接入位置继续使用原入口，策略状态只保存在 Presentation 的内部 C# `ControlMotionPolicy`，不保留第二套订阅或 override 状态。

## 源码依据与保留行为

实际读取固定 PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Controls/MyButton.xaml.vb:74–76,111`、`Controls/MyLoading.xaml.vb:124,179` 及 `Controls/MyPageRight.vb:236–248`：Loaded/输入状态门控、停止旧动画并设终态、页面先退役再停止和隐藏。C# 参考固定 PCL-CE `915ac29795efbdf6373417389e181e1504117048` 的 `Controls/ControlVisualHelpers.cs:7–9`、`Controls/MyButton.xaml.cs:52–53,124,139`、`Controls/MyLoading.xaml.cs:47,263–264` 与 `Controls/MyPageRight.cs:287–309`。Windows 偏好事件策略是 PCL-DB 已有行为，不声称复制了上游同名实现。

Loaded 保持先去重订阅、再发布 watching、最后同步 settle；Unloaded 保持先退订、再清 watching、最后 settle。构造时 owner 已 Loaded 则立即订阅，但不得在调用方为策略字段赋值之前调用 settle。可见性、启用和命中测试变化仍同步 settle，装饰动画仍可忽略交互资格。

偏好通知只响应 `ClientAreaAnimation` 或 null/空属性名。后台通知仍通过原 owner Dispatcher 排队，并在执行时检查 watching；卸载后忽略，若执行前已经重新加载则按原语义接受，不借迁移增加 generation 或取消规则。`EnabledForChecks` 先赋值再 settle，同值赋值也回调；`PauseForChecks` 不回调。`GetValueOrDefault(SystemParameters.ClientAreaAnimation)` 的求值与异常传播顺序保持，不增加参数校验、catch、Dispose 合约或系统设置写入。

Presentation 新增 `PresentationFramework` 框架引用，与既有 WindowsBase/PresentationCore 一起承载 FrameworkElement/SystemParameters；没有 UseWPF、XAML 或反向 App/Core/JSON 依赖。内部类型不导出，IVT 保持。单 EXE 仍嵌入原三项依赖，v4 模型和归档处理不变。Core、原 VB 控制台测试、12 份 XAML、曲线、属性时钟、资源身份及 Mod 缓存修复不改。

## 验证设计与证据

新增 40 项永久检查：5 项 VB 类型/构造/属性合约，35 项生命周期与线程检查。读取测试对象在真实静态事件中的订阅，只调用其自身处理器；不通知其他窗口或更改 Windows 偏好。覆盖早/晚构造、重复 Loaded/Unloaded、真实移除与重挂、UI/后台通知、旧通知、同步重入、受控异常、装饰门控及静态订阅清理。相同测试源注入旧 VB 基线与迁移候选均应 40/40。

独立 net48 探针对旧/新原 VB facade 比较 39 条结构与行为观测，包括构造可选默认值、属性签名、静态订阅数量、回调计数、门控、后台派发及异常阶段。冷目录仅有各自 EXE；探针显式初始化原 Bootstrap 后使用内嵌解析器。初版缺少宿主初始化的探针保留历史，不作为最终冷加载证据；冻结 v2 对两侧重跑。对象分配、闭包声明类型及私有堆栈变化不作为兼容性承诺。

完整回归门槛仅因新增 40 项改为策略 `.26`：standalone 30、all 7286、motion 6131、navigation 4725、control-motion 789；搜索 3444 与其他组保持。不同组有重叠，不相加为独立用例。最终冻结源码后，应通过 Release、VS 2026 Debug 四项目 net48/未优化/Portable PDB、同一单 EXE 冷目录的完整 standalone/UI/动画/搜索、三依赖与七文档实际摘要，并在最终 EXE 重验差分。验证工具合约需在 PS7 与 Windows PS5.1 各 126 项通过。

实际结果、探针/测试/源码摘要及提交绑定见本地 `artifacts/control-motion-policy-csharp/validation.json` 与 cold 下的 `result.json`。Core 与原 VB 测试未改，不把历史 Core 验证冒称本轮新执行。受控隐藏 WPF、时钟和 HTTP 不代替真人帧率、手工 F5 或真实平台完整下载安装；未修改用户配置、打包发版或推送。

## 后续

公共曲线、属性时钟与控件动画资格已具备 C# 实现。下一批适合按一组迁移控件按压/悬停/选中反馈引擎，先保留现有 WPF 动画类型、依赖属性 owner 和控件入口；页面与卡片编排仍单独处理，继续保护输入门控、快速反向、返回与旧时钟退役。
