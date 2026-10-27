# 本地 Mod 全选快捷键与搜索焦点

基线提交：`cf4b91e`。继续检查选择、筛选、焦点和动画的联动，确认 Ctrl+A 与按钮切换共用了同一处理分支。

## 依据与问题

重读固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `PageInstanceMod.xaml.vb`。`FrmMain_KeyDown` 每次 Ctrl+A 都调用 `ChangeAllSelected(True)`；`BtnManageSelectAll_Click` 按当前选择数量决定全选或取消。`SearchBox_PreviewKeyDown` 在搜索框为空时转移焦点，让快捷键到达列表，有文字时保留编辑器处理。固定 Git 原始字节及摘要见 `artifacts/local-mod-shortcuts/source/manifest.json`。

PCL-DB 的 Ctrl+A 调用按钮切换函数，第二次按键会清空文件选择，并把行、底栏动画从入场改成退场。该快捷键还直接排除了所有搜索框焦点，导致空搜索框中的 Ctrl+A 无法全选 Mod。

## 修复

增加按目标状态改变当前显示行选择的内部入口。按钮继续根据当前显示行切换全选／取消，Ctrl+A 明确请求全选。重复全选保留现有行、选择状态与动画时钟。

空搜索框的 Ctrl+A 在可选择时转移焦点到 Mod 列表；非空搜索（包括空格）保留原生文本全选。继续使用当前实际显示的行，保留页面与内容门控、写入期间禁用、原修饰键规则、Escape 和隐藏页保护。未修改文件操作、下载、更新或外部启动器设置。

## 验证与边界

为避免向用户桌面发送修饰键，私有按键处理方法增加可选的修饰键参数；正常 Shell 调用仍读取 Windows 键盘状态。旧行为基线只加入这一参数，保留原全选切换和搜索焦点分支。基线变体及摘要在 `artifacts/local-mod-shortcuts/baseline-selection.vb`、`baseline-transform.json` 中；它在相同最终夹具下出现 **10/34 项失败**。

新增 34 项 WPF 检查，明暗各 17 项：连续 Ctrl+A、选择与时钟保持、行身份和焦点、按钮切换、空／非空／空格搜索及原生文本全选、启用筛选／单项搜索、两级页面门控、写入保护、普通 A／Ctrl+Alt+A、Escape 清除，以及隐藏页焦点保护。使用合成 KeyEventArgs、可控修饰键和实际 WPF 焦点／编辑命令，不模拟桌面键盘输入。

最终同一构建通过选择链路 **164 项**、列表样式／行交互 **45 项**、取消焦点与页面交互 **17 项**；新增 34 项已包含在 164 项内部。验证契约 97 项、限时策略 44 项、进程检查 19 项和固定 PCL 来源／资源／控件另验。Release 重建零警告、零错误，源文件清单、二进制摘要和回执见 `artifacts/local-mod-shortcuts/validation.json`。

策略 `PCL-DB-checks/2026-10-28.3` 的 all 3,613、motion 2,998、navigation 1,990 为注册数量，本轮未重跑完整套件、搜索 UI 或 Core 全套。未做真实桌面按键／帧率测量，不写入用户游戏文件或配置，不打包、推送或发版。
