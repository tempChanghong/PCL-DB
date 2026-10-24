# 筛选栏滚轮路由修复

基线 `a987d9d309538a6f78e14f9a07f099eda93762d4`。本轮修复详情页的横向筛选与外层滚动冲突，验证后本地 Commit，不打包、推送或发版。

## 复现

PclFilterScrollViewer 在冒泡阶段处理横向滚轮，但外层 PclScrollViewer 在预览阶段已将同一事件标为已处理。外层原判断只识别内层竖向溢出，没有识别本程序的横向筛选视口；因此超长筛选栏实际上滚动了整页。

旧检查直接发送 MouseWheel 冒泡事件，漏过了外层预览处理。改成共享事件参数的预览→冒泡路由后，在修改业务代码前复现：78 项通过后失败，筛选栏溢出约 816 DIP，滚轮 -120 导致横向仍为 0、页面竖向从 0 到 120。见 `artifacts/filter-wheel-routing/before-search.json` 和 `before-wheel-observations.json`。

## 源码与修改

读取固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 MyScrollViewer.vb、PageDownloadCompDetail.xaml(.vb) Git 对象。上游筛选栏是 Horizontal/Left/Top 的 StackPanel；MyScrollViewer 在预览阶段接管滚轮，并对嵌套输入控件让出操作。规范化副本及 SHA-256 位于本轮 `source/`、`source.json`。

保持该单行布局与已有横向兜底。仅当事件来源链上存在实际横向溢出的 PclFilterScrollViewer 时，外层放行预览事件，供子视口冒泡处理。普通长度恢复原有竖向滚动；其他嵌套控件、弹出层和竖向滚动曲线保持原有分支。横向兜底是本工具的适配，不声称上游已存在这一视口。

## 验证

新增 15 项，使用已有详情页的离线项目和 30 个假设未来版本组夹具。验证实际页面确实可竖向滚动、筛选栏确实横向溢出；来源覆盖视口、胶囊及胶囊内部视觉节点，包含正反方向和左右边界。

在详情内容入场 175 ms、根页面入场 240 ms 时发送完整滚轮路由，横向移动且不创建外层竖向惯性，也不替换页面时钟。扩大窗口使筛选栏不再溢出后，同一来源恢复预览阶段的外层接管，并在原有 300 ms 时钟完成后验证竖向位移。项目、版本卡片、筛选、搜索会话及任务代次不变。

`filter-wheel-detail-entry.png`、`filter-wheel-root-entry.png` 为实际 WPF 时钟采样。`filter-wheel-nested-routes.json` 区分事件路由时的待执行惯性与最终竖向位移；没有使用物理鼠标或真实远程项目接口。

完整回归最初在既有可选项键盘夹具失败，原记录保留为 `motion-before-keyboard-setup.json`。检查发现该夹具向刚添加、尚未完成 Loaded 的原生 CheckBox 发送按键；补齐布局/Loaded 步骤，并记录可见性、输入门控、修饰键和选择状态，再复核原断言。没有删除检查或修改业务键盘行为。

最终门槛 `PCL-DB-checks/2026-10-24.27`：UI 973 / motion 913 / pack-search 335，共 2221 项；固定源码/资源、97 项验证契约及 19 项进程工具另查。最终回执、源码/程序摘要和 Commit 绑定在同目录 `validation.json`、`committed-validation.json`。

本轮未改搜索/下载/安装内核和打包实现，没有真实联网安装或桌面设备/帧率验收；未重跑未改变的 Core 套件，未打包、推送或发版。
