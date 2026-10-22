# PCL-DB 与 PCL2 源码对照报告

日期：2026-10-22（北京时间）。重点：动画与视觉、下载、搜索与联动、安装隔离、工程与打包。

## 1. 对照结论

**PCL-DB 已经还原了 PCL 的主要下载工作流、许多控件几何与关键动画曲线；还没有达到整个页面及运行机制“一比一”的程度。差距已从基本功能缺失，转为控件状态细节、加载节奏、搜索呈现和细粒度下载编排。**

最值得继续改进的地方是：

1. **视觉质感**：标题栏图标按钮回弹、滚动条悬停/拖动、个性化圆形单选动画仍明显简化；搜索统计、图标分页与筛选控件也未完全对齐。
2. **加载体验**：已有 PCL 镐子和内容入退场，但没有完整复制上游加载等待/最短展示状态；侧栏节奏还差 20ms。
3. **下载关键路径**：主要阶段已经并行，但组件安装器/依赖/API 附属文件预取比 PCL 晚；分段统计作用域不同，跨实例社区文件复用也较窄。
4. **搜索质量**：用户步骤已接通，分组和身份保护较完整；结果评分仍为简化策略，与 PCL 的平台权重、模糊相关性不同。版本预设和行内信息也落后于已读固定源。
5. **交付可持续性**：本仓候选来源追溯、资产绑定和独立检查有价值，但新增搜索尚未贯通全部候选门禁；四张新增图标虽然字节正确，也没有纳入来源哈希门禁。

本仓有明确的、适合“下载补全器”的优势：私有 stage 到最终实例的发布边界、严格的文件身份/校验声明合并、可持久恢复的下载协议、既有设置保留、同名发布的辨识及候选追溯。**这些不等于已经证明比 PCL 下载更快、动画更流畅、兼容范围更大或整体更可靠。**

建议下一步先修上一份报告已确认的内容正确性问题；之后用一个小迭代收口三个常见控件，再推进加载状态、搜索呈现和组件预取。具体顺序与验收见第 8 节。

## 2. 基线、方法与判断口径

### 2.1 固定对象

| 对象 | 对照身份 |
| --- | --- |
| PCL2 未限制版 | `Meloong-Git/PCL @ 5ef81e490ae46b1100d61e33d0183094c7a80ca8`，提交标题“2.14.0.0 正式版” |
| PCL-DB | `main @ f8601e41807e0b1e03cbeaefb6965ea49205555a` |
| 本仓位置 | `D:\CodeProjects\Minecraft\PCL-DB` |
| 上游本地仓库 | `D:\CodeProjects\Minecraft\PCL`；其当前 HEAD 为 `6b9a50df911c9d68b69b24c979efba477fb14b67`，**不是对照版本** |
| 关联报告 | [全仓综合审查报告](D:/CodeProjects/Minecraft/PCL-DB/docs/reviews/PCL-DB-full-review-2026-10-22.md) |

本轮直接用固定 Git 对象读取上游原文件，结合本仓当前源码比较，未用注释或 `SOURCE_REFERENCES.md` 代替原代码。搜索领域另导出十份原文件，记录原路径、Git blob 和行号；其他领域的上游行号同样来自固定 Git 对象。

本轮开始时已存在上一份未跟踪的 `docs/reviews/` 报告，已保留；业务源码、测试和 HEAD 与前轮相同。**本轮不修改业务代码、不 Commit、不重新构建、不启动 GUI、不执行真实下载安装、不制作候选或打包发版。**

前轮同提交已通过：核心 2051、UI 754、motion 358、pack-search 147、源码/资源对齐 17 项（含 44 份固定源码快照哈希），构建零警告、零错误；另有隐藏 WPF 输入边界探针。这些证据本轮沿用，未重复计数，亦不是 PCL 原程序与本仓的同机性能对比。

### 2.2 “还原”和“优势”分别指什么

| 判断 | 含义 |
| --- | --- |
| 关键参数/语义接近 | 直接读到两侧相同几何、时序、任务依赖或规则；仍可能有驱动与边界差异 |
| 部分还原 | 主路径一致，但有会影响观感、结果或运行效率的已知差异 |
| 有意适配 | 为独立补全器职责而改变页面、命名空间、发布或保护策略，不应机械改回 |
| 本仓有据增强 | 能指出具体新增约束、状态隔离或验证机制；仅在该范围内成立 |
| 待测 | 源码能说明机会或风险，尚无同条件实测，不能宣布运行效果优于上游 |

不使用“还原 90%”等缺乏样本定义的数字。启动、账号、联机、百宝箱和完整资源市场不是本项目要复制的范围；不建议为了截图相似度恢复这些入口。

## 3. 动画、控件、页面与关于页

### 3.1 已经深入对齐的部分

