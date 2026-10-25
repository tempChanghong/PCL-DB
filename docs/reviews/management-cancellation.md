# 本地 Mod 管理取消与调试中断

基线 `52ccae9e28f745e5ac875f4844fbcb9d9bffc50c`。处理用户提供的 LocalModCatalog.ReadMetadata 第 158 行取消异常截图，保持原扫描、元数据识别、三启动器隔离、共享目录、文件冲突与原子发布语义；本地提交，暂不推送、打包发版。

## 定位与范围

截图中 CancellationToken.IsCancellationRequested=True，VS 标记“用户未处理的 OperationCanceledException”。首次只读检查时进程 71112 仍存在；当时的 Application 日志未找到对应 PclDownloadBridge 的 .NET Runtime/Application Error 崩溃事件，程序日志也没有这次失败记录。因此确认的是后台取消检查触发调试中断，不能仅凭截图确定独立进程闪退；独立闪退本轮未复现，也没有操作用户当前的 VS 调试会话。

原界面在 Await 外层捕获取消，但工作委托直接调用 Core，取消异常先越过用户委托到达 Task 框架，再由等待方处理。Microsoft 说明，仅我的代码可以在用户异常进入框架处理时报告用户未处理中断，异步场景亦有专门处理：[调试异常说明](https://learn.microsoft.com/en-us/visualstudio/debugger/managing-exceptions-with-the-debugger?view=visualstudio)、[异步用户未处理异常说明](https://devblogs.microsoft.com/visualstudio/break-for-async-user-unhandled-exceptions-in-the-visual-studio-debugger/)。这与进程最终未捕获异常是两个不同证据层级。

参照固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 ModLoader：后台线程内处理取消、旧线程不发布结果、真正失败独立记录。本工具增加后台任务边界，工作委托内识别同一请求且已发出的取消，转换为 Canceled 任务；真实错误和其他令牌的取消明确保留为 Faulted。返回值仍在界面原有代次、实例、目录及可见性检查后发布。

同步 Core 中的 ThrowIfCancellationRequested 保留，不将取消伪装为空列表或成功。实例扫描、实例配置重读、本地 Mod 扫描、导入与启停接入同一后台边界。界面捕获取消增加本请求已取消条件，修复无取消请求的 OperationCanceledException 被静默吞掉、界面残留“正在读取”的情况；异常显示读取失败并允许重试，真实生产错误写入既有 logs 目录。未全局忽略取消或修改 VS 异常设置。

## 验证

新增 11 项专项：后台线程成功返回；预取消不执行；运行中取消保持自身令牌和 Canceled 状态；其他令牌的取消仍是错误；读错误与取消并发时保留真实异常；真实临时 JAR 扫描；扫描中返回；返回后重试；意外取消显示错误并可刷新；失败后重试；旧取消任务迟到不覆盖新列表/输入状态。

Release 与隔离输出的 Debug 构建零警告零错误。策略 `PCL-DB-checks/2026-10-25.13`：完整界面 1,230、导航 239、动画 1,285、搜索详情 607，Core 6,961。验证收据绑定最终程序与测试宿主摘要；固定 PCL 源码/资源与验证契约另验。本轮没有修改 Core 源码，但仍对当前构建重跑完整内核回归。

证据在 `artifacts/management-cancellation/`：专项收据、完整回归、构建日志、固定上游源码、验证摘要、测试源码清单及提交核对。测试使用临时实例与隐藏 WPF 表面，不写用户 `.minecraft`，不执行游戏或 Mod，不代表真人 VS 操作和独立闪退的复现。没有停止、继续或重启用户当时的调试会话。

默认 VS 用户未处理路径的后台边界已补齐；若用户另行勾选“引发时中断”，调试器仍可能按设置停在取消检查。更新后需停止旧调试会话、重新生成再 F5。若仍发生真正退出，依据 `%LOCALAPPDATA%\PclDownloadBridge\logs\yyyy-MM-dd.log` 与 Windows Application 事件继续定位，不能将所有退出归因于本次取消截图。
