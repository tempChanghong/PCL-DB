# 显式 Alpha 配方首批接入与验证

日期：2026-10-23。实现基线：`7db7ec8340755ee1d4a7557015a6fd367963c5dc`。本批仅本地提交，不推送、打包或发版，产品版本号保持不变。

## 本批范围与当前进度

接入三个字段完整的 UVMC 历史版本，均保留 `old_alpha` 分类、原始 ID、发布日期和独立下载路径：

| ID | 客户端 SHA-1 | 客户端字节数 |
| --- | --- | ---: |
| a1.1.0-1 | ea8db151ce4d1b2d4c60beb6ebc0abb65e8d7444 | 872811 |
| a1.0.17_03 | d0bfecc9966d6106b304dd219cf73f5e6670203b | 867329 |
| a1.0.16_02 | b45ada7f4f421298be217e805d2ccd41e3421c6a | 860260 |

原始元数据按字节加入测试资源，与已有研究归档 SHA-256 核对。元数据未公布自身摘要时，归档摘要仅作为本地来源记录；客户端、索引、支持库及资源计划继续核对各自声明的强摘要和大小。原始客户端大小是字符串，沿用既有合法解析，不重写归档。

累计支持 89 项补充版本：3 项 2.0 三色版、67 项旧快照、12 项 Beta、7 项 Alpha。171 项历史候选中已接入 86 项，剩余 85 项；其中 18 项字段完整，67 项仍缺字段。历史候选统计不包含三色版，不能把两种总数相加混用。

## 固定 PCL 源码对照

依据 PCL 未限制下载的固定提交 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`，五份本地参考与 Git 对象在标准化行尾后逐份核对。

- `ModDownload.vb / DlClientListBmclapiMain`：附加清单合并后复用正常安装；官方同名声明优先，补充请求独立超时，失败不阻断普通下载。UVMC 是独立公开来源，不等同于 PCL 私有特供清单。
- `ModDownloadLib.vb / McDownloadClientLoader`：继续复用库和资源跨阶段并行、选源、共享连接预算、文件校验、clientVersion 和实例原子发布。
- `ModDownloadLib.vb / McDownloadListItem`、`PageDownloadInstall.xaml.vb / ShowMinecraftVersionList`：沿用远古类别、圆石图标、42 DIP 列表行、日期排序和已有选择动画。远古补充卡片增至 19 行，不进入最新版本推荐；没有新增独立页面动画。
- `ModMinecraft.vb / McAssetsListGet`：virtual 资源进入根目录 `assets/virtual/legacy`；map_to_resources 才跟随有效隔离目录。已有受控资源冲突回归扩展至全部 89 项补充版本中的 86 项历史条目。
- `ModLaunch.vb / McLaunchArgumentsReplace`：game_directory 取有效隔离目录，game_assets 取根目录 virtual/legacy；旧参数先分割、去重，再替换变量。继续拒绝会覆盖目录的参数及混合的新旧 arguments。

对应源码：[ModDownload](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModDownload.vb)、[ModDownloadLib](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/ModDownloadLib.vb)、[下载选项](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/PageDownloadInstall.xaml.vb)、[ModMinecraft](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb)、[ModLaunch](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModLaunch.vb)。

## 独立配方与兼容边界

本批必须使用 LaunchWrapper **1.5**（SHA-1 `5150b9c2951f0fde987ce9c33496e26add1de224`，27,787 字节）和显式 `net.minecraft.launchwrapper.AlphaVanillaTweaker`。缺少显式选项会进入默认 Vanilla；按旧 Alpha 特例套用 1.6 也不正确。新配方仅开放这三个精确 ID，既不按 Alpha 分类，也不按版本前缀扩大。

`a1.1.0-2`、`a1.1.1` 继续使用 1.5 的默认 Vanilla；Classic／RC1／Infdev 的 1.6 特例保持不变。测试夹具将 wrapper 版本与 tweaker 选择分开，避免测试自身把所有 Alpha 偷换为 1.6。

原始三份元数据均有完整的 13 项支持库及 legacy 索引，源文件没有 assets 属性；本批不添加该属性，不引入参考库或资源索引的缺字段回退。legacy 索引 SHA-1 为 `c0fd82e8ce9fbc93119e40d96d5a4e62cfa3f729`，新实例仍使用完整摘要命名的私有索引，资源实际路径保持 PCL 的 virtual/legacy。

另外核对实际 1.5 JAR 的 AlphaTweaker／injector 反汇编：acceptOptions 保存目录，injector 使用 Launch.minecraftHome 设置工作目录，assetsDir 用于图标加载。当前 Mojang 源码与历史发布二进制存在差异，不能用当前源码的字段位置替代实际 1.5 行为。该静态检查没有实例化 Applet、启动 Minecraft 或执行存档代码。

## 验证与复现

```powershell
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-versions
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-recipes
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-candidates
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-early
./tools/verify-supplemental.ps1 -Install -VersionIds 'a1.1.0-1','a1.0.17_03','a1.0.16_02' -OutputDirectory './artifacts/alpha-batch-1/live-install'
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --live-supplemental './artifacts/alpha-batch-1/live-metadata'
./build.ps1
```

回归覆盖原始客户端与索引身份、跨版本 URL、猜测别名、未验证兄弟版本、缺少／替换／重复 tweaker，以及摘要正确但版本错误的 1.6 wrapper；错误配方在载荷传输前拒绝。已有实例的不兼容配方会阻止补全，原 UTF-16 JSON、INI、自定义参数和客户端字节不改；测试显式纠正配方后再同名补全，原隔离设置保留。

三个新版本均加入真实安装器的受控 virtual 生命周期测试：安装、发布前取消、同名重试、损坏修复、开启／关闭隔离、用户 resources 保留、普通 legacy.json 不被私有索引覆盖、virtual 文件与目录占用冲突。受控测试的客户端和资源是合成夹具，wrapper 为固定摘要的实际二进制，不计作真实联网安装。

联网验收在全新目录实际安装三个版本，核对完整文件计划、1,120 项 virtual 别名及根目录落点；取消后同名重试、损坏客户端补全继续逐项核对摘要，并保留已有设置。无外部游戏目录复用，后续仅复用本轮已校验文件；重试及补全要求零载荷网络传输。另检查全部 89 项补充版本与普通 rd-132211 的在线元数据／索引，这不等同于全部版本重新安装。

忽略目录 `artifacts/alpha-batch-1/` 保留原始来源摘要、实际 1.5 反汇编、固定源码核对、四项定向回归、联网进程／安装报告、完整构建和最终回执。通过状态、最终断言数、文件计划和字节数以 `validation.json`、`live-binding.json` 及绑定的进程结果为准；源码清单及二进制摘要绑定本轮验证。全套验证继续覆盖 UI、动画、搜索、安装和打包工具契约，未执行发布打包。

本批不提供 PCL GUI、游戏实际运行、Java 兼容性、游戏内资源、创建／保存／重进世界的人工验收结论。

## 下一步

剩余字段完整的候选为 16 项 1.5／显式 Alpha 和两项 1.6／Classic。可继续按小批次核对 Alpha 客户端、元数据、目录配方和取消重试；不能仅因配方相同跳过逐项验证。Classic 及 67 项缺字段候选继续保留独立研究和运行兼容边界。
