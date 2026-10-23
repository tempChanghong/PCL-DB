# 版本筛选栏对齐 PCL 的单行布局

日期：2026-10-23。基线：`3ec5ca7`。针对用户两张截图中的版本筛选栏，验证后本地 Commit，不推送、打包或发版。

## 当前代码与改动

截图中的矩形按钮与当前源码不同：`6ea7b87` 已接入 PclRadioButton.Highlight 的胶囊绘制和颜色动画，后续也已对齐子页面返回栏及版本统计。本轮确认仍存在的差异是筛选容器使用 WrapPanel，会按可用宽度换行。新增检查在修复前因容器不是单行 StackPanel 而失败，见 `before-pack-search/checks.json`。

现在按 PCL 使用 Horizontal／Left／Top 的 StackPanel，保留已有自然文字宽度、27 DIP 按钮、13.5 DIP 圆角及选中填色。卡片边距 `10,10,0,10`，按钮边距 `2,0,2,0`，文字边距 `-2,0,10,0`。正常卡片为 47 DIP 高，“全部＋9 个版本”在 850 DIP 和应用最小 810 DIP 宽度下都保持一行且不裁切。

对于真正超出宽度的列表，增加横向视口兜底，避免最后几项无法选择。仅溢出时显示已有 PCL 样式的 8 DIP 横向滚动条；滚轮、原生滚动条命令和 BringIntoView 均可到达末项。普通行不截获滚轮，外层详情页继续竖向滚动。门控／禁用时拒绝横向输入。此兜底是本工具的适配，上游 PanFilter 本身没有这个横向视口。

## 固定 PCL 源码依据

实际读取未限制版 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的四份 Git 文件，保存规范化行尾后的 SHA-256，并在最终验证重新核对 Git 对象：

- [PageDownloadCompDetail.xaml](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/Resource/PageDownloadCompDetail.xaml)：PanFilter 容器、排列和边距。
- [PageDownloadCompDetail.xaml.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/Resource/PageDownloadCompDetail.xaml.vb)：Load_OnFinish 的四阶段分组和筛选按钮、目标版本选择，以及 UpdateFilterResult 的结果卡片。
- [MyRadioButton.xaml](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Controls/MyRadioButton.xaml)、[MyRadioButton.xaml.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Controls/MyRadioButton.xaml.vb)：胶囊几何、Highlight 配色和单选语义。原控件 XAML 与已校准的动画时序保持原有实现。

## 筛选与安装关系

筛选按钮仍只交给当前详情所有者修改选中值；忙碌、门控和已退役按钮不能改变当前项目。四阶段分组、目标版本置顶、只有一个游戏组时隐藏筛选栏继续沿用已有实现。

切换筛选仍复用已有卡片与已生成版本行，保留展开状态；返回“全部”恢复原组，不合并跨版本发布 ID。连续切换后，点击具体版本仍进入对应原归档的命名确认；取消确认不启动安装、不改变搜索会话或任务代次。

只有重新初始化版本清单才重置横向位置。普通筛选保留当前位置；如果筛选令外层竖向滚动条消失、可用宽度变大，则由原生视口把原偏移限制在新的合法范围。验证曾记录末端范围从 814.2167 缩至 808.2167 DIP，确认这是合法范围收缩，断言核对 clamp 后的位置，未强制写入越界值。

## 验证与交付边界

新增 28 项搜索／筛选断言：同截图版本组合、47 DIP 高度、正常／最小宽度、最后一项可达、单选与卡片复用、普通滚轮交接、明暗主题、快速反向、原文件安装确认和取消、超长列表滚动／命令／BringIntoView、门控／禁用、旧按钮退休与仅一组隐藏。超长列表使用假定的未来版本夹具，不声称这些版本实际存在。复查亮色和深色的隐藏 WPF 呈现源截图。

当前策略 `PCL-DB-checks/2026-10-23.11`：Core 5846、UI 799、motion 637、pack-search 232，四套合计 7514 项断言，工具契约另有 97 项。固定源码／资源及验证进程工具继续运行。最终通过状态和摘要以回执为准。

`./build.ps1` 不带 Package；忽略目录 `artifacts/filter-bar-alignment/` 保存固定来源、修复前失败、定向检查、明暗截图、溢出范围记录、完整回归和最终提交绑定。

没有重新访问线上目录或安装真实游戏；离屏截图不充当真人桌面、DPI 或帧率验收。本地运行入口仍为 `src/PclDownloadBridge.App/bin/Release/net48/PclDownloadBridge.exe`，本轮不生成新的发行包。
