# 旧快照第一批与补充版本验证工具

本轮继续实施历史版本报告，新增 `13w10b`、`13w10a`、`13w09c`、`13w09b`、`11w47a`。已接入的历史候选由 6 项增至 11 项，加上 2.0 三色版，共 14 个补充版本；研究快照中的 171 项候选仍剩 160 项未开放。没有打包发版。

## PCL 源码依据

固定提交 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`：

- `ModDownload.vb / DlClientListBmclapiMain` 的补充合并及普通安装入口。PCL-DB 继续独立加载 UVMC，并保留普通官方同名条目优先、失败不阻断普通目录的约定。
- `PageDownloadInstall.xaml.vb / ShowMinecraftVersionList`、`ModDownloadLib.vb / McDownloadListItem` 的类别、日期、命令方块图标和 42 DIP 行。本批追加到已有「历史快照补充」，使用原有懒加载与动画，不增加独立页面。
- `ModMinecraft.vb / McLibListGetWithJson` 的 rules、Maven 和 Windows natives；本批都自带完整库声明，使用 LaunchWrapper 1.5 默认 VanillaTweaker，没有套用 RC1／Infdev 的 1.6 参考。
- `McAssetsGetIndexName`、`McAssetsListGet` 及 `ModLaunch.vb`：本批 pre-1.6 索引带 `map_to_resources`，资源映射到有效游戏目录；不是固定 virtual legacy 目录。沿用三态隔离、共享文件校验和原子发布。

源码：[固定 ModDownload](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModDownload.vb)、[固定 ModMinecraft](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb)、[固定 ModLaunch](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModLaunch.vb)。实际读取本地固定参考及对应 Git 对象。

## 接入与冲突边界

五项均保留 canonical ID、精确源 URL 和 `snapshot` 分类，不接受 pending／远古类别替代。不加入最新推荐，不开放组件加载器，也不增加缺失库或索引回退权限。

修正补充分类函数只识别 `13w12~` 的局限，其他已登记 snapshot 也显示为旧快照。2.0 仍单独处理，原有远古条目不变。

新夹具保留五份真实元数据；其索引 SHA-1 均为 `4759bad2824e419da9db32861fcdc3a274336532`。不能将该 pre-1.6 索引和 Mojang 另一个 pre-1.6 索引或 legacy 索引互换。

自动化检查覆盖严格身份／类别、官方同名优先、完整声明生成带摘要的 Windows 计划、取消／同名重试、已有 JSON 与隔离原字节保留、原索引不变，以及：

- 开启隔离时资源进入实例 resources，共享目录中不同内容保持不变。
- 关闭隔离时共享 resources 同名异内容拒绝发布；用户明确纠正测试文件后，同名重试成功。
- UI 将六个旧快照显示在同一卡片，保留普通行和最新推荐；逐行选择后没有加载器请求。

## 可重复验证入口

源码工具 `tools/verify-supplemental.ps1` 构建测试宿主，通过生产允许表和真实 SourcePolicy 工作：

```powershell
# 默认本批五项，只检查在线元数据、索引摘要和文件计划
./tools/verify-supplemental.ps1

# 新建独立目录实际下载，检查发布取消、重试、补全与最终文件
./tools/verify-supplemental.ps1 -Install

# 也可复验其他已接入条目，使用 canonical ID
./tools/verify-supplemental.ps1 -VersionIds '1.0.0-rc1','inf-20100630-1'
```

每次默认生成新的 `artifacts/supplemental-verification/<guid>/`。自定义 `-OutputDirectory` 必须不存在或为空。目录只属于本次验证，已有回执与游戏目录不能覆盖；不清理用户文件。工具不接受未登记候选，不绕过启动配方或允许表。

`-Install` 不使用外部复用源；第一项进行全新下载，后续项可共享本次运行已经校验的文件。工具逐项检查完整下载计划，包括私有索引、资源对象及隔离 resources；对照检查与实际安装的声明，避免源在两次请求间变化而记录成同一结果。

每项还在发布前取消一个新实例并同名重试，再在本工具创建的实例中加入窗口参数和用户设置、写为 UTF-16、损坏客户端首字节，然后校验补全。检查参数／JSON 原字节和原隔离设置保持不变，最终重新校验文件。此过程只修改该验证目录。

产物包括 `results/report.json`、解析后的源／有效元数据、stdout／stderr 和 `receipt.json`。解析视图不是原始网络响应，也不把自算摘要称为上游公布的摘要。回执绑定源码清单、测试宿主与 Core 摘要、报告摘要；失败不会继承旧的成功回执。

## 验证和保留的验收

针对性入口：`--regression supplemental-candidates`、`supplemental-versions`、`supplemental-recipes`。运行完整 `./build.ps1` 覆盖内核、UI、动画及搜索；不传 `-Package`。

本轮在线安装结果与完整回归回执保存在忽略目录 `artifacts/snapshot-batch-1/`；最终结果以本轮成功回执为准。自动化 fixture 使用合成客户端／资源与真实小型 wrapper，不将其记作真实游戏运行。

实际联网验证已通过两组独立的空游戏目录，均没有外部复用源：

|组别|初次安装累计载荷网络字节|资源布局|结果|
|---|---:|---|---|
|本批五个旧快照|72,993,197|每项 746 个隔离 resources 映射|全部安装、逐文件校验、发布取消／重试、UTF-16 配置保留及损坏客户端补全通过|
|RC1／Infdev 复验|116,801,399|1120 个 virtual legacy 逻辑文件|两项同样通过|

后续版本仅共享本次运行已校验的库和资源；各项重试与补全载荷网络字节均为 0。验证报告也分别列出各版本实际网络字节和复用数，没有将共享复用称为再次全量联网下载。此处网络字节采用安装内核的载荷统计，不含前置元数据请求。

工具明确记录 `gameLaunched=false`、`pclGuiVerified=false`。PCL 实际刷新、合适 Java 下的游戏启动、声音／资源加载、创建世界、保存重进与默认目录落点仍需单独验收；不因为下载和安装通过就宣称这些已完成。Classic／Indev 的目录兼容问题继续暂缓。

后续优先完成代表版本的运行验收，然后沿相同配方分批扩大旧快照覆盖。剩余候选按归档研究数据为 93 项字段完整、67 项需要补全；这些数量来自当次研究快照，不代表在线目录永不变化。
