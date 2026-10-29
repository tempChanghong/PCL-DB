# 多源搜索退出与请求收尾

基线：`72326b8418b88f63cd08a0b05c65eabae8e360cf`。本轮只修改 Core 搜索会话的异常退出收尾，保留 VB 界面、动画、分页规则、net48、配置和资源。

## 问题与证据

多源搜索会先展示已完成来源的预览，再等待另一来源。原实现遇到同步 `IProgress.Report` 抛错时，只给剩余任务附加故障观察 continuation，随后释放会话 gate；既未取消，也未等待这些请求。公开客户端 handler 的隔离探针复现：方法返回后仍有一个响应体存活、文件保持独占；同会话立即重试使活动响应体增至两个，重试结束后旧请求仍在运行。调用方取消时，延迟结束的读取也可能在方法返回后继续持有资源。

没有观察到结果或游标损坏，因此不将该问题写成已经发生的数据污染。同步 Core 预览回调错误与 VB UI 的异步呈现错误也不同：UI 的 Progress 会投递到 Dispatcher，本轮没有声称接管所有 UI 呈现异常。

## 修复与保持

每轮查询创建独立的 linked CTS，并保存全部已启动任务。预览异常或查询取消时，先按已有安全通知方式取消本轮请求，再异步等待全部任务；观察清理异常后，优先保留原调用方取消 token，否则重新抛出原始预览故障。等待完成后才释放 linked CTS、会话 gate，调用方原有 Using client 因而不会提前结束。

所有等待继续使用 `ConfigureAwait(false)`，不在 UI 上同步 Wait，也不新增后台收尾任务。正常平台故障仍作为该来源的 Failure，允许健康来源继续完成和发布部分结果；不会因为一个平台失败就取消其他平台。

排序、各源原始偏移、结束／错误标记、去重与缓存保持。当前未完成轮次不能提交；同一次分页调用之前已完成的有效轮次继续保留。没有改成整次调用全部回滚，也不自动重试已冻结的失败平台。

## 源码依据

- PCL2 固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`，`Modules/Resource/ResourceSearcher.vb:407-449`：结果处理前逐个 Join，退出时 Finally 清理仍存活的线程，并区分单源失败和可用结果。
- C# 重点参考 PCL-CE `dev` `915ac29795efbdf6373417389e181e1504117048`，`Modules/Minecraft/ModComp.cs:2624` 附近：等待全部来源，检查中止，再处理结果。当前异步实现使用取消与 await，未照搬 UI 同步阻塞。
- VB 调用端 `MainWindow.ModpackSearch.vb` 在 Using client 内 await 查询，旧／新 request 和 session 已有身份门控。本轮保留它们的生命周期和返回／加载动画规则。

## 验证

新增 `SearchSessionCleanupChecks.vb`，29 项事件门控回归，包括同步预览异常、调用方取消、有／无预览、原异常实例和堆栈、原 token、响应／文件所有权、同会话重试等待 gate、缓存不重复请求、部分来源失败、已完成轮次保留和当前轮次重抓。新测试配旧 Core 的负控制按预期失败；修改后的真实 VB 测试宿主专项全部通过。

复验专项：

```powershell
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression search-session-cleanup
```

最终完整验证与提交绑定见 `artifacts/search-session-cleanup/validation.json`：原有 VB 检查全部保留，加入本轮后为 7379 项；旧 Beta 8 已编译测试 EXE 继续运行 7305 项；还核对公开／友元接口、资源和配置样本，并执行最终单 EXE 的冷启动及完整搜索回归。套件有重叠，不累计为独立场景数。

复现探针、失败回执、状态／UI 调用边界审查和最终构建各自保留，先前探针不冒充最终候选验证。测试使用临时文件、受控响应和隐藏 WPF，不修改真实启动器配置或实例，不进行新的完整在线游戏安装或帧率验收。未打包、推送或发版。
