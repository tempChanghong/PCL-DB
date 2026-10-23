# 旧快照第三批：2012 年末至 2013 年初

本轮新增十个字段完整的历史快照，补充支持由 24 项增至 34 项：26 项旧快照、5 项远古补充、3 项 2.0 三色版。按归档研究清单计算，历史候选接入累计 31/171 项，剩余 140 项（73 项字段完整、67 项需要补全）。这些数量对应研究档案，不保证在线目录永不变化。

## 本批版本与配方

|ID|启动配方|资源索引|
|---|---|---|
|13w01b|LaunchWrapper 1.5 / 默认 VanillaTweaker|pre-1.6|
|13w01a|同上|同上|
|12w50b|同上|同上|
|12w50a|同上|同上|
|12w49a|同上|同上|
|12w42b|同上|同上|
|12w42a|同上|同上|
|12w41b|同上|同上|
|12w41a|同上|同上|
|12w40b|同上|同上|

十份原始 UVMC 元数据从核对 SHA-256 的研究档案加入测试资源。它们都有完整支持库下载信息，索引 SHA-1 均为 `4759bad2824e419da9db32861fcdc3a274336532`。每项保留自己的客户端摘要、版本 ID、来源日期和库规则；没有从编号推导日期，也没有为本批引入缺失库或索引回退。UVMC 清单未公布元数据摘要时不虚构 SHA-1；档案 SHA-256 用于核对本地夹具字节。

## 对照的 PCL 源码

固定提交 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`：

- `ModDownload.vb / DlClientListBmclapiMain`：附加清单合并后复用普通安装流程。PCL-DB 继续采用独立、限时的补充请求，补充源失败不阻塞普通列表。
- `PageDownloadInstall.xaml.vb / ShowMinecraftVersionList` 与 `ModDownloadLib.vb / McDownloadListItem`：分类、来源日期、命令方块图标、42 DIP 行和懒加载。本批进入已有「历史快照补充」卡片，沿用选择、返回、加载状态和动画。
- `ModMinecraft.vb / McLibListGetWithJson`：先按 Windows 规则筛选普通库和原生库；保留完整下载声明。本批没有增加组件或更改旧版启动入口。
- `ModMinecraft.vb / McAssetsListGet`：`map_to_resources` 写入有效游戏目录的 `resources`；隔离开启时写入实例目录，关闭时使用根目录。它与 `virtual` 的根目录 `assets/virtual/legacy` 映射不同。
- `ModLaunch.vb / McLaunchArgumentsReplace`：游戏目录、资源目录占位符保持各自角色，继续由现有 LaunchWrapper 配方校验约束。

固定源码链接：[ModDownload](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModDownload.vb)、[ModMinecraft](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb)、[ModLaunch](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModLaunch.vb)。本地参考与该提交的 Git 对象核对，行尾标准化后比较文本。

## 身份、冲突和已有实例

只向只读 `HistoricalSnapshotIds` 注册表增加已核对的十项 ID。别名映射、分类和配方继续共享这一注册表，既不按 `12w`／`13w` 前缀批量放行，也不接受 `pending` 分类。测试独立读取所有真实快照资源，要求身份集合与生产注册表完全一致，并明确将覆盖数量更新为 26。

普通官方同名版本保持优先；补充声明冲突时不能靠列表先后来选取校验值。新增条目继续使用基于索引摘要的私有索引文件名，避免污染普通索引。

所有注册快照都参与现有共享资源冲突回归：隔离安装保留共享目录的不同内容；关闭隔离遇到同名异内容会停止发布。只有测试明确纠正冲突文件后才进行同名重试，不由安装器自动删除或覆盖用户文件。发布前取消、原子实例发布、已有 JSON／INI 原字节与隔离设置保留继续使用原有安装事务。

已有实例补全的验证包含用户窗口参数、UTF-16 JSON、隔离设置和损坏客户端修复。不会为新增条目改写旧实例设置，也不会为旧快照请求模组加载器。其他历史档案及 Classic／Indev 目录适配继续留在后续范围。

## 验证入口与证据边界

```powershell
./tools/verify-supplemental.ps1 -Install -VersionIds '13w01b','13w01a','12w50b','12w50a','12w49a','12w42b','12w42a','12w41b','12w41a','12w40b' -OutputDirectory './artifacts/snapshot-batch-3/live-install'
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-versions
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-recipes
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-candidates
./build.ps1
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --live-supplemental './artifacts/snapshot-batch-3/live-metadata'
```

本轮验收包括归档夹具、全量回归、全部补充版本的在线元数据与索引检查，以及十项新增版本的真实联网安装。联网安装使用新的私有游戏目录，不使用外部游戏目录作复用源；后续版本只复用本次运行已校验的文件。逐项检查下载文件摘要、隔离资源落点、发布前取消、同名重试及已有实例补全。

源码清单、固定源码核对、进程回执、网络字节、实际安装结果及最终验证汇总存放于忽略目录 `artifacts/snapshot-batch-3/`，以最终成功回执为准。归档原始元数据和工具解析后的在线元数据分别留存，不能把后者的重新序列化字节当作上游公布的摘要。

这些证据不包含 PCL GUI 操作或游戏启动。Java 运行兼容性、游戏内声音／资源加载、实际存档落点及创建／保存／重进世界仍需单独验收。本轮不打包、不发版。
