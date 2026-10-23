# 返回路径与标题再次入场修复

日期：2026-10-23。实现基线：`b0af195`。验证后本地 Commit；本轮不推送、打包或发版，版本号不变。

## 问题与修复

上一轮统一了标题栏、Esc 和鼠标侧键的返回入口，但任务卡片里的「返回安装选项」仍直接调用 ResumeInstallation。该方法只恢复视图，没有调用 FocusDownloadPage，导致返回整合包后恢复焦点的标记未被消费，逻辑焦点仍可能留在已经隐藏的任务按钮上。

把焦点恢复放进共同的任务返回方法，标题返回分支直接返回处理结果，避免重复请求。恢复在发起可能异步完成的列表刷新之前安排；已有页面动画控制器负责延后到视口可交互时执行，不在异步刷新完成后追加一次可能抢走新页面焦点的回调。整合包返回聚焦恢复后的视口，避免屏幕外的输入框通过 BringIntoView 改变滚动位置。

另一个差异在标题动画：原实现每次收束到主页面都会把隐藏子标题重置为 -16 DIP。PCL XAML 只在初始化时使用 -16，PageChangeExit 的动画把偏移留在 -18，下一次进入从该位置回弹。因此增加隐藏偏移状态：初始为 -16，第一次离开子标题后保留 -18。快速反向仍从当前合成值开始；resize、隐藏、卸载及禁用动画沿用同一终态清理。

保留任务对象、取消令牌、原有返回目标、搜索结果及已选安装配置，不改变下载、安装和文件发布机制。

## 固定 PCL 源码依据

重新读取本地 Git 对象 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`，保存四份源码和规范化行尾后的 SHA-256：

- [FormMain.xaml](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/FormMain.xaml)：PanTitleInner 初始 Margin.Left 为 -16。
- [FormMain.xaml.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/FormMain.xaml.vb)：PageBack 统一调用 PageChangeActual；PageChangeExit 把子标题左边距变为 -18，随后只隐藏它；后续 PageChangeActual 从现值回到 0，不重置为初始边距。保留上一轮 150／200／350 ms 时序及 Weak／Middle 曲线。
- [MyPageRight.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Controls/MyPageRight.vb)：PageOnEnter／PageOnExit 与动画完成事件管理页面生命周期，退出结束后隐藏原内容。
- [PageSpeedRight.xaml.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageSpeedRight.xaml.vb)：Loaded 重置的是任务页自己的滚动条，不是返回目标的整合包视口。本轮没有改变任务页的滚动策略。

焦点恢复是本项目为现有 WPF 控件树和动画控制器所做的适配，不把它声称为 PCL 源码中已有的同名方法。

## 验证与证据

先增加回归断言，再运行未修复的生产代码：

- navigation 在任务卡片真实 Enter 事件返回 Search 后失败：恢复标记和可见视口焦点不符合预期。
- motion 在标题返回完成后的偏移断言失败：实际 -16，预期 -18。

修复后新增 10 项断言：Search／Detail／Local 三种任务返回各检查可见焦点、视口和任务归属（6 项）；动画期间排队焦点、收束后恢复焦点且不滚动或取消任务，以及隐藏偏移和再次入场曲线（4 项）。输入事件通过生产按钮的处理方法运行，动画按实际 WPF 时钟在确定时刻采样；原有首次 -16 入场、快速反向、旧时钟回调、深色模式、最小窗口、卸载与即时模式检查继续执行。

当前检查策略为 `PCL-DB-checks/2026-10-23.5`：UI 785（含 navigation 54）、motion 554、pack-search 171。完整回归还包含 Core 5741 项，以及固定 PCL 源码与资源、验证工具和进程隔离契约。四组主要回归合计 7251 项，navigation 是 UI 的子集，不重复计数。

```powershell
dotnet build PclDownloadBridge.sln -c Release --nologo
# GUI 检查使用 tools/VerificationProcessTools.psm1 的独立进程运行器等待真实退出。
# 定向检查：--navigation-checks / --motion-checks。
./build.ps1
```

证据保存在忽略目录 `artifacts/navigation-return-followup/`：before-navigation／before-motion 为修复前失败，after-navigation／after-motion 为定向回归，source 为固定源码快照，完整回归、源码清单和二进制摘要由 validation.json 及 committed-validation.json 绑定。最终成功与数量以回执为准，任何复用证据均须绑定同一二进制。

这是隐藏 WPF 呈现源中的实际布局、渲染、受控输入及确定时刻动画验证，没有进行真人桌面验收或真实帧率测量，也未重做真实联网整合包下载。构建与工具契约检查不等同于新发布包验收，本轮不产出发行工件。
