# 卡片箭头的初始状态与旋转归属

基线 `7c72b5654fdb4220920a4a76ff510b6618b54e3f`，工作分支 `main`。依据固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 MyCard 与 ModAnimation；没有引入新的 PCL-CE 布局。

## 问题与修正

受控 WPF 中直接创建默认展开的版本卡片，首次显示时箭头角度为 0°，指向下方。PCL MyCard.Init 为展开卡片创建 180° 旋转，折叠时才改为 0° 或 270°。原始失败回执为 `reproduction.json`（第 0 项即失败），`reproduction.state.json` 记录 `collapsed=false`、`angle=0`、`animated=false`。

检查相关路径还发现：旧旋转使用 HoldEnd 动画，自然结束后仍附着在 RotateTransform；重复写入未改变的 SwapLogoRight 会直接稳定箭头，从而提前结束正在运行的旋转。

- 默认旋转改为 180°，折叠及向右箭头仍保持 0°/270°，与原版初始化一致。
- 箭头旋转复用已有 PclMotionSlot，保留 PclCardMotion 的 250 ms、ExtraStrong（五次方 ease-out）曲线。自然完成写入终值并释放时钟，快速反向从当前显示角度起步，旧回调不能覆盖后继动画。
- 未变化的方向属性写入不再打断当前旋转；实际改变方向、隐藏和卸载仍稳定到已接受的展开/折叠状态。
- 高度、箭头、悬停和页面切换继续各自管理动画；保留几何、卡片布局、版本行、缓存、搜索和下载安装机制。没有扩大为折叠动画的系统偏好改造。

PCL MyCard.Init、IsSwapped 与同名 `MyCard Swap` 动画组确定初始化方向和旋转替换关系；ModAnimation.AaRotateTransform、AniEaseOutFluent、AniEasePower.ExtraStrong 确定增量旋转及 `1 - (1 - t)^5`。本工具继续使用既有 WPF 适配，不引入 PCL 的全局动画线程。

## 验证

新增 38 项控件检查：默认展开状态，明暗模式与两种折叠方向，250 ms 曲线采样，重复状态写入、快速反向、旧回调和自然完成；隐藏、卸载、方向更改后的稳定与恢复。自然完成以实际 WPF 时钟状态判定，不以固定睡眠认定完成。

新增 6 项真实详情页检查：实际卡片标题 Enter 折叠，返回搜索、入场放行后的搜索行回车快速返回、缓存重新展开、主题刷新和自然完成；核对页面时钟、卡片/版本行、项目/搜索会话、详情请求、任务代次及安装取消源保持。

策略 `PCL-DB-checks/2026-10-24.46`：UI 1059、动画 1230、搜索 508；control-motion 505 是动画子集。固定源码/资源及验证工具另查。最终源码摘要、EXE、回执和 Commit 绑定以 `artifacts/card-arrow/validation.json`、`committed-validation.json` 为准；两份固定源的精确 Git blob / SHA-256 在 `source/manifest.json`。

核对明暗/向下/向右卡片、实际详情及搜索截图。验证使用隐藏 WPF、实际页面/控件/时钟、受控网络和合成输入，不等同真人桌面、硬件帧率或真实安装验收。未重跑未修改的 Core 测试；未打包、推送或发版。