| 对照点 | 固定 PCL | 当前 PCL-DB | 判断 |
| --- | --- | --- | --- |
| 主要字体 | PCL English + Microsoft YaHei UI | 相同字体家族；字体文件 Git blob 与上游完全相同，为 `8ee62cb771aa2d44a247d9c0e610eb7b70248cdc` | 不需要再靠盲目换字体解决观感问题；应查 DPI、回退环境、文字格式与基线 |
| 普通按钮/图标按钮/列表行 | 原 XAML 树、圆角、边框、居中缩放及状态曲线 | 已移植相应树与曲线，并加入自身键盘、门控与取消适配 | 已超过“普通 WPF 控件换色”的程度 |
| 页面内容进入 | Y=-16；100ms淡入；250ms Fluent 与350ms Back并行位移；逐项25ms | 保留组合和主要参数，以 WPF Clock 驱动 | 参数接近；驱动器不是上游 AniTimer，不能推导每帧完全相同 |
| 退出与滚动条进出 | 70ms退出、15ms逐项；滚动条90ms出/350ms入 | 同主要参数，退出画面冻结后分段呈现 | 观感目标接近，实现有意不同 |
| 安装选项横移 | ±50位移、分段进出、五次方缓动 | 独立260ms计划，保留对应时点和曲线 | 主动作已对齐，不需另写一套横移动画 |
| 卡片 | 短折叠150ms、长距离分段、250ms箭头、渐变阴影 | 相应曲线、几何与阴影已移植；增加宽度变化和异步刷新接续 | 应保留当前架构，避免统一替换为普通固定时长 EaseOut |
| 平滑滚动 | 300ms六次方、剩余增量叠加、排除下拉/多行编辑等 | 同目标，另处理手动定位、捕获、隐藏及布局变动 | 有状态边界增强；手感和帧耗时仍需桌面验证 |

源码对照：固定 PCL `Controls/MyPageRight.vb:392–461`、`MyButton.xaml.vb:151–173`、`MyCard.vb:138–212`、`MyScrollViewer.vb:7–28`；本仓 [PclSurfaceMotion](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.App/Controls/PclSurfaceMotion.vb:83)、[PclPressMotion](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.App/Controls/PclPressMotion.vb:10)、[PclCardMotion](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.App/Controls/PclCardMotion.vb:8)、[PclScrollViewer](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.App/Controls/PclScrollViewer.vb:89)。

### 3.2 仍影响“精致感”的明确差异

| 编号 | 差异与两侧依据 | 适合的改进 |
| --- | --- | --- |
| V-01 标题栏 | PCL `FormMain.xaml:107–110` 用白色 MyIconButton，按压缩至0.8并回弹；本仓 `MainWindow.xaml:34–40` 用普通 CaptionButton，主要只有悬停背景 | 为图标按钮补白色标题栏角色，再复用相同按压/回弹，保留28 DIP区域和拖动/关闭边界 |
| V-02 滚动条本体 | PCL `MyScrollBar.vb:10–43` 空闲/悬停/捕获透明度0.5/0.9/1，180/130/50ms；本仓 `App.xaml:9` 是固定色 Thumb | 迁移本体状态表、主题色及中断清理；不要混同于页面滚动条横向进出已对齐 |
| V-03 个性化圆形单选 | PCL `MyRadioBox.xaml.vb:102–127` 以150ms为基准做外环压缩/回弹及圆点缩放，完整动作不止150ms；本仓 `PclSelectionStyles.xaml:13–29` 主要切 Dot.Visibility | 在原生 RadioButton 互斥/键盘行为上补环、点动画，不改选中值和主题持久化逻辑 |
| V-04 加载呈现 | PCL `MyPageRight.vb:174–196,283–290` 有400ms等待和加载入场后400ms最短停留；本仓搜索/详情开始即换加载卡，完成再换结果 | 先补请求所有者控制的等待/显示/退出状态；快响应直接进内容，慢响应稳定展示。时长只控制画面，不拖慢网络或发布 |
| V-05 侧栏时序 | 主导航110+30ms已接近；PCL下载侧栏130+30ms，本仓仍110+30ms | 参数化导航/侧栏/内容刷新时序，输入恢复、焦点与视觉采用同一时点 |
| V-06 减少动画 | 本仓页面/滚轮尊重系统开关，部分局部控件仍动画；上游也有自己的动画控制策略 | 明确定义本项目统一策略，补控件后应同时验证启用/关闭终态，不以“与PCL相似”代替偏好策略 |

前三项范围小、频率高，适合作为第一批视觉还原任务。当前没有证据支持整体重写页面时钟层。

### 3.3 关于页与页面结构

上游关于主卡的 25/10 外边距、21/40/21/16 内容、45 DIP行、34 DIP头像、40/自适应/150列及35 DIP按钮，在本仓已经有明确对应。龙腾猫跃、HerobrineXia等来源行和资源也已保留；本项目产品行、文档与构建信息应显示真实身份。

