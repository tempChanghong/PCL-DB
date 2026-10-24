# 浮动下载按钮内部按压反馈

基线 `1a3714dc457252a0553fb8ccb3abc27b822ea246`。本轮补齐浮动按钮缺失的内部按压动画，验证后本地 Commit，不打包、推送或发版。

## 复现与源码

按钮已有外部入场缩放，其模板内部也有独立 ScaleTransform，但原先没有按压动画实现。新增检查在修改前复现：440 项通过后失败；实际模板按下后没有动画属性，内部与外部缩放均为 1，见 `artifacts/install-button-press/before-motion.json` 和 `before-press-observation.json`。

读取固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Controls/MyExtraTextButton.xaml.vb`、`Modules/Base/ModAnimation.vb`、`Controls/MyPageRight.vb` Git 对象，规范化副本和 SHA-256 在同目录 `source/`、`source.json`。

MyExtraTextButton 的按下反馈是并行增量：800 ms Strong Fluent 朝 0.85 缩放，另用 60 ms 默认 Fluent 减少 0.05，长按终点为 0.8；释放按默认 Back 在 300 ms 内恢复，移出按默认 Fluent 在 500 ms 内恢复。ModAnimation 明确 Strong=4、Middle=3；特别核对 60 ms 微缩也是三次 Fluent，补充独立的 30 ms 数值检查，避免误写为线性。

## 修改

PclInstallButton 继承标准 WPF Button，保留现有模板、点击语义及原生 Invoke 能力。模板仅给现有内部变换增加 PART_Press 名称；内部按压时钟与外部入场缩放、按钮底色及页面卡片动画分开管理。动画数据使用依赖属性，支持 WPF 克隆/冻结。

鼠标按下运行原曲线；释放回弹，移出取消原手势并恢复。键盘沿用原生 Button 的 Space/Enter 行为，同时给予按压反馈。切页门控、禁用、隐藏、失焦、卸载、主题变化或模板替换会清除旧手势和旧时钟，恢复后到来的释放事件不能启动安装。显式自动化调用仍使用原生实现，并同时检查调用时和执行时的页面门控。

模板被清空时不一定调用 OnApplyTemplate，因此在模板属性变化时立即取消旧变换，避免离开视觉树的 ScaleTransform 继续带有时钟。系统关闭客户端动画时使用静态几何，不改变按钮可用性及点击功能。上述键盘、原生 Button 和门控处理是本程序的适配，不是逐字复制上游 UserControl。

## 验证

新增 63 项，包括三种方向/三个起点的原公式采样、时长及冻结副本；0.8 长按、1.05 释放回弹、0.975 移出恢复；实际下载模板及独立外部缩放；过期时钟、页面门控、禁用、隐藏、卸载、主题、模板重建、静态模式、Space 释放和失焦；原生 Invoke 可用性及门控期间调用不在恢复后误触发。

`install-button-held-800.png` 显示实际安装页的长按，`install-button-release-150.png` 显示独立模板的释放回弹。采样使用真实 WPF 时钟和合成输入；原生自动化调用通过标准接口验证，没有操作桌面鼠标，不能外推为人工桌面键鼠或帧率验收。

最终门槛 `PCL-DB-checks/2026-10-24.26`：UI 973 / motion 913 / pack-search 320，共 2206 项。固定源码/资源、97 项验证契约及 19 项进程工具另查。最终回执、源码/程序摘要及 Commit 绑定以同目录 `validation.json`、`committed-validation.json` 为准。

本轮未改下载/安装/搜索内核、真实 PCL 配置及打包流程，不重新计入旧 Core 结果，不打包、推送或发版。
