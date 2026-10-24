# 卡片悬停与页面切换

基线 `c8b811e7f2bc365dfdd6abf6774baa72233fe6c4`，工作分支 `main`。以固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 MyCard、ModAnimation 和 ModBase 为依据；没有引入新的 PCL-CE 风格或布局。

## 问题与修正

受控 WPF 中给真实 PclCard 触发悬停，再禁用卡片，旧实现的阴影和文字动画仍保留。`reproduction.json` 在第 1 项通过后失败，`reproduction.state.json` 记录 `clean=false`、`shadowAnimated=true`。旧 Hover 每次为文字/箭头新建画刷，颜色和阴影使用 HoldEnd 动画；隐藏、禁用和命中门控只处理了折叠或按下状态，没有统一清理悬停通道。

- 标题、箭头和阴影的四个动画通道改用已有 PclMotionSlot 管理；完成时写入终值并释放时钟。重复悬停保持当前目标，中途反向从实际显示值起步，旧回调不能覆盖新状态。文字和箭头各保留一支私有画刷。
- 使用已有 PCL ARGB 插值适配，保留 MyCard 的 90 ms 时序、悬停/静置阴影透明度 0.4/0.07。阴影取 ColorObject4/1，文字取 ColorBrush2/1；修正深色模式下将适配后的文字色误作静置阴影色的差异。
- 沿用共享控件动画策略，在隐藏、卸载、禁用、命中门控或系统关闭客户端动画时立即稳定悬停通道。主题刷新应用当前颜色，不保留旧主题动画。构造时也设置可用的静置颜色。
- 保留卡片布局、折叠高度/箭头旋转、版本行、详情缓存、安装状态及页面切换曲线。当前页面在切换前仍捕获悬停中的合成帧，隐藏卡片随后清理自身动画。

PCL MyCard.MouseEnter/MouseLeave 以同名 AniStart 组管理上述四种变化，颜色来自对应 Brush/Object 资源。ModAnimation.AniType.Color 按增量累积并保留小数，ModBase.MyColor.Lerp/ClampToByte 按 ARGB 通道插值、舍入。本工具复用既有 WPF 槽和 ARGB 适配，而非移植 PCL 的全局动画线程；生命周期与系统动画偏好处理属于本工具适配。

## 验证

新增 38 项控件检查：明暗模式 45 ms 数值采样、重复目标、反向衔接、旧回调、真实时钟自然完成、主题刷新，及禁用、命中、隐藏、卸载、关闭动画后的清理与恢复。自然完成由实际 WPF 时钟状态判定，不用固定睡眠断言动画已结束。

新增 8 项真实详情页检查：版本卡悬停、父级门控、恢复、与折叠时钟相互独立、设置往返和快速返回、缓存复用及静态回退。核对版本行/项目/搜索会话、额外请求、任务代次和安装取消源保持。

策略 `PCL-DB-checks/2026-10-24.45`：UI 1059、动画 1192、搜索 502；control-motion 467 是动画套件子集。固定源码/资源及验证工具另查。最终源码摘要、EXE、回执和 Commit 绑定以 `artifacts/card-hover/validation.json`、`committed-validation.json` 为准；三份固定源的精确 Git blob / SHA-256 在 `source/manifest.json`。

核对明暗卡片、实际详情及搜索截图。验证使用隐藏 WPF、实际页面/控件/时钟、受控网络和合成输入，不等同真人桌面、硬件帧率或真实安装验收。未重跑未修改的 Core 测试；未打包、推送或发版。