仍有结构差异：PCL 关于页位于“更多”，本仓是设置子页，并多一个28 DIP返回行，所以首卡位置不能自然与原截图完全重合。如果继续精修，应明确采用哪一种固定页头，再核对头像中心、文字基线、长名称截断和 tooltip；不必为此恢复整个“更多”侧栏。关于页返回保存设置滚动位置和尊重新焦点，是适应本仓结构的有据收益。

主要依据：固定 `PageOtherAbout.xaml:8–65`；本仓 [关于页 XAML](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.App/MainWindow.xaml:419)、[返回与焦点](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.App/MainWindow.About.vb:20)。

### 3.4 现有动画增强与未证明之处

本仓分开 root/category/content 时钟所有权，旧回调自检身份，隐藏/卸载/尺寸改变时收束；冻结退出画面不可交互，当前异步结果不必承担旧页面退场。快照有四百万像素预算，最多16个可见内容单元分段，另加剩余画面和可选滚动条分段。这些是本仓架构的具体优点。

PCL 本身已有命名动画组、ForceExit 和完整加载状态机，不能说其没有中断保护。当前也没有本仓比 PCL 更流畅的证据。RenderTargetBitmap、UpdateLayout、懒行视口判断和图标解码的冷/热路径应记录 P95/最大 UI 阻塞与帧间隔；离屏采样帧数或录屏编码 FPS 均不能代替应用实际流畅度。

## 4. 下载、选源、并行与缓存

### 4.1 已还原的原则和仍有意义的差异

| 对照点 | 固定 PCL | 当前 PCL-DB | 判断及改进方向 |
| --- | --- | --- | --- |
| 自动清单选源 | 自动官方先试5秒；镜像优先30秒后备源；官方优先60秒；有效官方清单<4秒影响会话文件偏好 | 对应5/30/60秒及<4秒规则，文件/版本偏好分开；使用 manifest_v2 的条目摘要 | 核心接近；不能概括成每个URL都实时测速或所有组件目录都完全同参 |
| 失败提前激活 | 前源失败启动后源，赢家后收束其他 Loader | 解析/校验成功才赢；失败触发备源；finally取消并等待竞争请求 | 目标一致，本仓退出边界更显式；等待取消可能增加尾延迟，不是必然更快 |
| 地址映射 | assets/libraries/meta/Maven分别映射；社区接口有独立MCIM路径及历史特例 | 分类映射官方/BMCL，CF/MR独立平台策略 | 主线接近；不应移植上游凭证或把BMCL当社区搜索接口；特殊兼容样例仍需逐项对照 |
| 大分支并行 | 原版库/资源及包文件不阻塞彼此 | client+libs、assets/mapped、pack并行，共享预算和取消源 | 核心原则已恢复；组件不等全部assets不代表更细依赖也已恢复 |
| **组件预取** | 原版期间先下载Forge/NeoForge安装器及依赖、Fabric API/OptiFabric，处理器到真正需要时才等client | ComponentInstaller整体在client+libs完成后启动，之后才获取安装器/依赖/附属Mod | **实质并行差距**；适合拆Prepare/Apply，可能缩短冷缓存关键路径，但收益待测 |
| **调度统计作用域** | NetManager对全局传输100ms采样、30样本和85%等阈值控制新增分段 | 同类统计器属于每个DownloadEngine；预算共享连接/速度限制，但没有等价全局吞吐采样 | 数值接近而作用域不同；应先记录分支/全局指标，再决定聚合调度，不能只改阈值称完成还原 |
| 分段 | 已知大小≥1MiB，最大剩余段60/40、256KiB下限，排除若干主机 | ≥8MiB且有SHA-1/SHA-512、非BMCL才分；同60/40及256KiB，另有单文件段上限 | 拆段算法接近；门槛、主机例外和上限为独立取舍，尚无谁更快的比较 |
| 多任务同文件 | 全局NetManager统管不同LoaderDownload，同目标在途复用；取消只移除该订阅方 | 一次安装的分支共享预算，根安装写锁；UI主要单任务 | 本仓范围更小，不能说多任务完全还原或更强；不宜为“相似”直接取消根锁 |

关键上游位置：`ModDownload.vb:198–243,1215–1384`；`Modules/Base/ModNet.vb:808–885,1267–1282,1676–1838`；`Pages/PageDownload/ModDownloadLib.vb:1086–1214,1782–1832`。

本仓对应：[SourcePolicy](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.Core/SourcePolicy.vb:94)、[并行入口](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.Core/MinecraftInstaller.vb:143)、[组件入口](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.Core/ComponentInstaller.vb:50)、[引擎统计](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.Core/DownloadEngine.vb:400)、[分段调度](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.Core/PclSegmentScheduler.vb:91)。

### 4.2 缓存与复用：不能简单说本仓全面领先

