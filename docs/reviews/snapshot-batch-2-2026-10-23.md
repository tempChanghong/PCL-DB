# 旧快照第二批与只读注册清单

本轮新增十个字段完整的历史快照，补充支持由 14 项增至 24 项；其中 16 项为旧快照、5 项远古补充、3 项 2.0 三色版。历史候选接入累计 21/171 项，归档研究快照剩余 150 项（83 项字段完整、67 项需要补全）。数量以当次归档为基准，不代表在线目录永不变化。

## 本批版本

|ID|启动配方|资源索引|
|---|---|---|
|13w09a|LaunchWrapper 1.5 / 默认 VanillaTweaker|pre-1.6|
|13w07a|同上|同上|
|13w11a|同上|同上|
|13w06a|同上|同上|
|13w05b|同上|同上|
|13w05a|同上|同上|
|13w04a|同上|同上|
|13w03a|同上|同上|
|13w02b|同上|同上|
|13w02a|同上|同上|

十份原始元数据从已核对 SHA-256 的研究档案加入测试资源。它们的索引 SHA-1 均为 `4759bad2824e419da9db32861fcdc3a274336532`；保留客户端、库、原始 ID 和来源日期，不根据编号猜日期、参考库或合并身份。UVMC 的元数据没有公布清单 SHA-1；归档 SHA-256 仅证明本地夹具与研究文件一致。

## 对照的 PCL 源码

固定提交 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`：

- `ModDownload.vb / DlClientListBmclapiMain`：合并附加项后继续普通下载流程。PCL-DB 保留独立补充超时、官方同名优先以及冲突声明排除。
- `PageDownloadInstall.xaml.vb / ShowMinecraftVersionList`、`ModDownloadLib.vb / McDownloadListItem`：按类别显示、命令方块图标、来源日期与 42 DIP 行。本批仍追加到已有快照补充卡片，继续复用懒加载、选择联动和动画。
- `ModMinecraft.vb / McLibListGetWithJson`、`McAssetsListGet`：Windows 库规则与 map_to_resources 的有效目录映射。开启隔离使用实例 resources，关闭隔离使用共享 resources；不替换成 virtual legacy。
- `ModLaunch.vb` 的旧参数替换：保留游戏目录与资源目录角色，沿用现有启动配方校验；本批不改变启动入口或注入器。

本地读取固定参考及 Git 对象；[固定 ModDownload](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModDownload.vb)、[固定 ModMinecraft](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb)、[固定 ModLaunch](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModLaunch.vb)。

## 注册与冲突处理

原先快照身份在允许表、分类和启动配方中重复枚举。本轮在 SupplementalVersions 增加只读 `HistoricalSnapshotIds`，由同一清单生成别名映射、判断 snapshot 分类及选择 1.5 配方。只读包装在别名表之前初始化，避免静态初始化顺序问题；调用方不能修改底层注册表。

测试与 UI 检查使用该清单，同时独立读取全部真实元数据资源，要求两边的 snapshot 身份集合完全一致。这样新增或误删注册项不会因测试也复制同一列表而悄悄通过。资源清单还保留数量断言，变更覆盖范围需明确审核。

新版本仅接受 snapshot，不继承 2.0 的 pending 分类，也不按 `13w` 前缀自动开放。相同日期的不同 ID 继续分别保留。普通官方同名优先，补充列表相互矛盾的声明继续排除。没有扩大缺失索引、缺失库下载声明的回退范围。

对全部 16 项快照运行共享资源冲突检查：隔离安装保留根目录的不同内容；关闭隔离遇到同名异内容时停止发布；仅在测试明确纠正冲突文件后同名重试。不自动删除或覆盖原文件。已有 JSON、窗口参数和隔离设置仍按原字节保留。

UI 检查确认十六行处于同一卡片、普通行与最新推荐不被重建／替换、逐项选择没有加载器请求；分类检查主动 materialize 全部行，可视区懒加载由原有长列表检查另行验证。

## 验证入口与证据

```powershell
./tools/verify-supplemental.ps1 -Install -VersionIds '13w09a','13w07a','13w11a','13w06a','13w05b','13w05a','13w04a','13w03a','13w02b','13w02a'
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-versions
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-recipes
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-candidates
./build.ps1
```

验证脚本默认批次保持首批五项，第二批通过明确 ID 选择。每次使用新目录，不读外部游戏目录作复用源；后续项仅共享本次运行已校验的文件。检查实际文件、隔离资源、发布取消和同名重试，以及 UTF-16 已有配置保留与损坏客户端补全。工具、过程和源码回执保存到 `artifacts/snapshot-batch-2/`，以本轮最终成功回执为准。

这些检查分别提供自动化 fixture、在线元数据与真实联网安装证据。没有启动 PCL 或 Minecraft；Java 运行兼容性、声音／资源加载、实际默认目录落点及创建／保存／重进世界仍需独立验收。Classic／Indev 目录适配继续暂缓，不混入本批。暂不打包发版。
