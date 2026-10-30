# 首次进入 Mod 管理后的浮动操作条动画

基线：`a6e20176519c94f08cb470465c0acfb5ec92e468`，2026-10-30。本轮修复选中 Mod 后底部操作条直接出现的问题，不改下载、文件操作、隔离、配置格式或 Core。

## 源码依据与复现

主参照 PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Plain Craft Launcher 2/Pages/PageInstance/PageInstanceMod.xaml.vb`，`RefreshBars` 的 60 ms 淡入与 120/150/90 ms 上移回弹，以及 90 ms 淡出；本工具已有的时长、曲线、位置与计数规则保持。PCL-CE 仅作辅助参考，不替换 PCL2 的交互依据。

旧检查常在第一次布局前准备管理页，策略因此赶上了后续 Loaded。真实桌面顺序则是主窗口和隐藏页面先 Loaded，再首次进入 Mod 管理页。`RefreshLocalModSelection` 此时才创建 `PclControlMotionPolicy`；旧策略只在将来的 Loaded 订阅系统偏好并置 `_watching=True`，导致当前页面的 `CanAnimate` 一直为 False，直接显示终态。

独立离屏 HWND 探针使用原产品 EXE，Windows 动画开启、不设置动画 override：页面 Loaded 一次时策略尚不存在；准备 Mod 页后页面仍已 Loaded，但 `_watching`、`CanAnimate` 均为 False。全选四项后两个时钟均不存在，透明度立即为 1。候选使用同一探针，两个时钟均存在，透明度从 0 开始。记录在 `artifacts/mod-popup-recheck/late-policy-before.txt` 与 `late-policy-after-absolute.txt`。

## 修复与冲突处理

仅为已经 Loaded 的策略 owner 补上即时订阅与 `_watching` 初始化。构造时不调用 settle：调用者的策略字段尚未赋值，立即回调可能递归创建。后续 Loaded 沿用 `_watching` 去重，Unloaded 解除订阅，重新挂载恢复；现有系统减少动画、输入门控、主题与旧时钟归属处理不变。

不通过强制开启动画、延长时长、反复触发 Loaded 或重建列表掩盖问题。底部操作条仍在增选时回弹，减少选中数量不重播，取消后立即关闭操作入口，旧退场不能隐藏快速重选的新操作条。

## 验证范围

新增 34 项：10 项策略迟创建生命周期，以及明暗各 12 项实际首次进入页面后的选择条检查。覆盖真实时钟、中间帧、增选、退场、快速重选、门控、减少动画、卸载重挂和返回后的文件保留；全部观测记录完才汇总失败。新回归加到旧基线后，18 项通过、16 项失败；修复后 34/34。原断言未删除或放宽。订阅去重与静态事件退役另由源码审查确认，不把其回调次数宣称为实测。

计数策略 `PCL-DB-checks/2026-10-30.22` 仅因这 34 项调整：all 7200、motion 6045、navigation 4639、control-motion 711；其他组、超时与断言不变。各组存在重叠，不合计为独立场景。

最终验证回执位于 `artifacts/mod-popup-recheck/`：同一冷目录单 EXE 的 standalone、motion、all、pack-search，及嵌入依赖和七份文档匹配，使用既有 `tools/verify-single-exe.ps1`；另检查控制动画组、VS2026 Debug 与 PCL 固定资源。实际完成状态、输入摘要与提交树绑定以本轮 `validation.json` 和单 EXE `result.json` 为准。

这是受控 WPF/临时实例验证与渲染检查，不代替真实鼠标操作或帧率测量。Core 业务源码未改，不重跑无关下载/安装全量；未修改用户配置，未发布版本。