| 类型 | 源码对照结论 | 适合的后续工作 |
| --- | --- | --- |
| HTTP元数据缓存 | PCL `ModNet:223–229` 已有 CacheCow FileStore / HTTP协商缓存，另有Loader和资源索引复用；本仓核心SourcePolicy没有等价持久HTTP缓存，页面60秒缓存不是同一种能力 | 先统计重复请求，再考虑尊重ETag/Last-Modified/Cache-Control的有界缓存；强制刷新和摘要不一致必须使旧缓存失效 |
| 连接中续传 | PCL会从失败分段已完成位置在其他源继续，并使用Range与磁盘临时段；不是没有续传 | 不再以“增加Range”作为尚缺的主任务；比较各自恢复条件、错误响应和清理边界 |
| 跨重试持久恢复 | 本仓有稳定内容键、完成临时文件、连续区间日志及重建检查，可供取消后新引擎/进程恢复；已读PCL路径对取消会Reset，进程首次任务清理下载临时目录 | 这是本仓明确的机制增强；补强杀后的真实重建测量、坏日志和同URL换内容，不能扩大为上游所有缓存重启都丢失 |
| 已有PCL实例内容 | PCL通用引擎会按size/hash在根与各版本的mods/resourcepacks等查找，可命中不同文件名，但要求实际size/hash俱全；固定MR导入仅传SHA-1，不能泛称全部MR导入都会走这条扫描。本仓普通复用主要同相对路径，另有自身摘要缓存 | 本仓对历史PCL社区文件复用范围较窄；建立单次安装有界候选索引，避免每文件重复全目录哈希 |
| 处理器输出 | 本仓只有完整可信输出摘要等条件满足才省略处理器，无outputs继续运行 | 可审查、保守；不能据已读VB调用链推断上游外部Forge安装工具完全无缓存 |
| 磁盘空间 | 已读PCL路径对>50MiB文件检查缓存/目标盘空间；本仓没有对应可用空间预检，且同时持有多种私有缓存 | 本仓有改进空间；按归档、解压、stage、分片、缓存估算峰值，并测试ENOSPC，不能只看最终文件大小 |

现有内容键/摘要缓存值得保留，但其副本、日志写入和峰值磁盘需求是代价。自动清理必须区分活跃租约、可恢复片段、已验证缓存与用户文件，不能只按目录年龄删除。

### 4.3 下载性能的合格对比方式

同一内容、URL候选、连接数、限速、缓存状态和机器条件下，分别测网络请求数、内容传输字节、有效吞吐、完成尾延迟、磁盘/哈希耗时与取消响应。至少覆盖1/4/8/32/128MiB、Range拒绝/忽略、断流、官方慢/镜像快、一大client+多小assets等场景。

实际部署的PCL正式二进制是否与固定源码一致须另外确认；本轮没有构建或运行上游程序，因此目前只能比较源码机制，不得宣称速度胜负。

## 5. 搜索、选择版本、页面信息与在线联动

### 5.1 工作流与结果规则

| 对照点 | 固定 PCL | 当前 PCL-DB | 判断 |
| --- | --- | --- | --- |
| 主操作链 | 搜索→项目→版本→名称→原始包下载→ModpackInstall | 同一用户顺序；核对详情与发布副本，归档后进入已有importer | 主流程已接通；归档位置和任务页归属不同，是架构适配 |
| 表单 | 名称/来源、版本/类型两行，28 DIP输入、35 DIP按钮 | 基本尺寸与顺序相同，列比例/间隔、按钮容器不同 | 骨架接近；上游6列含1.3*/1.2*及50间隔，本仓等分+20间隔，不是完全同布局 |
| 版本预设 | 固定源已列26.x和更多1.21.x，并从AllDrops补最高Drop | 固定下拉从1.21.1开始，允许手动输入 | 动态预设未对齐；可复用已接受原版清单，保留正在输入的内容，不增加重复请求 |
| 缓存与请求 | 60秒Loader复用、ShouldStart后更换Storage，详情目标来自已接受输入 | 条件副本、结构化键、在途复用、非负UTC60秒、请求对象身份 | 语义接近；本仓约束更显式，不能反推上游普遍会串请求 |
| 分页 | 40展示项、独立原始offset；不足继续取，按源提示错误 | 同40项和独立offset；每调用最多4轮，继续加载/保留末个有效页 | 有意加有界查询和退回有效页；但上轮发现的页码500/501上限不一致仍待修 |
| 搜索排序 | 整合包MR下载量×5；名称相似度、描述弱权重、下载对数等 | 名称全等/包含/其他三级，原始下载量，Key兜底 | **实质不同**，不能称结果排序一比一；可独立借鉴评分函数，不必合并身份 |
| 跨站去重 | ResourceProject.IsLike启发式判断同类工程 | 平台:ID独立身份 | 本仓避免错误绑定另一站文件，但同项目可能两行；可考虑显示分组，真实身份继续独立 |
| 中文能力 | 搜索词有繁简转换；Wiki替代搜索只针对Mod/数据包 | 无该Wiki搜索目录，直接转义后发包查询 | 不应误报“漏掉PCL整合包中文翻译搜索”；那不是上游此处已有的整合包能力 |

