# PCL 子页面标题栏与返回逻辑对齐

日期：2026-10-23。实现基线：`8f2834f`。本轮验证后本地提交，不推送、打包或发版。

## 结果与范围

整合包详情不再保留 PCL-DB 主标题、顶部下载／设置选项及内容卡片里的「返回搜索结果」。改为左上角的返回箭头和当前项目名，退出下载侧栏及安装目录工具条，使详情占据完整页面宽度。标题使用接受后的项目信息，异步元数据刷新不新增返回层级；搜索条件、平台分页、结果对象、滚动位置和版本卡片继续由原有页面状态持有。

任务管理也使用 PCL 的子页面标题，返回时恢复实际进入来源：详情 → 任务 → 详情 → 搜索；原版安装 → 任务 → 原版安装；版本列表 → 任务 → 版本列表。修正旧逻辑在仍有历史选中版本时，会从任务页误返安装选项的问题。返回不触发取消，不清空已选组件、自定义名称或任务状态。

标题栏按钮、Esc 与两个鼠标侧键共用返回入口。在线安装确认期间保留门控，设置／关于页持有页面时，旧子页面的返回动作不生效。原版安装页内部返回与关于页内部返回保留各自的原有层级；PCL 固定代码也未将原版安装选择页、关于页列为全局子页面。

返回整合包时将焦点还给已恢复的视口，避免直接聚焦屏幕外的搜索／名称输入框，由 WPF BringIntoView 把滚动位置带回顶部。新进入详情和本地导入仍使用原有输入焦点策略。

## 固定源码依据

直接读取并与 Git 对象逐份核对提交 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的三个文件，标准化行尾后相同：

- [FormMain.xaml](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/FormMain.xaml)：PanTitleMain／PanTitleInner 双层标题；返回图标 28 DIP、左距 12、LogoScale 0.87，标题字号 15、左距 47。
- [FormMain.xaml.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/FormMain.xaml.vb)：PageNameGet 为 ResourceDetail 使用项目名、TaskManager 使用任务管理；PageBack、PageStack、PageChangeActual、PageChangeExit 管理子页面、返回目标、主标题门控与两层切换；ResourceDetail 使用空左栏。TriggerPageBack 同时接受两个鼠标侧键。
- [ModAnimation.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Base/ModAnimation.vb)：Weak=2、Middle=3，AniEaseInFluent 的幂曲线及 AniEaseOutBack 的余弦回弹；不使用 WPF 的多项式 BackEase 替代。

沿用现有下载视图、整合包模式及 progressReturnView 作为返回状态，不另建一套会随元数据刷新重复压栈的项目导航。Window.Title 及项目身份不随详情改变，只有标题栏可见层切换。

## 动画与冲突处理

主页面进入子页面：旧标题 150 ms 淡出、向右 12 DIP；新标题延迟 200 ms，150 ms 淡入并用 350 ms Middle 余弦回弹从 -16 DIP 回位。返回主页面时旧子标题 150 ms 向左 -18 DIP 并淡出，主标题延迟 200 ms，以 Weak 回弹回位；合计 550 ms。子标题之间使用 130 ms 退场、更新文本、延后 30 ms 再用 150 ms 淡入。

独立控制器持有标题层及文本的时钟，替换从当前合成值开始，旧回调仅能清理自己的时钟；相同标题刷新不重播。页面输入归属立即切换，退出中的顶部按钮不能继续操作。窗口尺寸变化、卸载、隐藏及系统动画偏好变化收束到当前状态；关闭页面动画时直接显示终态。

详情退出侧栏和目录工具条会改变下载视口原点，冻结的退出画面按原根坐标补偿，避免跟着新布局瞬间横移或上跳。它不改变下载阶段、异步列表的归属、动画完成后的焦点恢复和滚动位置。

截图检查发现深色模式会把 Dynamic ColorBrush8 变为正文深色，因此子标题与已有主标题一样固定白色，返回按钮沿用 White 主题；这是本项目深色模式下保持标题栏可读的适配。长项目名省略显示并保留完整提示，预留最小化／关闭按钮空间。

## 验证

```powershell
./src/PclDownloadBridge.App/bin/Release/net48/PclDownloadBridge.exe --navigation-checks artifacts/title-back-alignment/navigation-checks.json
./src/PclDownloadBridge.App/bin/Release/net48/PclDownloadBridge.exe --motion-checks artifacts/title-back-alignment/motion-checks.json
./src/PclDownloadBridge.App/bin/Release/net48/PclDownloadBridge.exe --pack-search-checks artifacts/title-back-alignment/pack-search-checks.json
./build.ps1
```

导航套件新增 17 项，覆盖详情标题／布局、原图标参数、深色模式、搜索对象与位置保留、任务逐层返回且不取消、设置往返、隐藏返回／在线确认门控、Esc、两个鼠标侧键，以及 810×470 最小尺寸下的长标题避让。

动画套件新增 12 项，检查实际 WPF 时钟的 150／200／350 ms 曲线与余弦位置、旧画面原点、当前输入归属、相同标题不重播、深色切换不重启、快速反向切换、文本层更新与过期回调、缩放／卸载清理及关闭页面动画的即时模式。

完整回归继续包含下载、安装、取消重试、搜索、组件、已有配置保留及打包工具契约。当前检查策略升级为 `PCL-DB-checks/2026-10-23.4`，相应套件为 UI 779、navigation 48、motion 550、pack-search 171；历史回执仍按历史策略检查，不能作为当前候选证据。

WPF 在隐藏的独立呈现源中实际布局、渲染并按确定时刻推进动画；键盘与鼠标侧键通过受控事件验证，没有修改用户 Windows 动画设置。这些检查及生成截图不等同于真人操作或真实桌面帧率测试。本轮不重新下载游戏或联网搜索，也不执行发布打包。

忽略目录 `artifacts/title-back-alignment/` 保存固定源码、初步及最终进程、明暗／长标题截图、全套构建、源码清单与提交绑定。最终通过状态、断言数、二进制及源码摘要以 `validation.json` 和 `committed-validation.json` 为准。
