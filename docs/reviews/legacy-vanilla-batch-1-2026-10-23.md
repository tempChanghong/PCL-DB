# 远古版本补充：legacy 索引与默认 Vanilla 配方

本轮接入 `b1.1-1`、`b1.1-2` 和 `a1.1.0-2`，延续[上一批报告](beta-batch-2-2026-10-23.md)的分组与验证计划。累计支持 86 项补充版本：67 项历史快照、16 项远古补充和 3 项 2.0 三色版。归档历史候选支持 83/171 项，剩余 88 项（21 项字段完整、67 项缺少库／索引声明）。

## 本批元数据

|ID|原始分类|元数据日期（UTC）|客户端大小|客户端 SHA-1|
|---|---|---|---:|---|
|b1.1-1|old_beta|2010-12-21|1129358|366af727742cff91e37af5cb3155927dfe44fc66|
|b1.1-2|old_beta|2010-12-21|1129359|e6499cd774ebc3f0b633d6fdd8c5a84149ad0aad|
|a1.1.0-2|old_alpha|2010-09-12|873031|66e8319d9b34662cdaf985ec3c1e0e926fcad46e|

三项均使用与 ID 相同的 UVMC 文件路径、LaunchWrapper 1.5、默认 VanillaTweaker、13 项完整库声明和 SHA-1 为 `c0fd82e8ce9fbc93119e40d96d5a4e62cfa3f729` 的 legacy 索引。客户端大小在原始 JSON 中是字符串，沿用现有解析器，不改写原始夹具。归档夹具逐份与研究档案 SHA-256 核对，不把本地归档摘要当作 UVMC 公布的元数据摘要。

两个 b1.1 修订日期相同，大小相差一字节，仍保留不同 ID 与客户端摘要，不归并为 `b1.1`。`a1.1.0-2` 属于 old_alpha 分类，但使用默认 Vanilla 配方；分类不决定 Tweaker。新增只读 Alpha 身份注册表，与既有 Beta 注册表一样只开放逐项确认的 ID。Alpha 四项、Beta 十二项与历史快照六十七项分别与归档夹具集合核对。

RC1／Infdev／Classic 的 LaunchWrapper 1.6 特例保持原有边界；本批不添加库或资源索引的缺字段回退，不开放 `a1.1.0-1` 等尚未验证的显式 Alpha 配方。

## 固定 PCL 源码对照与资源路径

参考提交 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`，五份本地参考与 Git 对象在标准化行尾后核对：

- `ModDownload.vb / DlClientListBmclapiMain`：附加清单合并后复用正常安装流程；保留官方同名优先、补充请求独立限时和失败隔离。
- `ModDownloadLib.vb / McDownloadClientLoader`：库与资源阶段并行，记录 `clientVersion`，继续使用现有选源、共享预算、强摘要校验与原子实例发布。
- `ModDownloadLib.vb / McDownloadListItem`、`PageDownloadInstall.xaml.vb / ShowMinecraftVersionList`：old_alpha／old_beta 都归入远古类别，使用圆石图标、42 DIP 行和日期排序。本批进入已有远古补充卡片，保留两个同日修订及其独立选择，不进入最新推荐，不重建普通版本行。
- `ModMinecraft.vb / McAssetsListGet`：`map_to_resources` 跟随有效隔离目录；本批的 `virtual=true` 则映射至根目录 `assets/virtual/legacy`。强摘要核对的真实 legacy 索引有 1,120 个条目、596 个不同对象，计划保留这些对象缓存及 virtual 别名，不生成实例 `resources` 映射。
- `ModLaunch.vb / McLaunchArgumentsReplace`：`${game_directory}` 取有效隔离目录，`${game_assets}` 取根目录 `assets/virtual/legacy`。补充索引继续使用完整 SHA-1 的私有名称，但资源目录不能随该名称改变，原始 `assets=legacy` 语义保留。

源码：[ModDownload](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModDownload.vb)、[ModDownloadLib](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/ModDownloadLib.vb)、[PageDownloadInstall](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/PageDownloadInstall.xaml.vb)、[ModMinecraft](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb)、[ModLaunch](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModLaunch.vb)。

## 验证与复现

```powershell
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-versions
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-recipes
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-candidates
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-early
./tools/verify-supplemental.ps1 -Install -VersionIds 'b1.1-1','b1.1-2','a1.1.0-2' -OutputDirectory './artifacts/legacy-vanilla-batch-1/live-install'
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --live-supplemental './artifacts/legacy-vanilla-batch-1/live-metadata'
./build.ps1
```

元数据回归核对三项原始索引、客户端摘要、字符串大小与分类，拒绝跨版本 URL、含糊 ID、猜测别名及未接入的兄弟版本。注册表不可由调用方修改，Alpha 分类不会误选 Alpha Tweaker；普通同名声明继续优先。

新增 virtual 受控测试逐项运行真实安装器的安装、发布前取消、同名重试和损坏补全，再检查开启／关闭隔离均使用根目录 virtual 缓存；用户根目录和实例内的 `resources`、已有 UTF-16 JSON／INI 保持原字节。私有索引不能覆盖普通 `assets/indexes/legacy.json`，也不能改变 virtual 目录。损坏 virtual 别名从已校验对象复用；目录占用目标文件时拒绝实例发布，显式纠正测试目录后才能同名重试。受控测试使用真实固定摘要的 wrapper 与合成客户端／资源，不计作真实联网安装。

既有 `map_to_resources` 冲突回归扩展至全部 67 项快照、12 项 Beta 和 4 项 Alpha；在有效隔离目录保留不同的共享资源，关闭隔离时拒绝覆盖及发布，明确纠正测试冲突后再重试。动画、搜索、已有实例补全、组件安装与设置保留继续参加全套回归。

联网验收在新目录安装本批三个真实版本，逐项核对全部计划文件摘要与根目录 virtual 资源落点，并验证取消、同名重试、损坏客户端补全及已有设置保留。无外部游戏目录复用；后续版本只共享本轮已校验文件。开发者验证报告新增 `virtualResources` 字段，区别于 `mappedResources`。另检查全部 86 项补充版本与普通 `rd-132211` 的在线元数据／索引，不把这一检查称为 86 项全部重新安装。

忽略目录 `artifacts/legacy-vanilla-batch-1/` 保存归档摘要、真实索引检查、源码清单、固定 PCL 核对、进程、联网安装、全套构建与最终成功回执。最终数量、文件计划与通过状态以 `validation.json` 和 `live-binding.json` 为准，源码清单与二进制摘要绑定本轮验证。在线元数据的重新序列化副本仅作诊断。

本轮不启动游戏，不提供 PCL GUI、Java 兼容性、游戏内资源或创建／保存／重进世界的人工验收结论；不打包、不发版。

## 下一步

字段完整的默认 Vanilla 分组已接入完毕。剩余 21 项完整候选为 19 项 LaunchWrapper 1.5／显式 AlphaVanillaTweaker，以及两项 LaunchWrapper 1.6／Classic。下一轮应先选择显式 Alpha 的代表版本，核对 wrapper 参数与目录语义，建立独立配方及回归后逐项开放；缺字段的 67 项仍要求独立补全依据。
