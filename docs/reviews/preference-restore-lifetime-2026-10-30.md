# 启动偏好恢复的完成与退役

基线 c87ad9f56ae2df0252dd463f3b4e00010621ae28。修复 App 的异步恢复交接，保留 VB 界面、C# Core、VB 测试、.NET Framework 4.8 和程序集／资源／配置名称。

## 复现

RestoreLauncherBindingsAsync 已会在关闭或读取代次变化后丢弃结果，但没有将退役状态告诉上层。RestorePreferenceValuesAsync 因而仍调用 CapturePreferenceBaseline；旧读取成功晚到时，会在关闭窗口或新恢复之后重采基线，新编辑可能随之被误认为已保存。旧读取的迟到异常也会向已失效的上层传播。

新增用例在原实现先失败：closed startup retires without creating a preference baseline: 0，见 artifacts/preference-restore-lifetime/baseline-compatibility.json。问题位于 App 的恢复边界，不归因于 C# Core 迁移。

## 修复

- 私有目录恢复改为 Task(Of Boolean)，只有当前代次完成才返回 True；关闭或失效返回 False，迟到异常同样仅在已退役时丢弃，当前异常继续原栈传播。
- 上层偏好恢复以 Nothing 标记退役，不应用新模型或生成基线；已有有效基线和用户新编辑不会被旧请求覆盖。
- WindowLoaded 对关闭、重叠入场和退役结果直接退出，版本列表等待后再检查关闭，避免迟到状态提示与日志。原失败处理和 finally 清理保留。
- 关闭后的恢复标志清理不再触发控件输入、提示和任务可用性更新。因任务停止而暂缓的关闭尚未设置 _closing，不会误退役正常恢复。

没有新增取消线程、强杀、保存路径或配置写入。原关闭保存条件、当前失败暂停自动保存、页面切换曲线和时间不变。私有返回类型改变不影响公开接口或现有忽略结果的内部调用者。

## 源码依据

PCL2 5ef81e490ae46b1100d61e33d0183094c7a80ca8 的 ModLoader.vb:275–287 以 LastRunningThread 和取消状态判定结果所属；FormMain:557 起先处理退出任务再关闭。参考 CE 915ac29795efbdf6373417389e181e1504117048 的 ModLoader.cs:514–523、641–672 对过期成功及异常的处理，以及 FormMain:491 起的退出确认。

采用的是有效工作才能发布的顺序，未照搬上游强制中断或退出动画。恢复基线是本工具自身协议，十三份固定源码及 SHA-256 见 artifacts/preference-restore-lifetime/source/manifest.json。

## 验证与限制

新增 31 项：关闭、被新恢复替代两类情形各覆盖成功、I/O 失败、取消、普通异常，每种三项；两个正常成功控制、三个当前异常／取消控制，以及两个暂缓关闭控制。核对基线引用、目标、输入状态、读取次数、状态文本和新编辑的实际差量保存。旧 268 项兼容测试正文保留，专项 299 项；完整 UI 由 6885 增为 6916。策略仅升至 PCL-DB-checks/2026-10-30.18 并更新这两个计数，不放宽断言或超时。

测试使用实际私有恢复入口和 WindowClosing 处理器、临时偏好文件与受控目录加载器。完整 WindowLoaded 的配置路径及网络加载未实际启动，其关闭后入口／提示门控由源码核对；不宣称真实用户配置或网络验收。新夹具的门控信号类型编译问题已修正，未据此更改产品语义。

提交门槛：最终 Release／VS2026 Debug、VB 7984、当前 Core 原始／规范 API 零差异（979／1245，资源 2／JSON 15），同一冷目录单 EXE 的启动 25／兼容 299／完整 UI 6916，以及嵌入两依赖／七根文档摘要和验证契约。实际完成状态与提交树见 artifacts/preference-restore-lifetime/validation.json。

原编译 VB 宿主、完整 motion 和搜索专项本轮不另跑，不将历史结果算作新验证。主代理自查，无独立代理审查；无手工 F5、游戏或性能验收。未打包、推送或发版。原始文件读取仍可在后台自然结束，本轮不新增强制终止机制。

后续可转回管理、搜索入口的跨页面退役审查，避免把本轮基线保护扩大为其他任务协议重写。
