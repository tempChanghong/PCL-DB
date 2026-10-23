# Alpha 同日修订版接入与验证

日期：2026-10-23。实现基线：`14d8d3d`。本批本地提交，不推送、打包或发版，产品版本号不变。

## 范围与进度

接入两组 Alpha 修订，均保留原始 ID、独立元数据 URL 和 old_alpha 分类：

| ID | 原始发布日期（+08:00） | 客户端 SHA-1 | 字节数 |
| --- | --- | --- | ---: |
| a1.0.14-1 | 2010-07-30 08:00 | 439be76e595fa0850636bd2a2ba991b30c651c7f | 852817 |
| a1.0.14-2 | 2010-07-30 08:00 | 5f2965a915fa9f040e6f908cf64e1ee5ba1db44e | 852834 |
| a1.0.13_01-1 | 2010-07-29 08:00 | bb680bcaa6334c73d10bba7c7a29b4c112d4cdce | 843401 |
| a1.0.13_01-2 | 2010-07-29 08:00 | 7d6efcd302b730c834a9d8040584bf71a7f97d02 | 843414 |

每组两个版本同日但客户端不同，不按日期或去掉后缀的名称合并。`a1.0.14`、`a1.0.13_01`、猜测的下划线别名及未接入的兄弟修订不自动开放。原始 JSON 按字节归档到测试资源，与既有研究归档 SHA-256 核对；本地归档摘要不冒充上游公布的元数据校验值。

累计支持 93 项补充版本：3 项三色版、67 项旧快照、12 项 Beta、11 项 Alpha。171 项历史候选覆盖 90 项、剩余 81 项；其中 14 项字段完整（12 项显式 Alpha 1.5、2 项 Classic 1.6），另有 67 项缺字段。三色版不在这 171 项历史候选中。

## 源码对照与实现边界

继续依据 PCL 未限制下载的固定提交 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`。本轮重新阅读并与 Git 对象核对五份参考：

- `ModDownload.vb / DlClientListBmclapiMain`：补充清单合并后复用正常下载。官方同名声明优先，补充请求独立限时，失败不影响普通清单。
- `ModDownloadLib.vb / McDownloadClientLoader`：继续使用已有选源、支持库／资源并行、共享下载预算、强摘要校验、clientVersion 与实例原子发布。
- `ModDownloadLib.vb / McDownloadListItem` 与 `PageDownloadInstall.xaml.vb`：沿用 old_alpha 分类、圆石图标、42 DIP 行、日期排序及现有选择／返回动画。远古补充卡片增加到 23 行；所有同日条目逐个进入真实页面选择流程，保留原始 ID，不请求不适用的组件。
- `ModMinecraft.vb / McAssetsListGet`：virtual 资源进入游戏根目录 `assets/virtual/legacy`；map_to_resources 则跟随有效隔离目录，两者不可混用。
- `ModLaunch.vb / McLaunchArgumentsReplace`：game_directory 指向有效隔离目录，game_assets 指向共享 virtual/legacy；保留参数分割、去重及目录覆盖检查。

具体源码链接见[上一批的固定源码清单](alpha-batch-1-2026-10-23.md)。完整构建另检查全部 44 份固定 PCL 参考哈希。

本批四项均使用 LaunchWrapper 1.5、显式 AlphaVanillaTweaker、13 项完整库声明和固定摘要的 legacy 索引；源文件没有 assets 属性。wrapper SHA-1 为 `5150b9c2951f0fde987ce9c33496e26add1de224`，大小 27,787 字节；索引 SHA-1 为 `c0fd82e8ce9fbc93119e40d96d5a4e62cfa3f729`。仅扩展精确 ID 配方，不新增缺字段参考回退，不影响默认 Vanilla 和既有 1.6 特例。

测试夹具把已审定的 Alpha 1.5 ID 集合集中供配方、virtual 生命周期及原始元数据检查使用，与生产分类注册表分开。避免以后只扩展其中一组测试，使新版本遗漏目录或取消重试验证。

## 验证与复现

```powershell
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-versions
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-recipes
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-candidates
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-early
./tools/verify-supplemental.ps1 -Install -VersionIds 'a1.0.14-1','a1.0.14-2','a1.0.13_01-1','a1.0.13_01-2' -OutputDirectory './artifacts/alpha-batch-2/live-install'
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --live-supplemental './artifacts/alpha-batch-2/live-metadata'
./build.ps1
```

元数据回归逐项固定客户端摘要、字符串大小及原始索引，拒绝跨版本 URL、兄弟元数据和含糊别名；同日两组分别在正反清单顺序下检查合并结果，保留稳定输入顺序与不同客户端。原有官方优先、补充失败隔离和过期结果门控继续参加检查。

新增四项均进入已有配方拒绝／显式纠正后重试、真实安装器的受控 virtual 生命周期及共享 map_to_resources 冲突测试。覆盖缺少／替换／重复 tweaker、正确摘要但错误版本的 wrapper、已有 UTF-16 JSON／INI 保留、隔离设置保留、发布前取消／同名重试、普通 legacy.json 保留、virtual 损坏修复和文件目录占用。受控客户端与资源是合成夹具，不计为真实下载。

联网验收使用全新目录实际安装四项，不使用外部游戏目录复用；后续只复用本轮已校验文件。每项完整校验 1,727 个计划文件和 1,120 项 virtual 别名，检查发布前取消、同名重试、损坏客户端补全及已有设置保留；重试及补全要求零载荷传输。另检查全部 93 项补充版本与官方 rd-132211 的在线元数据／索引，不把此检查称为全部版本重新安装。

静态检查使用实际 1.5 wrapper 和四个真实客户端，核对 Applet、候选 Minecraft 字段及静态 File 工作目录字段。按实际字段名记录，不假定所有年代都使用同一混淆名称；不实例化 Applet、不启动游戏。不能据此断言游戏运行、Java 兼容或创建／保存／重进世界通过。

忽略目录 `artifacts/alpha-batch-2/` 保存来源摘要、固定源码核对、研究进度、四组定向回归、静态客户端检查、联网安装及全套构建回执。最终通过状态、断言数、传输字节及源码／二进制绑定见 `validation.json`、`live-binding.json` 和提交后的 `committed-validation.json`。全套回归包含 UI、动画、搜索和打包工具契约，本批不执行打包。

## 下一步

继续按小批次接入剩余 12 项完整 Alpha 1.5 候选，逐项核对客户端及目录配方。两项 Classic 和 67 项缺字段候选继续保持独立研究边界，不按前缀开放。
