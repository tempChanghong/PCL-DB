# 整合包版本筛选胶囊与颜色动画修复

日期：2026-10-23。基线：`21dbeec`。验证后本地 Commit，不推送、打包或发版。

## 问题与结果

详情页版本筛选原先使用矩形命令按钮，与 PCL 的高亮胶囊单选控件不同。复用顶部 PclRadioButton 前，另用受控 WPF 动画复现了旧实现的资源问题：悬停颜色过渡结束后，背景、图标和文字仍保留 HoldEnd 动画时钟。两项修复前失败分别保存在 `before-pack-search` 和 `before-control-motion`，前者是外观还原性差异，后者是可重复的动画生命周期问题。

现在同一个 PclRadioButton 支持 White 和 Highlight 两种配色。主导航保留 White；详情筛选使用 Highlight，按自然文字宽度排列，不再有原矩形按钮的固定最小宽度。筛选只更新 Checked，保留版本卡片、已构造行、展开状态、目标版本置顶及精确文件 ID，不改变安装流程。

每个控件持续使用自己的三支画刷，以 PclMotionSlot 管理颜色动画。中途反向从当前颜色起步，同目标更新不重启；完成后写入最终颜色并释放时钟。旧动画完成回调不能覆盖新状态。颜色动画只改画刷，不逐帧修改布局，也不逐帧创建插值委托。

## 固定 PCL 源码依据

实际读取未限制版提交 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的六份 Git 文件，规范化行尾后保存 SHA-256 并在最终验证中重新比对 Git 对象；不修改上游工作目录或既有固定快照。

| 来源 | 本轮使用的机制 |
| --- | --- |
| [MyRadioButton.xaml](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Controls/MyRadioButton.xaml) | 27 DIP 高度、13.5 DIP 圆角；本地控件 XAML 保持原样，仅既有类名适配 |
| [MyRadioButton.xaml.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Controls/MyRadioButton.xaml.vb) | ColorState.Highlight 的调色板和状态时序；按下只改变背景，不重启文字过渡 |
| [PageDownloadCompDetail.xaml.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/Resource/PageDownloadCompDetail.xaml.vb) | Load_OnFinish 创建 Highlight 单选按钮，按钮边距 `2,0,2,0`、文字边距 `-2,0,10,0`，由详情更新筛选结果 |
| [PageDownloadCompDetail.xaml](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/Resource/PageDownloadCompDetail.xaml) | 筛选容器边距 `10,10,0,10` |
| [ModAnimation.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Base/ModAnimation.vb)、[ModBase.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Base/ModBase.vb) | AniType.Color 的颜色增量，MyColor.Lerp 与 ClampToByte 的 ARGB 通道插值及字节舍入；适配成固定起点到目标的 WPF 颜色动画 |

| Highlight 状态 | 背景／文字 | 时长 |
| --- | --- | --- |
| 选中 | ColorBrush3／白色 | 120ms |
| 按下未选中项 | ColorBrush6；沿用正在进行的文字过渡 | 90ms |
| 悬停未选中项 | ColorBrush7／ColorBrush3 | 90ms |
| 离开未选中项 | ColorBrushSemiTransparent／ColorBrush3 | 150ms |

White 模式继续保留白色选中背景、主题文字及 alpha 120／60ms 的按下过渡。以下是本工具适配，并非声称上游逐行相同：保留 WrapPanel 供窄窗口换行；键盘焦点使用悬停色；Space／Enter 仍由详情所有者决定切换。忙碌或输入门控拒绝切换时，控件不会自行改变 Checked。隐藏、卸载、禁用、门控、系统关闭动画或主题切换会停止旧时钟，恢复后不会重新激活已取消的按下手势。

## 验证

新增 32 项控件动画断言和 7 项搜索／筛选断言，没有删除原有检查：

- 控件覆盖 ARGB 数值采样、Freezable 克隆／冻结、明暗调色板、反向衔接、旧回调、完成清理、重复写入、已选项吸收输入、生命周期、White 兼容与系统关闭动画。
- 在隐藏但有效的 WPF 呈现源中采样颜色过渡，核对父容器没有新增 Measure／Arrange 调用；这只能证明布局行为，不等同真实显示器帧率测试。
- 详情覆盖圆角／尺寸／边距、鼠标与键盘互斥选择、忙碌和门控、目标卡片复用、深色文本及原有版本分组。截图检查亮色和深色详情，没有发现裁切或文字对比异常。

当前验证策略为 `PCL-DB-checks/2026-10-23.8`：Core 5741、UI 799、motion 586、pack-search 193，四套合计 7319 项断言。control-motion 212 是 motion 的子集，不重复计入总数。工具契约另有 97 项；旧发行包的历史回执不套用新计数。固定 PCL 源码／资源和进程工具继续验证。最终通过状态与二进制摘要以回执为准。

```powershell
./build.ps1
# 定向 control-motion / pack-search 通过 VerificationProcessTools.psm1 等待真实进程退出。
```

忽略目录 `artifacts/pack-filter-capsules/` 保存固定源码、修复前失败、定向回归、截图、最终构建和完整回归。`validation.json` 与 `committed-validation.json` 绑定源码清单、二进制、回执与最终提交。未执行真实联网下载、游戏启动或真人桌面验收，本轮不产出发行工件。