排序差异的简单公式样例：空查询同批次返回CF下载10万、MR下载5万，PCL按10万/25万排序，DB按10万/5万排序，先后相反。这是根据源码推导的固定数据例子，不是线上搜索质量实测。

固定上游 `ResourceSearcher.vb:161–163,186–193,405–411,455–524`；本仓 [ModpackSearchSession](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.Core/ModpackSearchSession.vb:63)、[查询与返回](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.App/MainWindow.ModpackSearch.vb:113)。

### 5.2 仍未对齐的视觉和信息层次

| 编号 | 源码差异 | 建议 |
| --- | --- | --- |
| S-01 项目统计行 | 两侧都有64 DIP行/50图/17+18+16文本；PCL另有类别Tag、版本/下载/时间/来源图标与共享列、版本范围/加载器、相对时间及万/亿格式；本仓为纯文本、GameVersions.Last、固定日期及完整数字 | 先扩展示模型和纯格式化器，再对齐图标/列/文字基线；完整信息放Tooltip，不能把API最后一项当完整兼容范围 |
| S-02 页码 | PCL为23 DIP容器中的双左/左/右图标，低透明禁用；本仓27 DIP文字按钮 | 复用现有图标按钮几何/按压，但先补对应Color主题的hover/disabled，保持可理解的辅助名称 |
| S-03 版本筛选 | PCL为MyRadioButton的Highlight模式及横向筛选；本仓是PclButton和WrapPanel | 先给PclRadioButton补Highlight模式，不能把只适合蓝色标题栏的白色模式直接放白卡；保留窄窗口换行优势 |
| S-04 版本行 | 42 DIP、类型图标、加载器/文件名主要接近；PCL还有发布下载量、相对更新时间；本仓没有发布下载量字段，用固定日期 | 可补统计字段及相对时间，保留精确日期Tooltip，CF零下载量按上游习惯不显示误导数字 |
| S-05 图像格式 | 上游有内置WebP解码路径；本仓图标通过BitmapImage/WPF解码，限制响应/像素/并发并有占位 | 有界加载是优点，格式兼容仍应按Windows/WIC环境受控验证；少一个库不证明所有WebP必然失败 |
| S-06 另存为 | PCL整合包版本行提供安装与另存图标；本仓主要直接安装，归档在校验缓存 | 可后置独立“另存原始包”，进入同一归档获取/校验，不为它复制第二套下载器 |

上游依据：`MyResourceItem.xaml:1–83`、`ResourceProject.vb:456–567`、`PageDownloadCompDetail.xaml:25`、`.xaml.vb:128–129,247–278`、`ResourceVersion.vb:340–376`；本仓 [项目行](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.App/Controls/PclResourceItem.vb:19)、[版本展示](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.App/MainWindow.ModpackReleasePresentation.vb:41)、[筛选](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.App/MainWindow.ModpackVersions.vb:16)、[分页XAML](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.App/MainWindow.xaml:161)。

### 5.3 应保留的增强和待修的身份问题

本仓按上游九项阈值逐次折叠Drop/远古分组，保留已接受目标、卡片懒构造和返回状态，语义已较接近。同卡片重名时，上游改用文件名；本仓继续用版本号、平台ID与最后序号兜底，同名文件也能区分。这是明确、可测试的增强。

发布副本固定了平台/项目/版本/文件与摘要，显示名称不作为缓存或安装身份。应保留这些约束，不能为复制PCL排序而合并跨平台真实ID。

上一份报告F-02仍需优先修：CF分支只保留数字正式版和周快照，丢掉pre/rc等声明，造成分组和目录到包清单MC核对退化；固定PCL对1.x/26.x前缀识别更宽。修复必须分开**精确原始ID、展示分组、兼容比较**，不能直接把上游归一化的“预览版”显示文本拿去比较包清单。MR保留精确列表、PCL对整合包收敛一个展示版本，也是有意不同的数据策略。

## 6. 安装、版本隔离、路径冲突与恢复

