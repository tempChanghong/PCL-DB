# 整合包详情的复制与网页操作

基线 `210a2d0933a90056c0b622ef2c83eac5746e7ecd`。本轮处理详情页外部操作的失败恢复及动画联动；验证后本地 Commit，暂不打包、推送或发版。

## 问题与源码

原“复制名称”直接调用 Clipboard.SetText，浏览器按钮直接调用 Process.Start。与已有关于页、任务结果操作不同，这两条点击路径没有异常边界。向原复制入口注入剪贴板占用异常后，137 项搜索检查通过，新增断言失败：调用一次，异常逸出。全局异常处理会接到该错误，不能把这次取证说成进程崩溃。原回执及观察在 `artifacts/project-action-recovery/before-search.json`、`project-action-before.json`。

读取固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 PageDownloadCompDetail.xaml(.vb)、ModBase.vb Git 对象。详情按钮使用 ClipboardSet / OpenWebsite；ClipboardSet 限四次、间隔 200 ms，并容纳最终异常；OpenWebsite 失败后尝试复制网址并提示手动访问。规范化源码和摘要在本轮 `source/`、`source.json`。

## 实现

复制名称与网页操作统一经过详情页操作边界。名称和网址取自点击当时已经接受、显示的项目元数据；剪贴板最多四次尝试，三次等待采用异步 Task.Delay，不 Sleep UI 线程。沿用项目已有 Forms 依赖的 SetDataObject(data, true, 0, 0)，在 STA 上写入 UnicodeText，并将重试节奏放在外层；没有另行清空剪贴板。关于 retryTimes/retryDelay 及 STA 要求，另核对了 [Microsoft .NET Framework 文档](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.clipboard.setdataobject?view=netframework-4.8.1)。

打开浏览器失败后按同一限次机制尝试复制原网址。反馈区分浏览器成功、网址复制成功、两者均失败；最后一种显示可手动访问的网址。操作反馈在原项目卡片内按现有文字样式显示，平时折叠，不改变初始页面布局。此处使用本程序的局部反馈，没有复制 PCL 的完整全局提示队列。

每次动作绑定项目对象与操作代次；新动作、项目元数据替换、详情隐藏、切页往返和关闭会阻止旧重试继续写剪贴板或反馈。输入门控、禁用和命名弹层期间拒绝新动作。错误不写入版本列表的加载错误提示，搜索请求、版本卡片和安装任务代次继续由原逻辑管理。

## 验证

新增 24 项离线检查：永久占用、第三次成功、200 ms 等待槽位的次数、异步等待、已核对名称/网址、浏览器回退与双失败、输入门控、禁用、命名弹层、后续操作覆盖、同项目离开再进入、元数据替换、换项目、关闭、设置往返，以及蓝色/深色反馈布局。

使用真实 PclSurfaceMotion 卡片入场时钟，在 175 ms 取样，证明等待中的动作允许入场继续推进，动作完成不替换页面时钟。另走实际设置页返回，检查旧动作失效。截图 `project-copy-failed.png`、`project-action-entry-175.png`、`project-copy-blue.png`、`project-copy-dark.png` 供视觉检查。

当前门槛 `PCL-DB-checks/2026-10-24.28`：UI 973 / motion 913 / pack-search 359。固定源码/资源、97 项验证契约与 19 项进程工具另查。完整回执、最终源码/程序摘要及 Commit 绑定存于本轮 `validation.json`、`committed-validation.json`。

剪贴板和浏览器操作使用故障注入；没有改动用户剪贴板或启动真实浏览器。真实系统占用持续时间、设备输入和桌面帧率没有验收。本轮没有改下载/安装内核或打包实现，没有重跑未改变的 Core 套件。
