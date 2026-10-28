# 管理目录切换与取消回调交接

基线：`8d29fbed02d9aece5e22741884e8f650111d663b`。

## 源码依据

读取 PCL 固定提交 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 Git 原始对象：

- `ModLoader.State` 先记录新的状态，再通知取消。
- `PageInstanceMod` 按当前实例重新读取本地 Mod，页面退出清理选择。
- `FormMain.PageChangeAnim` 清理旧页面后安排当前页面进入，返回标题与当前页面对应。

原始文件与摘要见 `artifacts/management-target-handoff/source/manifest.json`。多启动器绑定和异步请求代次是 PCL-DB 的实现；本轮保留原有 PCL 风格布局和动画时序。

## 问题与修复

管理目录切换原先先调用管理读取、Mod 读取的取消，再清理旧目录数据及页面。取消通知可以同步触发新的操作：进入同一物理目录下另一启动器的实例、打开新的 Mod 页，或转去设置。外层随后继续清理，会覆盖已接受的实例与行列表，并可能结束新页面的动画。取消观察者报错也可能从目录选择事件向外抛出。

现在先捕获并退役两组旧请求，发布旧目录的清理状态与页面，再统一发送取消通知。发送后不再清理新数据或结束页面动画。所有已捕获通知都会尝试执行；观察者异常进入切换失败边界。错误反馈只属于仍匹配的目录、管理／Mod 请求代次及主页面导航代次，不能覆盖更新的操作。

## 验证

最终同一夹具覆盖三种启动器 × 两种旧读取 × 四种回调行为，每例检查选择绑定、页面／动画归属及旧结果结束后的重试和文件保护，共 72 项。基线 36 项失败、36 项通过，修复后全部通过。覆盖同根目录不同绑定、明暗外观、动画启停、取消观察者异常、设置页接管和再次换绑定。各启动器按原生实际 Mods 目录验证，没有把 HMCL 的共享目录硬套为 PCL 的隔离目录。

同一最终强制重建 EXE 的相邻管理退役／入口错误／实例选择／页面发布／目录快捷操作／任务刷新、资源安装目标／搜索返回／输入重入，以及任务页／安装动作／返回动画专项均通过。精确计数保存在 `artifacts/management-target-handoff/validation.json`，不将有重叠的组相加作为独立覆盖总数。构建零警告、零错误；验证契约、限时工具、验证进程及固定 PCL 来源检查通过。

静态截图（离屏 WPF）：

- [基线：新实例 Mod 页被清理回列表](../../artifacts/management-target-handoff/baseline-complete-Target/target-handoff.png)
- [修复：保留新实例 Mod 页](../../artifacts/management-target-handoff/final-Target/target-handoff.png)

夹具使用临时原生配置、实际 JAR 格式和受控的异步读取，不写用户目录、外部配置或真实网络服务；文件检查保留已有内容。时钟归属和静态截图不能代表桌面帧率；没有重跑完整 Core、all、motion、navigation、pack-search 或单文件分发验证。

新增检查已注册到 navigation、motion，all 通过 navigation 包含该组。策略 `PCL-DB-checks/2026-10-28.27` 注册数量分别为 all 6403、motion 5788、navigation 4192、pack-search 3132，注册数不是本轮执行数。源码、EXE、内嵌文档、截图、回执与提交的对应关系见 `validation.json` 和 `committed-validation.json`。未推送、打包或发版。