| 对照点 | 固定 PCL | 当前 PCL-DB | 还原性、优势与代价 |
| --- | --- | --- | --- |
| 库规划 | rules、普通/native、classifier、artifact显式path、版本比较和库去重；部分扩展路径另作特例 | 对应规划，另解析Maven的`@extension`并物化显式path供PCL读取；同目标合并强校验，矛盾提前失败 | 核心接近，特殊rules及扩展坐标处理不完全等价。强声明不因去重丢失是本仓优势，但严格冲突会拒绝某些错误元数据 |
| 实例中间状态 | 先写JSON，组件阶段有`.pclignore`隐藏，最后合并/迁移/补库并移除标记 | 私有stage，最终目录同卷Move；共享资源/库可先完成 | 本仓最终目录发布边界更直观；PCL已有中间状态保护，不能说上游毫无防护。两者都不等于全根目录断电事务 |
| 隔离判定 | V2优先、旧1/2、mods/saves、全局0–4，并可能惰性迁移保存V2 | 相近结果规则+三态；补全只读已有字节，不迁移 | 更符合补全器职责。不是完整PCL设置管理的替代；普通现代原版补全不额外解析隔离，旧resources映射才需读入/重检 |
| 共享Mods/旧资源 | 使用PathIndie对应目录并迁移附属Mod等 | 按实际隔离目录映射，冲突检测、短租约、只回收本次新增且仍匹配文件 | 本仓文件所有权更显式；代价是遇不同内容会拒绝，不自动覆盖；旧资源额外保留objects也占空间 |
| 两种整合包 | 已有CF/MR依赖、客户端optional/unsupported、多URL、强制隔离和覆盖顺序 | 同主要语义，MR同时检查SHA-1/SHA-512，外层归档身份锁，覆盖发布前复验 | 内容绑定更严格；更多I/O，依赖范围和格式兼容更窄，不是全面兼容性更好 |
| 格式容忍 | PCL还支持更多包格式，可提示跳过不支持加载器；CF overrides可用`.`/`./` | 当前两类格式、单一支持加载器；严格相对overrides目录拒绝根路径 | 属兼容范围差距。先找合法实包与排除manifest/生成文件的规则，再决定支持根overrides；不直接放宽未知必需加载器 |
| 取消/重试 | Loader组合、下载订阅和临时树按自身机制收束 | linked token、等待同伴退出、缓存保留、自有stage清理及最终重检 | 本仓边界清楚但同步大文件hash/copy仍可能慢；不能说取消必然更快 |

上游依据：`ModMinecraft.vb:240–277,1653–1760,2023–2031`；`ModDownloadLib.vb:22–29,1780–1891`；`ModModpack.vb:121–221,361–450`。

本仓对应：[库选择](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.Core/PclLibrarySelection.vb:12)、[声明合并](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.Core/DownloadPlan.vb:9)、[隔离计划](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.Core/PclIsolationPlan.vb:26)、[共享Mods](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.Core/SharedModsPublication.vb:25)、[包计划](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.Core/ModpackInstallPlan.vb:26)。

“更严格”也有明确限制：本仓无摘要共享运行库的弱复用Bug（上一报告F-01）还未修，不能把所有安装发布都说成已经可靠；BetterMC历史原包有必需文件查不到，仍未完成该包安装验收。Zombie本地MRPACK/在线发布有历史安装与内容比对证据，但未启动游戏。本轮没有重装任何实包，也不借源码还原程度替代PCL/游戏实际启动验收。

## 7. 工程、VS、依赖、资源与打包

### 7.1 对齐与独立工程改造

| 对照点 | 固定 PCL | 当前 PCL-DB | 评价 |
| --- | --- | --- | --- |
| 技术栈 | VB.NET WPF、net48，传统MSBuild；另有PCLCS/MeloongCore引用 | 同VB/WPF/net48，SDK式App/Core/Tests，严格类型 | 语言框架一致，工程拆分与独立命名空间是必要改造，不应机械复制完整启动器依赖 |
| 单EXE依赖 | 最早构造期AssemblyResolve，ConcurrentDictionary+Lazy，13个托管程序集前缀名单 | 同方向，内嵌Core/Json两项，按名称/版本/文化/public key token核对 | 内嵌顺序/缓存同源思想；精确身份是进一步约束，单EXE形态本身不是本仓独有优势 |
| 文档与候选 | 可见源有版本/提交注入和构建工件 | 候选绑定完整提交、源码输入、依赖/文档和摘要，再发布目录/ZIP/EXE/清单 | 可追溯性是本仓明确收益；需要持续维护schema/门禁，当前已有遗漏，亦非二进制可重现构建证明 |
| 可见CI | 上游`.github/workflows/build.yml`确有Windows Debug/Release构建与工件上传 | 当前仓库未见相应CI，主要依靠本地脚本/VS配置 | 本仓欠缺可见持续回归；建议Windows PR/手动检查默认只构建与离线回归，不默认打包/发版 |
| 独立产品图标 | 上游vbproj指定Images/icon.ico | 本仓无ApplicationIcon、自有.ico及Window.Icon配置 | Windows交付精致度可改进；应制作独立DB图标，多尺寸/高DPI，不能只把About圆标当PE图标 |

