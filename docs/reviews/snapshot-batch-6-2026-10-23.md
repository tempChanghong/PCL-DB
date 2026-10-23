# 旧快照第六批与 1.2 文件路径映射

本轮接入十一项字段完整的历史快照，补充支持从 55 项增至 66 项：58 项旧快照、5 项远古补充和 3 项 2.0 三色版。按归档研究清单统计，历史候选累计接入 63/171 项，剩余 108 项（41 项字段完整、67 项需要补全）。统计对应研究档案，不代表在线目录永不变化。

## 本批版本

|游戏 ID|文件路径 ID|启动配方与资源索引|
|---|---|---|
|12w22a|同游戏 ID|LaunchWrapper 1.5 / 默认 VanillaTweaker / pre-1.6|
|12w21a|同游戏 ID|同上|
|12w21b|同游戏 ID|同上|
|12w19a|同游戏 ID|同上|
|12w18a|同游戏 ID|同上|
|12w17a|同游戏 ID|同上|
|12w16a|同游戏 ID|同上|
|1.2|1_2|同上|
|12w08a|同游戏 ID|同上|
|12w07a|同游戏 ID|同上|
|12w07b|同游戏 ID|同上|

十一份原始 UVMC 元数据先与研究档案记录的 SHA-256 核对，再原字节加入测试资源。它们均自带完整库下载声明，索引 SHA-1 均为 `4759bad2824e419da9db32861fcdc3a274336532`，各自保留不同的客户端摘要。归档 SHA-256 仅用于本地夹具核对，不能代替 UVMC 未公布的元数据摘要。

## 1.2 的身份、分类与冲突边界

清单和元数据 ID 都是 `1.2`，元数据 URL 是 `files/1_2/1_2.json`，客户端 URL 是 `files/1_2/1_2.jar`。本轮增加明确、逐项的元数据路径映射，下载后的游戏 ID、`clientVersion` 和显示名称仍为 `1.2`；不批量替换其他 ID 中的句点。`1_2` 仅是文件路径，不注册为版本别名，也不与普通 `1.2.1` 合并。

固定 PCL 的 `ShowMinecraftVersionList` 会纠正普通清单中部分 `1.*` snapshot 的分类。PCL-DB 已有的补充展示分支在此纠正前返回，本轮保留 UVMC 的 `snapshot` 原始分类。它继续显示于「历史快照补充」、使用命令方块图标，不进入普通正式版或最新推荐，也不触发模组加载器查询。

UI 回归加强为逐项检查全部历史补充的显示 ID、分类和图标，而不是仅抽查一个普通周快照，覆盖 `1.2` 这样的数字外观 ID。核心检查同时覆盖精确映射 URL、官方同名优先，以及拒绝把 `1_2` 等路径 ID 当作版本别名。

## 固定 PCL 源码对照

参考提交 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`，本地参考与对应 Git 对象在标准化行尾后核对：

- `ModDownload.vb / DlClientListBmclapiMain`：合并附加清单后继续普通流程。补充请求仍独立限时，失败不阻塞普通下载。
- `ModDownloadLib.vb / McDownloadClientLoader`：库／资源阶段并行、JSON 保留 `clientVersion`。本批复用现有下载器、共享预算和安装事务。
- `ModDownloadLib.vb / McDownloadListItem`、`PageDownloadInstall.xaml.vb / ShowMinecraftVersionList`：分类、日期排序、命令方块图标、42 DIP 行与懒加载；分类纠正与元数据身份分别处理。
- `ModMinecraft.vb / McLibListGetWithJson`、`McAssetsListGet`：Windows 库规则及有效隔离目录的 `resources` 映射，区别于根目录 `assets/virtual/legacy`。
- `ModLaunch.vb / McLaunchArgumentsReplace`：游戏目录与资源目录变量保留各自角色，继续使用已有启动配方校验。

源码链接：[ModDownload](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModDownload.vb)、[ModDownloadLib](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/ModDownloadLib.vb)、[PageDownloadInstall](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/PageDownloadInstall.xaml.vb)、[ModMinecraft](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb)。

## 验证与证据

```powershell
./tools/verify-supplemental.ps1 -Install -VersionIds '1.2','12w22a','12w21a','12w21b','12w19a','12w18a','12w17a','12w16a','12w08a','12w07a','12w07b' -OutputDirectory './artifacts/snapshot-batch-6/live-install'
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-versions
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-recipes
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-candidates
./build.ps1
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --live-supplemental './artifacts/snapshot-batch-6/live-metadata'
```

验收包含定向回归、全仓核心／UI／动画／搜索检查、全部补充版本的在线元数据和索引检查，以及十一项新增版本的真实联网安装。安装使用新的私有游戏目录，不读取外部游戏目录作复用源；后续项仅共享本次运行已校验的文件。逐项核对文件摘要、隔离资源路径、发布前取消、同名重试、UTF-16 JSON／用户参数／隔离设置原字节保留及损坏客户端补全。

所有注册快照继续参加共享资源冲突回归：隔离安装保留根目录不同内容，关闭隔离遇到同名异内容时拒绝发布；测试明确纠正冲突后才能重试。私有索引名、原子实例发布和缺失字段回退边界不变。源码、固定源码核对、进程、联网安装与最终验证回执保存在忽略目录 `artifacts/snapshot-batch-6/`，以最终成功回执为准。

本轮没有 PCL GUI、游戏启动、Java 兼容性或存档验收证据。其余历史候选、Classic／Indev 目录适配以及游戏内资源与创建／保存／重进世界仍需后续验证。本轮不打包、不发版。
