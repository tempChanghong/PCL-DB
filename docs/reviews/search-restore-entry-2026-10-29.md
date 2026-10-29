# 缓存搜索与本地 Mod 入口交接

基线 `320bbeca65173cfd316eb9e2c430ce550386f6de`。本轮业务修改只在 CommunityResources 的分类进入事务；保留 Core、下载／安装、实例隔离、外部配置、动画控制器和时长。

从本地 Mod 管理点击“下载新 Mod”，若目标 Mod 分类保存着未完成的旧搜索，SelectResourceKind 原先先恢复旧搜索，再执行 ConfigureLocalModSearch。受控 HTTP 实测四个明暗×动画场景均提交一次旧查询，随后又取消；新查询才使用当前实例的版本和加载器。这些多余请求并非当前实例所需。

另一个可复现问题发生在真实 LayoutUpdated 通知：分类正在更新布局时若已转到设置，旧事务仍执行配置，覆盖新输入，并留下隐藏读取。旧代码每个场景配置一次、旧读取一次；修复后均为零，设置页与新输入保留。

现在先完成仍有效的布局／滚动和配置，再判断是否需要恢复原缓存。进入前捕获分类、资源、导航代次，主页面、模式、会话及名称／搜索／图片请求；在布局、滚动和配置边界复查。配置绑定其他实例、重置会话或接受新导航后，不再读取旧缓存。普通未完成缓存仍读取一次，结果生成实际布局后恢复 240 的阅读位置；失败保留重试反馈，显式重试可重新成功。已完成详情继续走上一批受保护的恢复分支。

PCL2 固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 PageInstanceMod.xaml.vb:335–340 先设置 TargetInstance／TargetName 再 PageChange；FormMain 在框架 UI 更改前设置 PageCurrent，MyPageRight 保持完成 UI 与 loader 状态的顺序。CE 固定 `915ac29795efbdf6373417389e181e1504117048` 的 PageInstanceCompResource.xaml.cs:1435–1456 也将资源下载与实例筛选联动，但它在 PageChange 后设置 targetVersion；本轮以 PCL2 的预绑定顺序为主，并未照抄 CE 的顺序。16 份 Git 原始字节与摘要分别见 `artifacts/search-restore-entry/source/manifest.json`（15）及 `extra-ce-manifest.json`（1）。

同一份新增 VB 检查、私有入口和冻结 Core／JSON，两侧只改一个业务文件：旧代码 16/28、12 项失败，修复后 28/28。24 项覆盖真实“下载新 Mod”入口及布局重入的明暗×动画组合；另 4 项覆盖正常缓存、保存视口、失败与重试。保留所有旧检查，新文件由完整搜索入口调用。首次夹具未建立实际滚动位置，导致一项普通视口检查额外失败；补上实际列表和滚动后，该项在旧代码即通过，原始记录保留。中间候选的 VB 局部变量重名编译失败也保留日志，不计作成功运行。

策略 `PCL-DB-checks/2026-10-29.11` 仅搜索增加 28 至 3444。最终正式 EXE 检查单 EXE 25、完整搜索 3444、内嵌两依赖与七份根文档；另查 VS2026 Debug／三 Portable PDB、验证契约 110。Core 输入和 Release 字节未变，精确复用 `3c143de61af42a3f9814ffac26e729a7259ba8a0` 的 Core 7814、旧 VB 宿主 7305、公开／Friend API 979／1245、资源 2、JSON 15；这些不是本轮重新运行结果。UI 6662／motion 5976 未重跑。

源码／EXE／完整回执及最终提交树绑定见 `artifacts/search-restore-entry/validation.json`。仅主代理自查，不冒称独立复核。未覆盖同分类配置的任意重入、所有行渲染回调、自然播放帧率、真实联网安装或真人桌面验收；未改用户实例或外部配置，未打包、推送、发版。