主要依据：上游主vbproj、`Application.xaml.vb:218–240`、`Resources.resx:121–173`、`build.yml:53–69`；本仓 [RuntimeBootstrap](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.App/RuntimeBootstrap.vb:20)、[App工程](D:/CodeProjects/Minecraft/PCL-DB/src/PclDownloadBridge.App/PclDownloadBridge.App.vbproj:3)、[候选检查](D:/CodeProjects/Minecraft/PCL-DB/tools/verify-single-exe.ps1:39)。

### 7.2 本仓自己的三个验证缺口

| 编号 | 状态 | 后续处理 |
| --- | --- | --- |
| B-01 历史清单兼容 | 当前所有v2清单被硬编码要求新UI/动画计数，误拒历史Beta658/328；前轮已复现且旧包字节自洽 | 分离历史结构/哈希完整性与当前精确候选门槛，不能改旧包来迁就工具 |
| B-02 搜索候选门禁 | 普通build跑pack-search，但单EXE候选、manifest、候选parser和VS入口没贯通该scope | 同步补scope/Succeeded/Count/Passed校验，处理系统关闭动画的合法分支；不能用普通多文件EXE通过代替精确候选通过 |
| B-03 新图片来源门禁 | Alpha/Beta/Release/NoIcon四图本轮Git blob均与固定PCL完全相同，但不在现有15项asset清单中 | 纳入来源、摘要及门禁。当前没有字节错误，这是防未来漂移的覆盖改进 |

签名和私有发布流程不能排名：上游工程里的 `SignAssembly=false` / `SignManifests=false` 不等于正式EXE没有Authenticode；公开更新入口为空也不等于正式发行版没有更新。上游MeloongCore固定gitlink已读到，但其对应子模块对象本轮不可读，未初始化/拉取，不推断内部验证能力。

当前本仓旧Beta资产不能充当HEAD的精确候选；本轮不制作新包。以后明确要求发行时，再定义新版本并绑定该次candidate result.json验证分发资产。

## 8. 根据对照结果，下一步适合做什么

### 8.1 明确推荐的下一轮

**先完成“内容正确性与游戏版本身份”小批次：修上一报告F-01、F-02；顺手修F-03的上限一致性，避免视觉工作建立在已知错误之上。**

范围：无摘要组件运行库发布、CF精确游戏版本解析/绑定、分页边界。保留原安装/下载架构，不拓展加载器或资源市场。

验收：同长度坏共享库不能被静默复用并发布实例；可信私有输出相同则复用，不同明确冲突并保留用户文件；CF pre/rc/新快照保持精确ID且错MC清单拒绝；分页上限保留当前内容。加入能在现基线上失败的正式回归，修后执行适当检查，再按用户后续任务要求Commit。本报告不代做这些修改。

### 8.2 第一批还原性工作：常见小控件收口

处理 V-01/V-02/V-03：白色标题栏图标按钮、滚动条本体状态、个性化圆形单选框。不动下载内核、搜索身份和页面树架构。

验收：源时长/曲线和最终几何可定位；蓝色/深色、按住/移出/快速反向、主题切换、隐藏/卸载、键盘/门控下均无旧时钟写回。标题栏点击/拖动区正确，滚动条offset不变，RadioButton互斥组不变。补统一减少动画的终态策略。

### 8.3 第二批：搜索呈现与可用信息

处理 S-01–S-04和动态MC预设：先扩展纯展示模型/格式化器，再补统计图标、范围/加载器、相对时间、发布下载量、图标分页和Highlight筛选。保留长内容Tooltip和窄窗口WrapPanel。

验收：条件输入不被刷新覆盖；同一平台/项目/发布ID及用户命名不因显示格式变化而改变；两平台数据缺字段、零值、多MC、同名文件都可辨；最小窗口及高DPI不挤压重要动作。视觉比较使用固定数据，而非比较两次随机搜索结果。

排序另做独立小批次：参考PCL相关性、平台权重和描述弱匹配，以平台:ID保持身份；用固定样本比较命中顺序与历史页稳定。不要把排序和跨站合并身份打包成同一项改造。

### 8.4 第三批：加载呈现与实测流畅度

处理 V-04/V-05：统一LoaderWait/Enter/Stay/Exit/Content状态；主导航保留110/140ms，侧栏130/160ms，内容刷新单独定义。现有时钟所有权、冻结画面和输入/焦点归属不变。

验收覆盖0/50/399/401ms、入场中完成、失败/重试、取消、切设置再返回、旧查询被替换、缓存翻页。记录100/150/200%DPI、冷/热页及下载负载下UI阻塞/帧间隔，再决定图标解码或布局优化。不要用增加动画数量代替流畅度验收。

### 8.5 第四批：组件预取和有效内容复用

先拆组件 `PrepareAsync` 与 `ApplyAsync`：准备阶段获取并校验安装器、解析依赖、下载附属文件；处理器仍只在必要client/libs就绪后执行。所有分支共享同一预算、token和私有work所有权。

