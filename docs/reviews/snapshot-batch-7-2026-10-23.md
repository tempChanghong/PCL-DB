# 旧快照阶段收尾：第七批与完整清单

本轮接入最后九个归档 snapshot，补充支持从 66 项增至 75 项：67 项旧快照、5 项远古补充和 3 项 2.0 三色版。历史候选累计接入 72/171 项，剩余 99 项。这里的“完整”仅指归档研究清单中的 67 项 snapshot，不表示所有 Minecraft 历史版本都已支持，也不表示已完成游戏启动或存档验收。

## 本批版本

|ID|元数据日期（UTC）|启动配方与资源索引|
|---|---|---|
|12w06a|2012-02-09|LaunchWrapper 1.5 / 默认 VanillaTweaker / pre-1.6|
|12w05a|2012-02-02|同上|
|12w05b|2012-02-03|同上|
|12w04a|2012-01-26|同上|
|12w03a|2012-01-19|同上|
|12w01a|2012-01-05|同上|
|11w50a|2011-12-15|同上|
|11w49a|2011-12-08|同上|
|11w48a|2011-12-01|同上|

九份原始 UVMC 元数据先与研究档案记录的 SHA-256 核对，再原字节加入测试资源。它们均有完整库下载声明，索引 SHA-1 为 `4759bad2824e419da9db32861fcdc3a274336532`，各自保留不同的客户端摘要。不根据版本号猜日期、合并同周版本或增加缺失字段回退。归档 SHA-256 只核对本地夹具，不充当 UVMC 未公布的元数据摘要。

## 阶段清单与覆盖核对

|接入批次|新增旧快照|累计|
|---|---:|---:|
|既有 13w12~|1|1|
|[第一批](snapshot-batch-1-2026-10-23.md)|5|6|
|[第二批](snapshot-batch-2-2026-10-23.md)|10|16|
|[第三批](snapshot-batch-3-2026-10-23.md)|10|26|
|[第四批](snapshot-batch-4-2026-10-23.md)|10|36|
|[第五批](snapshot-batch-5-2026-10-23.md)|11|47|
|[第六批](snapshot-batch-6-2026-10-23.md)|11|58|
|本轮第七批|9|67|

以下为全部注册 ID，按接入批次排列；界面仍按清单来源日期排序：

```text
13w12~
13w10b 13w10a 13w09c 13w09b 11w47a
13w09a 13w07a 13w11a 13w06a 13w05b 13w05a 13w04a 13w03a 13w02b 13w02a
13w01b 13w01a 12w50b 12w50a 12w49a 12w42b 12w42a 12w41b 12w41a 12w40b
12w40a 12w39b 12w39a 12w38b 12w38a 12w37a 12w36a 12w34b 12w34a 12w32a
12w30a 12w30b 12w30c 12w30d 12w30e 12w27a 12w26a 12w25a 12w24a 12w23a 12w23b
12w22a 12w21a 12w21b 12w19a 12w18a 12w17a 12w16a 1.2 12w08a 12w07a 12w07b
12w06a 12w05a 12w05b 12w04a 12w03a 12w01a 11w50a 11w49a 11w48a
```

独立覆盖审计将研究清单中全部 `catalogueType=snapshot` 的 ID 与真实元数据夹具比对，要求两边均为 67 项、集合完全相等且没有重复。核心回归再将这些夹具的身份集合与生产只读注册表比对，避免只改数量或遗漏某项仍被当作完成。覆盖审计不要求既有夹具的 JSON 排版与研究档案字节相同；新增九项另外逐份核对归档 SHA-256。

## 固定 PCL 源码对照与边界

参考提交 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`，本地参考与对应 Git 对象在标准化行尾后核对：

- `ModDownload.vb / DlClientListBmclapiMain` 合并附加清单后复用普通下载流程；补充请求仍独立限时，失败不阻塞普通目录，官方同名优先。
- `ModDownloadLib.vb / McDownloadClientLoader` 保留库／资源并行阶段和 `clientVersion`；本批继续使用现有共享预算、缓存校验和安装事务。
- `ModDownloadLib.vb / McDownloadListItem`、`PageDownloadInstall.xaml.vb / ShowMinecraftVersionList` 提供类别、日期排序、命令方块图标、42 DIP 行与懒加载参考。本批进入现有历史补充卡片，保留页面动画、普通版本和最新推荐。
- `ModMinecraft.vb / McLibListGetWithJson`、`McAssetsListGet` 对照 Windows 库规则与资源映射；`map_to_resources` 使用有效隔离目录，区别于根目录 `assets/virtual/legacy`。
- `ModLaunch.vb / McLaunchArgumentsReplace` 对照游戏目录、资源目录变量。已有实例的原始 JSON、隔离设置和用户参数继续保留；不重写启动配方。

源码：[ModDownload](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModDownload.vb)、[ModDownloadLib](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/ModDownloadLib.vb)、[ModMinecraft](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb)、[ModLaunch](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModLaunch.vb)。

`1.2 → 1_2`、`13w12~ → 13w12-` 等明确路径映射继续保留，路径 ID 不自动注册为版本别名。补充 snapshot 不套用普通 `1.*` 分类纠正，逐项检查显示 ID、原始分类和图标。所有旧快照仍禁用模组加载器，不按前缀自动开放未来条目。

全部 67 项注册快照参加共享资源冲突回归：隔离安装保留根目录不同内容；关闭隔离遇到同名异内容时拒绝发布，测试明确纠正冲突后才同名重试。发布前取消、原子实例发布、私有索引名与缺失字段回退边界保持原有约束。

## 验证与证据

```powershell
./tools/verify-supplemental.ps1 -Install -VersionIds '12w06a','12w05a','12w05b','12w04a','12w03a','12w01a','11w50a','11w49a','11w48a' -OutputDirectory './artifacts/snapshot-batch-7/live-install'
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-versions
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-recipes
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-candidates
./build.ps1
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --live-supplemental './artifacts/snapshot-batch-7/live-metadata'
```

本轮验收包含独立覆盖审计、定向回归、全仓核心／UI／动画／搜索检查、全部 75 项补充版本的在线元数据与索引检查，以及新增九项的真实联网安装。既有批次的安装证据见相应报告，不把本轮九项安装称为 67 项全部重新安装。新目录不读取外部游戏目录作复用源；后续项只共享本次运行已校验的文件。逐项检查文件摘要、资源落点、发布前取消、同名重试、UTF-16 JSON／隔离设置原字节保留及损坏客户端补全。

覆盖审计、源码清单、固定源码核对、进程、联网安装与最终验证回执保存在忽略目录 `artifacts/snapshot-batch-7/`，以最终成功回执为准。重新序列化的在线元数据仅作诊断，不替代上游原始字节或公开摘要。工具每批最多 32 个 ID，全量在线元数据使用独立入口。

## 后续适合推进的工作

剩余 99 项分为：字段完整的 22 项 Alpha、10 项 Beta，以及缺库／索引的 63 项 Alpha、4 项 Beta。下一轮先按 LaunchWrapper 版本、tweaker、目录参数和资源索引对 32 项字段完整候选分组，再逐项验证代表版本，不直接套用 snapshot 的默认配方。67 项缺失字段候选继续要求独立补全依据，Classic／Indev 目录适配仍需单独处理。

还需在 PCL 中实际启动、核对 Java 兼容性、游戏内资源和存档落点，并完成创建／保存／重进世界验收。本轮自动化和联网安装不提供这部分证据，不打包、不发版。
