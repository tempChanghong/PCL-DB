# PCL 图标文字按钮还原

基线 `4856bf4f99c4e9c6e4cac93b57f56167052fe9c0`。本轮将“PCL2 为核心、PCL-CE 仅作补充参考”写入项目说明，针对上一轮紧凑操作按钮做源码对照。保留用户已确认的搜索分层、深色提示和现代线性导入图标；未修改查询、缓存、分页、项目操作处理器、版本隔离或下载安装内核。

## 固定源码与调整

参考 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Controls/MyIconTextButton.xaml(.vb)`、`Modules/Base/ModAnimation.vb`、`ModBase.vb` 和 `Pages/PageDownload/Resource/PageDownloadCompDetail.xaml`。原始 Git blob、SHA-256 与源码保存在忽略目录 `artifacts/pcl-action-parity/source/`。

- 默认移除上一轮借鉴 CE 的常驻描边；保留 27 DIP 高、13.5 DIP 圆角和 PCL 文字边距，背景在悬停/键盘焦点时反馈。
- 增加 PCL 的高亮/普通文字角色，网站入口高亮，复制和导入为普通角色。固定 PCL 详情页当时使用 35 DIP MyButton，其网站按钮同样为高亮；本工具已确认的紧凑布局继续使用 PCL 原生 MyIconTextButton 的角色和几何规则。
- 图标改用 16 DIP 最大尺寸，保持自然宽高比，避免窄图标仍占正方形宽度，文字间距更贴近源控件。
- 移出动画从 200 ms 校正为源码的 150 ms。按压分支只改变背景，保留正在运行的 100 ms 图标/文字变色；70 ms 按压背景有独立时钟。
- 实际 75 ms 中间帧检查发现普通 WPF ColorAnimation 不符合 PCL AaColor / MyColor 的 ARGB 字节线性插值。复用项目已有的 PclRadioColorAnimation（资源行也在使用），保持当前显示色作为替换动画起点，不再新建一套颜色计算。
- 保留原生 Button 键盘/辅助功能、颜色角色变化时的时钟清理、门控和代次绑定。主题、禁用、失焦及快速返回仍取消旧手势；PCL 原控件没有提供的键盘可达性继续保留。

## 验证

修改前按固定源码的 150 ms 标准检查，在 19 项通过后复现旧过渡时长不符；修正后新增逐帧检查又发现颜色中间值不符，保留两份失败回执并修正实现。没有降低中间色、时钟归属或输入断言。

新增 20 项检查覆盖明暗角色、窄图标布局、按压与悬停并行、移出中间帧、角色切换、过期时钟、禁用与新手势恢复。策略 `PCL-DB-checks/2026-10-24.34`：UI 1014、动画 976、搜索 382，共 2372 项；控件 326 项为动画子集。固定源码/资源 20 项、验证契约 97 项和进程工具 19 项另查。

明暗按钮及实际详情页使用隐藏 WPF 窗口渲染，网络和输入为隔离夹具，不代表真实设备键鼠/桌面帧率验收。本轮不重跑未修改的 Core，不打包、推送或发版。最终源码、EXE 摘要与本地 Commit 绑定见 `artifacts/pcl-action-parity/validation.json` 和 `committed-validation.json`。