验收：慢assets不阻塞已满足依赖的处理器；提前预取失败能取消同伴；晚取消/无Java/目标冲突不提前发布实例或共享Mods；预取与原版同路径冲突提前发现；等待全部分支退出才回收work。用冷/热缓存真实样本测总时间，不预设提速比例。

随后补已有PCL实例社区文件的有界索引复用；可靠长度可先筛候选，再以摘要确认，跳过链接/越界/不可读路径，供体只读、同内容异名可命中、同名异内容不能误用。MR的fileSize只是估值，须允许摘要兜底，不能仅按其声明大小淘汰正确供体。引入预算级吞吐指标前先保留分支与全局观测数据，验证公平性和尾延迟，不直接套用上游阈值。

### 8.6 持续验证、磁盘与发行边界

并行完成 B-01/B-02/B-03 和当前VS文档同步；可在不制作发行包的情况下先测试parser、回执与历史归档规则。增加可见Windows CI默认普通构建/离线回归，显式打包和公开发布保留独立触发。

之后评估有界HTTP缓存、峰值磁盘预检、CF根overrides兼容和原始包另存为。它们各有独立身份/空间/路径边界，不宜为“跟PCL一样”一次全部放宽。新增独立图标可与纯界面工作并行，实际游戏启动与干净Windows环境验收单列。

## 9. 如何证明还原更接近、机制确实更好

| 目标 | 可接受证据 | 不能替代它的证据 |
| --- | --- | --- |
| 静态视觉 | 同客户区/DPI/主题/字体环境、固定内容、相同状态截图和几何对照 | 资源相同、尺寸大致相近、挑一张最好看的预览 |
| 动画观感 | 相同交互轨迹及实时时钟，记录中断/返回/快速连续输入和帧间隔 | 源时长相同、seek采样帧或录屏编码FPS |
| 搜索还原 | 固定双平台数据、相同输入/分页/过滤，明确比较排序与分组规则 | 只看搜到几个结果，或把外部服务失败算本地算法缺陷 |
| 下载提速 | 固定网络/内容/缓存/连接/限速，对照关键路径与传输/磁盘耗时 | 暖缓存对冷缓存，或仅“已经支持多线程” |
| 安装正确 | 计划差分、最终文件与profile/隔离设置核对、冲突/取消/重试；另做PCL实际发现与启动 | 总文件数相同、任务页成功、只跑Java -version |
| 可交付 | 当前源码的精确候选、其全部验收回执及分发哈希，冷环境检查 | 旧Beta包、普通bin构建、只有一张SHA清单 |

## 10. 源码和取证索引

本报告的详细两侧行号矩阵与来源记录均位于：`D:\CodeProjects\Minecraft\PCL-DB\artifacts\pcl-comparison-20261022-f8601e4`。

| 材料 | 位置 |
| --- | --- |
| 本轮基线 | `baseline.json` |
| 动画/视觉详细矩阵 | [animation-comparison.md](D:/CodeProjects/Minecraft/PCL-DB/artifacts/pcl-comparison-20261022-f8601e4/animation-comparison.md) |
| 下载/安装详细矩阵 | [download-install-comparison.md](D:/CodeProjects/Minecraft/PCL-DB/artifacts/pcl-comparison-20261022-f8601e4/download-install-comparison.md) |
| 搜索/联动详细矩阵 | [search-comparison.md](D:/CodeProjects/Minecraft/PCL-DB/artifacts/pcl-comparison-20261022-f8601e4/search-comparison.md) |
| 工程/资源/打包详细矩阵 | [build-comparison.md](D:/CodeProjects/Minecraft/PCL-DB/artifacts/pcl-comparison-20261022-f8601e4/build-comparison.md) |
| 搜索固定源码路径和blob | `pcl-search-source-index.json`，对应十份文本在`pcl-source/` |
| 新增四图字节/门禁对照 | `search-resource-byte-comparison.json` |
| 沿用的同提交检查 | `D:\CodeProjects\Minecraft\PCL-DB\artifacts\audit-20261022-f8601e4\validation-summary.json`及完整日志/回执 |

固定源码复核示例：

```powershell
git -C 'D:\CodeProjects\Minecraft\PCL' show '5ef81e490ae46b1100d61e33d0183094c7a80ca8:Plain Craft Launcher 2/Controls/MyPageRight.vb'
git -C 'D:\CodeProjects\Minecraft\PCL' show '5ef81e490ae46b1100d61e33d0183094c7a80ca8:Plain Craft Launcher 2/Modules/Resource/ResourceSearcher.vb'
```

`artifacts`为本机忽略目录，综合结论和下一步方案已完整写入本报告，不依赖仅链接附件才能理解。原业务源码、测试、用户包、游戏实例、PCL参考仓库、既有报告及发行工件均保留；本轮只读对照不意味着上述建议或上一报告缺陷已经修复。
