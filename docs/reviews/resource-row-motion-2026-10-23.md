# 搜索项目行输入与背景动画修复

日期：2026-10-23。基线：`6ea7b87`。验证后本地 Commit，不推送、打包或发版。

## 问题与修复

原 PclResourceItem 的 MouseLeave 只刷新动画，没有取消按下。按住项目行、移出、再移回并松开会触发 Click；在隔离 WPF 呈现源中，修复前首条断言已失败，见 `before-control-motion/checks.json`。PCL 的 Button_MouseLeave 会直接清除 IsMouseDown，因此这是已确认的操作差异。

原鼠标、Space 和 Enter 共用一个 `_pressed` 字段，无法核对松开事件属于哪次输入。现在鼠标和精确键盘按键分别记录，只有匹配的松开才能完成手势。移出取消鼠标手势；失焦、隐藏、禁用、卸载、输入门控、主题和动画偏好变化清除待完成输入，并仅释放当前控件持有的鼠标捕获。恢复页面后的旧松开不能打开详情，新的正常操作仍可继续。

项目行原先复用 MyListItem 对应的普通版本行动画，导致文字／图标一同缩至 0.98，且使用 0.75 初始背景和 541ms 离开尾段。现有独立资源行动画只缩放背景，文字／图标保持原大小。固定画刷与状态缓存避免重复创建画刷和重播动画；快速反向从当前画面继续，旧时钟完成不能覆盖新状态，完成后释放所有属性时钟。

## PCL 源码依据

实际读取未限制版 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的三份 Git 文件并保存规范化行尾后的 SHA-256；最终验证重新与 Git 对象比对，未修改上游工作区和既有固定快照。

- [MyResourceItem.xaml.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/Resource/MyResourceItem.xaml.vb)：Button_MouseDown／MouseLeave／MouseUp、RectBack 和 RefreshColor。
- [MyResourceItem.xaml](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/Resource/MyResourceItem.xaml)：资源项目行结构；保留本工具原有 64 DIP 行高、50 DIP 图标和文字结构。
- [ModAnimation.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Base/ModAnimation.vb)：AniEaseOutFluent 默认三次方和 After 的组内等待规则。颜色沿用上一轮按 ModBase.MyColor 校准的 ARGB 字节插值适配器。

| 状态 | 背景颜色／缩放／透明度 | 时序 |
| --- | --- | --- |
| 初始 | ColorBrush7，0.8，0；圆角 3 DIP | 静态 |
| 悬停 | ColorBrushBg1，缩放至 1，透明度至 1 | 颜色 120ms 线性；透明度 120ms Fluent；缩放 144ms Fluent |
| 按下 | ColorBrush6，背景缩放至 0.996 | 同上；不缩放文字和图标 |
| 离开 | ColorBrush7，透明度至 0，背景接近 0.996 | 颜色／透明度 180ms 线性；背景 180ms Fluent，然后 1ms 回到 0.8 |

键盘输入和焦点高亮是本工具补充；上游本段仅处理鼠标。系统关闭动画时，本工具仍立即提供悬停反馈；上游该静态分支会将已有背景透明度设为零。本工具还按统一策略清理隐藏／卸载和旧时钟，不移植整个上游动画调度器。

本轮只修改资源行的局部表现及输入，不修改普通版本行、搜索分页／排序／缓存、下载调度、版本隔离、共享文件和原子安装发布。

## 验证与边界

新增 51 项控件断言：移出取消、键盘键值和鼠标归属、重复松开、失焦；明暗主题各覆盖颜色／几何／曲线、500 次相同状态去重、父容器不新增布局、固定内容大小、离开尾段、反向和旧时钟；六种生命周期中核对清理、恢复后拒绝旧输入及新手势可用。新增 2 项真实搜索页夹具断言，确认取消的项目行不会打开详情、改变安装任务所有者，门控恢复不会激活旧键盘手势。

联跑时，上一轮胶囊检查固定等待 210ms 后断言完成，在前置暂停时钟夹具后出现失败。WPF 首个动画 tick 不保证与 Stopwatch 同时开始；现在限时等待实际无时钟／无动画属性状态。仍核对相同终态，仍有超时；既有数值时长和舍入断言不变。没有放宽通过计数或移除断言。

当前策略 `PCL-DB-checks/2026-10-23.9`：Core 5741、UI 799、motion 637、pack-search 195，四套合计 7372 项断言。control-motion 263 已包含在 motion 中，不另计；工具契约另有 97 项。固定 PCL 源码／资源和验证进程工具继续运行。最终结果与摘要以回执为准。

```powershell
./build.ps1
# 定向检查通过 VerificationProcessTools.psm1 等待真实进程退出。
```

忽略目录 `artifacts/resource-row-motion/` 保存源码、修复前失败、定向检查、明暗项目行和搜索页截图、最终构建与回执。`validation.json` 和 `committed-validation.json` 绑定测试源码、程序摘要、报告及最终提交。

这是离线夹具、受控输入和有效 WPF 隐藏呈现源验证，截图不是原生桌面交互验收。没有真人帧率、真实联网下载或游戏启动的新证据；本轮不产出发行工件。
