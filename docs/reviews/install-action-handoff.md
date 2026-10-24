# 浮动下载按钮与页面交接

基线 `75e7decec8c6ea64dd909fcaabb60f2055c129b6`。本轮修复浮动按钮提前显示的问题，验证后本地 Commit，不打包、推送或发版。

## 复现与源码

上一轮补齐任务页切换后，浮动下载按钮仍只在原版水平选择动画中启用独立动效。从任务页返回时，它在逻辑页面变更的第一帧就完整显示，覆盖了退出画面，而安装卡片仍在等待交接。新增检查先在未改业务代码时复现：414 项通过后失败，`before-action-observations.json` 记录按钮 visible / opacity / scale 为 true / 1 / 1，安装页 opacity 为 0；失败回执为 `before-motion.json`。

依据固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的三个 Git 对象：

- `FormMain.xaml.vb`：`PageChangeAnim` 先退出，110 ms 换页，再等 30 ms 触发 `PageOnEnter`。
- `Controls/MyPageRight.vb`：`TriggerEnterAnimation` 此时才将 MyExtraTextButton 的 `Show` 设为 True，按钮与普通卡片各自运行动画。
- `Controls/MyExtraTextButton.xaml.vb`：保留 50 ms 延迟、80 ms 淡入，及 400 ms Back / 160 ms Fluent 并行缩放。

规范化副本与 SHA-256 在 `artifacts/install-action-handoff/source/`、`source.json`，不使用相邻 PCL 工作区的 HEAD 代替固定基线。

## 实现

现有 `PclSurfaceMotion` 在其当前有效时钟允许正文入场时发出一次事件。浮动按钮的待显示状态归属于当前右侧页面或根页面控制器：逻辑切页时先隐藏，正文交接时才运行已有按钮曲线。140 ms 正文入场后，按钮按自己原有的 50 ms 延迟开始淡入，保留独立缩放、按压变换及适配层原有的 130 ms 输入门控；不把按钮当作普通卡片平移。

正常交接只放行一次；相同页面的元数据、主题或正文刷新不会重复启动。提前切到其他页面后，旧时钟不能放行新页面的按钮；中途完成/调整尺寸时直接呈现请求的终态；静态模式同步完成，没有待显示残留。按钮已开始回弹后，卡片结束不会截断它。

任务返回、设置页返回和整合包侧栏返回共用该机制。原版版本选择的水平切换保持原规则。下载状态、组件对象、名称、滚动位置和上一轮焦点交接不变，没有添加独立页面延时计时器。

## 验证

新增 26 项，覆盖等待交接、交接瞬间的零缩放/零透明度、原曲线采样和小幅回弹、输入门控叠加、主题/正文刷新、卡片与按钮各自完成、任务/设置快速反向、旧回调、两类页面尺寸变化、侧栏返回、静态模式和卸载。原有立即显示的期望同步到延迟显示；原逐像素页面退出检查暂停新按钮时钟，保证采样之间不会自行推进，未取消该检查。

明暗采样图为 `install-action-handoff-task-70.png`、`install-action-handoff-task-230.png`、`install-action-handoff-root-230.png`，同步页面与按钮时钟，分别对应退出阶段和按钮局部 90 ms 的半透明入场。采样不等同于真实桌面帧率或人工操作验收。

最终门槛 `PCL-DB-checks/2026-10-24.25`：UI 973 / motion 850 / pack-search 320，共 2143 项；另检查固定源码/资源、97 项验证契约及 19 项进程工具。最终状态、实际 EXE 摘要、源码清单与 Commit 绑定在同目录 `validation.json` 和 `committed-validation.json`。

Core、真实 PCL 设置及打包实现未改；没有重新计入历史 Core 结果，没有真实联网安装、打包、推送或发版。
