# 旧快照第五批：2012 年 6–7 月

本轮接入十一项字段完整的历史快照，补充支持从 44 项增至 55 项：47 项旧快照、5 项远古补充和 3 项 2.0 三色版。按归档研究清单统计，历史候选累计接入 52/171 项，剩余 119 项（52 项字段完整、67 项需要补全）。数量对应研究档案，不保证在线目录永不变化。

## 本批版本与身份

|ID|启动配方|资源索引|
|---|---|---|
|12w30a|LaunchWrapper 1.5 / 默认 VanillaTweaker|pre-1.6|
|12w30b|同上|同上|
|12w30c|同上|同上|
|12w30d|同上|同上|
|12w30e|同上|同上|
|12w27a|同上|同上|
|12w26a|同上|同上|
|12w25a|同上|同上|
|12w24a|同上|同上|
|12w23a|同上|同上|
|12w23b|同上|同上|

原始 UVMC 元数据先与研究档案记录的 SHA-256 逐份核对，再原字节加入测试资源。十一项均有完整的库下载声明，索引 SHA-1 均为 `4759bad2824e419da9db32861fcdc3a274336532`，各自保留不同的客户端摘要。归档元数据中的 `12w30a/b`、`12w30d/e`、`12w23a/b` 分别同日发布；以完整 ID 区分，不按日期或周号合并身份，也不根据字母后缀猜日期。

归档 SHA-256 用于核对本地夹具；UVMC 清单没有公布元数据 SHA-1 时不虚构摘要。没有引入缺失库或索引回退，也没有更换旧快照的启动入口。

## 固定 PCL 源码对照

固定提交 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`，本地参考与对应 Git 对象在标准化行尾后核对：

- `ModDownload.vb / DlClientListBmclapiMain` 合并附加清单后复用普通流程。PCL-DB 继续独立、限时读取补充源；官方同名优先，补充失败不阻塞普通下载。
- `ModDownloadLib.vb / McDownloadClientLoader` 使用并行的库／资源阶段并保留 `clientVersion`。本批直接进入已有下载器、共享下载预算和安装事务。
- `ModDownloadLib.vb / McDownloadListItem`、`PageDownloadInstall.xaml.vb / ShowMinecraftVersionList` 保留分类、来源日期排序、命令方块图标、42 DIP 行与懒加载。新增项进入现有「历史快照补充」卡片，沿用页面和选择动画。
- `ModMinecraft.vb / McLibListGetWithJson` 按 Windows 规则筛选普通库和原生库；本批保留完整的下载声明和规则。
- `ModMinecraft.vb / McAssetsListGet`、`ModLaunch.vb / McLaunchArgumentsReplace` 区分有效游戏目录的 `resources` 与根目录 `assets/virtual/legacy`；继续由现有隔离计划和启动配方校验保护目录变量。

源码链接：[ModDownload](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModDownload.vb)、[ModDownloadLib](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/ModDownloadLib.vb)、[ModMinecraft](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb)、[ModLaunch](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModLaunch.vb)。

## 冲突处理与验证范围

只在只读快照注册表中新增已核对的 ID，不按版本前缀自动开放。分类仅接受 `snapshot`；独立读取真实元数据的身份集合必须与注册表一致，明确覆盖数量从 36 更新到 47。普通版本和最新推荐保持原有身份，旧快照不请求模组加载器。

全部注册快照参与共享资源冲突回归：隔离安装保留根目录的不同内容；关闭隔离遇到同名异内容时停止发布；仅在测试明确纠正冲突后才同名重试。继续复用私有索引名、发布前取消和原子实例发布。已有实例补全检查 UTF-16 JSON、用户窗口参数和隔离设置的原字节保留，及损坏客户端修复。

```powershell
./tools/verify-supplemental.ps1 -Install -VersionIds '12w30a','12w30b','12w30c','12w30d','12w30e','12w27a','12w26a','12w25a','12w24a','12w23a','12w23b' -OutputDirectory './artifacts/snapshot-batch-5/live-install'
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-versions
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-recipes
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-candidates
./build.ps1
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --live-supplemental './artifacts/snapshot-batch-5/live-metadata'
```

本轮验收包含定向回归、全仓核心／UI／动画／搜索检查、全部补充版本的在线元数据与索引检查，以及十一项新增版本的真实联网安装。安装使用新的私有游戏目录，不读取外部游戏目录作复用源；后续项只共享本次运行已校验的文件。工具保留每批 1–32 个 ID 上限，全量在线元数据使用独立入口。

源码清单、固定源码核对、进程回执、实际安装与最终验证汇总保存在忽略目录 `artifacts/snapshot-batch-5/`，以最终成功回执为准。重新序列化的在线元数据仅用于诊断，不等于上游原始字节或公开摘要。

本轮不包含 PCL GUI 操作、游戏启动、Java 兼容性或存档验收；游戏内资源及创建／保存／重进世界仍需单独验证。其余历史候选及 Classic／Indev 目录适配不在本批，不打包、不发版。
