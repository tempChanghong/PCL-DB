# 旧快照第四批：2012 年 8–10 月

本轮接入十个字段完整的历史快照，补充支持从 34 项增至 44 项：36 项旧快照、5 项远古补充和 3 项 2.0 三色版。按归档研究清单计算，历史候选累计接入 41/171 项，剩余 130 项（63 项字段完整、67 项需要补全）。统计对应本地归档，不代表在线目录永不变化。

## 接入版本

|ID|启动配方|资源索引|
|---|---|---|
|12w40a|LaunchWrapper 1.5 / 默认 VanillaTweaker|pre-1.6|
|12w39b|同上|同上|
|12w39a|同上|同上|
|12w38b|同上|同上|
|12w38a|同上|同上|
|12w37a|同上|同上|
|12w36a|同上|同上|
|12w34b|同上|同上|
|12w34a|同上|同上|
|12w32a|同上|同上|

每份原始 UVMC 元数据均先与研究档案记录的 SHA-256 核对，再原字节加入测试资源。十项自带完整的库下载声明，索引 SHA-1 均为 `4759bad2824e419da9db32861fcdc3a274336532`，客户端摘要分别保留。没有根据版本号推导日期、共用客户端身份，或给本批引入缺失字段回退。归档 SHA-256 用于检查本地夹具，不充当 UVMC 未公布的清单摘要。

## 参照的 PCL 实现

固定提交为 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`，本地参考与该提交 Git 对象在标准化行尾后核对：

- `ModDownload.vb / DlClientListBmclapiMain`：附加清单合并后继续使用普通下载流程。PCL-DB 的补充请求继续独立限时；官方同名优先，补充源失败不影响普通下载。
- `ModDownloadLib.vb / McDownloadClientLoader`：库与资源阶段并行、JSON 保留 `clientVersion`。本批直接进入已有安装管线，不另建一套下载器。
- `ModDownloadLib.vb / McDownloadListItem` 和 `PageDownloadInstall.xaml.vb / ShowMinecraftVersionList`：按来源日期排序、命令方块图标、42 DIP 行与懒加载。本批加入已有「历史快照补充」卡片，继续使用选择／返回和页面动画。
- `ModMinecraft.vb / McLibListGetWithJson`：Windows 规则与原生库筛选。本批保留元数据中的规则及完整摘要，继续使用现有下载计划去重和共享缓存。
- `ModMinecraft.vb / McAssetsListGet`、`ModLaunch.vb / McLaunchArgumentsReplace`：`map_to_resources` 按有效隔离目录写入 `resources`，不与 `virtual` 的根目录 `assets/virtual/legacy` 混用；游戏目录和资源目录参数保留各自角色。

源码链接：[ModDownload](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModDownload.vb)、[ModDownloadLib](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/ModDownloadLib.vb)、[ModMinecraft](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb)、[ModLaunch](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModLaunch.vb)。

## 冲突和已有实例

只向只读快照注册表添加核对过的十个 ID，不按 `12w` 前缀批量开放。所有快照仍只接受 `snapshot`；测试独立读取真实元数据资源，要求其身份集合与注册表一致，并将明确覆盖数量从 26 更新为 36。

普通官方声明保持优先，冲突的补充声明不能由列表顺序决定。基于索引摘要的私有索引名继续避免污染普通版本索引。组件选择保持禁用，不为旧快照触发加载器请求。

共享资源回归覆盖全部 36 项注册快照：开启隔离时不改写根目录的不同内容；关闭隔离遇到同名异内容时拒绝发布；测试明确纠正冲突后才能同名重试。继续沿用发布前取消、原子目录发布及已有实例补全，保留已有 JSON、INI、隔离设置和用户窗口参数的原字节。

## 验证与证据

```powershell
./tools/verify-supplemental.ps1 -Install -VersionIds '12w40a','12w39b','12w39a','12w38b','12w38a','12w37a','12w36a','12w34b','12w34a','12w32a' -OutputDirectory './artifacts/snapshot-batch-4/live-install'
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-versions
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-recipes
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-candidates
./build.ps1
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --live-supplemental './artifacts/snapshot-batch-4/live-metadata'
```

本轮验收包含定向回归、全仓核心／UI／动画／搜索检查、全部补充版本的在线元数据与索引检查，以及十项新增版本的真实联网安装。安装使用新的私有游戏目录，不使用外部游戏目录复用；后续项只共享本次运行已校验的文件。逐项验证文件摘要、资源落点、发布前取消、同名重试、UTF-16 已有 JSON 与隔离设置保留，以及损坏客户端补全。

工具保留每批 1–32 个 ID 的执行上限；本批明确选择十项，全量在线元数据检查使用独立入口。源码清单、固定源码核对、进程回执、真实安装结果与最终验证汇总保存于忽略目录 `artifacts/snapshot-batch-4/`，以最终成功回执为准。工具重新序列化的在线元数据仅供诊断，不能替代上游原始字节或公开摘要。

本轮没有游戏启动或 PCL GUI 操作证据；游戏内资源、Java 运行兼容性和创建／保存／重进世界仍需单独验收。其余历史候选及 Classic／Indev 目录适配不在本批。本轮不打包、不发版。
