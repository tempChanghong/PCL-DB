# PCL2 代码依据

## 本地 Mod 识别、启停和导入

- 核心依据为 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `LocalResourceFile`、`LocalResourceLoaders`、`PageInstanceMod`、`MyLocalModItem`：实际隔离目录、启停后缀、导入启用名称、44 DIP 行、搜索/筛选、右侧启停图标及既有页面动画。后缀只移除末尾一次，不沿用中间字符串替换或导入前删除目标的行为。
- 本地元数据格式补充参考 PCL-CE `915ac29795efbdf6373417389e181e1504117048` 的 `ModLocalComp`，独立实现有界只读解析，不调用会写配置或联网关联的上游服务。来源 Git blob 与 SHA-256 在 `artifacts/local-mods/source/manifest.json`。
- 写入使用独立的文件句柄校验/重命名、实际目录互斥与安装锁；异常/取消仅回收本次拥有的新增文件，保留配置和已有内容。实现与验证边界见[本地 Mod 管理](docs/reviews/local-mod-management.md)。

## 已安装实例列表、概览与只读目录解析

- 以固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `McInstance/PathIndie/JsonObject`、`PageSelectRight` 和 `PageInstanceOverall` 为核心，复用现有 PCL 列表行、卡片、标题返回与逐项动画。独立适配读取规则，省去上游缓存、配置初始化以及快捷目录的自动创建。
- PCL-CE `915ac29795efbdf6373417389e181e1504117048` 仅补充搜索区与信息分层；CE/HMCL 的目录与隔离沿用已有只读原生适配。保留同一目录的不同启动器身份、外部设置和实例文件。
- 管理页原版补全接入既有安装器，最终仍须通过原版元数据对照；本地候选识别不等于完整校验。新增页面原字节快照与 Git blob 记录在 `artifacts/installed-management/source/manifest.json`，范围与证据见[本地实例管理第一批](docs/reviews/installed-instance-management.md)。

## 大整合包的读取与覆盖内容提取

- 重读固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 ModModpack：两格式读取清单后通过 ExtractModpackFiles / CopyOverrideDirectory 处理覆盖内容，该层没有本工具误加的 128 MiB / 2 GiB 容量筛除。
- 移除本地及在线整合包的固定容量上限，保留现有流式提取、摘要/声明长度、路径和发布事务校验。实际 ZIP/MRPACK 及大容量夹具验证见 [修复记录](docs/reviews/large-modpacks.md)，不推断 PCL 依赖库内部未提供的实现。

## 卡片箭头的初始化与旋转替换

- 重读固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 MyCard.Init / IsSwapped 和 ModAnimation.AaRotateTransform / AniEaseOutFluent，修正默认展开卡片的初始方向，保留 180°/0°/270° 状态及 250 ms ExtraStrong 曲线。
- 旋转复用已有动画槽，自然完成、隐藏和卸载释放旧时钟，快速反向从显示值起步；重复方向属性不打断当前动画。保留独立高度、悬停与页面动画，详见 [卡片箭头验证](docs/reviews/card-arrow.md)。这是既有 WPF 适配的完善。

## 卡片悬停的颜色与时钟归属

- 重读固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 MyCard.MouseEnter/MouseLeave、ModAnimation.AniType.Color 和 ModBase.MyColor：保留 90 ms、ARGB 通道插值，以及文字 Brush2/1、阴影 Object4/1 和 0.4/0.07 透明度。
- 标题、箭头和阴影复用已有动画槽，重复目标不重启，完成、主题刷新及生命周期交接清理旧时钟；深色阴影不再误取文字色。保留折叠与页面动画独立归属，详见 [卡片悬停验证](docs/reviews/card-hover.md)。系统动画偏好与 WPF 生命周期处理是本工具适配。

## 分页按钮与缓存页面的输入归属

- 重读固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 PageResource 分页按钮、ChangePage 与 MyPageRight 入退场命中门控，保留现有分页卡布局、滚动、加载及页面动画。
- 首页、上一页、下一页共用当前搜索页/输入来源校验，再调用原有分页方法，避免设置或详情页收到缓存按钮的迟到事件后替换后台结果。正常缓存返回、快速返回放行后的翻页和本页继续加载保持。详见 [分页入口验证](docs/reviews/paging-entry.md)；这是本工具缓存页面的补充，不直接移植 PCL 后台线程中的 100 ms 等待。

## 搜索与重置的界面入口

- 重读固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 PageResource.StartNewSearch / EnterTrigger / ResetFilter 和 MyPageRight 的入退场命中门控。保留同一搜索流程、回车入口、重置草稿和缓存行为，以及已确认的搜索分层与动画。
- 搜索按钮、回车和重置图标统一经当前搜索页/输入归属校验，再进入原有内部方法。隐藏的缓存搜索控件或尚未放行的页面不能因迟到输入替换查询或清空条件；内部自动搜索、分页与加载机制保持。详见 [搜索入口验证](docs/reviews/search-entry.md)，这是本工具复用缓存页面后的补充防护。

## 详情操作与模态输入交接

- 重读固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 PageDownloadCompDetail 复制入口、ModBase.ClipboardSet 和 MyMsgInput：保留四次尝试、200 ms 间隔及现有输入框进退场。项目操作继续异步等待，不阻塞 WPF 线程。
- 本工具的待重试操作在介绍卡直接或继承失去启用/命中权限时退役，避免弹窗关闭或页面门控恢复后重新改写剪贴板、补发旧反馈。局部版本卡片入场不影响仍可交互的介绍卡。详见 [交接验证](docs/reviews/project-action-handoff.md)；取消语义是本工具异步操作的补充，不宣称原版已提供相同取消接口。

## 原版版本行与折叠退场

- 重读固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 MyCard、MyPageRight 与 PageDownloadInstall：原版列表绑定版本 Tag，使用延迟构建和 Lambda 转发点击，退场控件不再接收操作。保留本工具已有的卡片几何、折叠/页面曲线、版本名/数据绑定与键盘返回。
- 原版版本行点击前核对当前页面、输入门控、版本对象和仍展开的当前卡片，避免旧行在折叠退场、清单替换后进入安装页。有效缓存和补充版本增量插入继续允许选择，不依赖加载代次一刀切作废。详见 [归属验证](docs/reviews/vanilla-row-ownership.md)；这是本工具异步清单的补充防护，不宣称固定 PCL 有相同校验函数。

## 纵向静态滚动与动画模式交接

- 重读固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 MyScrollViewer、PageDownloadInstall 和 PageDownloadCompDetail，保留正常的 300 ms 六次方缓动、嵌套控件路由和页面几何。原版以 RealOffset 累积增量并同步原生位置；本工具关闭平滑滚动或系统动画后的静态分支也需保留每次输入。
- 静态分支复用现有目标与原生确认队列；门控、隐藏、卸载及指针/键盘/触摸接管均可撤销待确认请求。静态与动画切换先处理已接受的位移，不增加逐滚轮同步布局。详见 [静态滚动验证](docs/reviews/immediate-scroll.md)。这是本工具静态回退的补充，不宣称原版提供相同回退实现。

## 筛选栏连续滚轮与原生请求交接

- 重读固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 MyScrollViewer 与 PageDownloadCompDetail.xaml(.vb)，保留单行筛选、分组、原版垂直滚动曲线和已确认的布局。PCL MyScrollViewer 按增量累积 RealOffset、逐步夹紧并以 ScrollChanged 同步实际位置；本工具横向溢出适配也需要区分待确认目标和已显示位置。
- 修复连续滚轮被原生 ScrollViewer 队列合并而丢失距离，补齐门控、隐藏、键鼠接管、尺寸变化和清单刷新时的旧请求清理。横向适配继续使用既有的 `-Delta / 3`，它是本项目的补充，并非声称固定 PCL 提供相同横向实现。详见 [对照与验证](docs/reviews/filter-wheel-bursts.md)。

## 卡片折叠与输入交接

- 主要重读固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 MyCard、PageDownloadInstall 和 PageDownloadCompDetail。保留 40 DIP 折叠高度、展开时 34 DIP 标题点击范围、90 ms 悬停、原有分段高度曲线和 250 ms 箭头动画，没有采用新的 CE 布局。
- 本工具额外取消失焦、捕获丢失、主题变化及程序/键盘主动折叠前尚未完成的标题按压；只撤销旧手势，不清理仍有效的高度或箭头时钟。普通点击、内容缓存与文件身份保持。详见 [折叠交接验证](docs/reviews/card-gesture.md)。

## 筛选与标题按钮的按压所有权

- 以固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 MyRadioButton.xaml(.vb) 为核心：保留 90 ms 悬停、120 ms 选中、150 ms 移出以及两种角色的按压时长与配色；保持实际详情筛选的单选与卡片身份规则。
- 固定源控件主要通过 MouseLeave 取消按压。本工具保留的键盘焦点功能还需处理 LostKeyboardFocus、主题替换和 LostMouseCapture；这三类中断统一清理按压和旧颜色时钟，不修改 Checked 或触发 Click。详见 [对照与验证](docs/reviews/capsule-gesture.md)。

## 输入弹窗关闭后的焦点与视口

- 重读固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 MyMsgInput、MyScrollViewer #3854 和 MyPageRight：保留原输入框焦点、弹窗进退场及页面入场机制，沿用避免焦点自动改变滚动位置的处理思路。
- 为桥接程序的异步弹窗补上调用控件与页面所有权。关闭后恢复仍可用且在视口内的原控件，否则聚焦滚动容器并保持当前位置；导航、新弹窗、新任务和用户后续焦点选择可使旧请求失效。这是本工具的补充实现，不宣称固定 PCL 已提供同样的异步返回 API。详见 [焦点返回验证](docs/reviews/prompt-focus.md)。

## 滚动输入归属与详情卡片间距

- 重读固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 MyScrollViewer、MyPageRight、MyMsgInput、ModAnimation 与 PageDownloadCompDetail：保留 300 ms 六次方 Fluent 滚动、卡片入退场和模态交互，详情介绍卡下方间隔恢复为源代码的 25 DIP。
- 本工具另补齐禁用、门控、隐藏、卸载和动画偏好变化时的滚动清理，防止旧时钟及待处理原生请求在恢复后继续推进；这是桥接程序的生命周期防护。筛选栏禁用时消费旧滚轮，避免冒泡到外层原生处理器。详见 [对照和验证](docs/reviews/scroll-handoff.md)，没有改变搜索或下载安装内核。

## PCL 核心样式与图标文字动画

- 界面布局、控件和动画以 PCL2 为核心，PCL-CE 仅作补充参考。重新读取固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 MyIconTextButton.xaml(.vb)、ModAnimation.AaColor、ModBase.MyColor 和 PageDownloadCompDetail，校正默认无描边、网站高亮、自然图标比例、150 ms 移出及按压时背景/文字的独立动画。
- 复用已实现的 PCL ARGB 字节线性颜色动画，实际中间帧与旧时钟替换检查见 [还原记录](docs/reviews/pcl-action-parity.md)。保留已确认的搜索分层和线性导入图标，不调整搜索或安装内核。

## 提示条明暗、分层搜索与紧凑操作

- 主要依据固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Controls/MyHint`、`MySearchBox` 和 `MyIconTextButton`：保留浅色提示原值、40 DIP 搜索框、90 ms 清空过渡和 27 DIP 图标文字按钮；再对照 CE `915ac29795efbdf6373417389e181e1504117048` 的 `PageComp.xaml`、`PageDownloadCompDetail.xaml`、`MyHint.xaml.cs` 及 `ToneProfileConfig.cs`，适配深色语义色、搜索/筛选分层与详情次要操作。
- 导入入口改用 CE 同款 Lucide download 圆角线性路径，许可随文档保留。按钮继续使用原生 WPF 输入/自动化与本工具时钟槽和门控，清空保留查询/结果所有权；未修改查询、下载安装或外部配置机制。详见 [对照与验证](docs/reviews/ce-ui-polish.md)。

## 版本筛选栏的单行横排与关系验证

- 重读固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `PageDownloadCompDetail.xaml(.vb)`、`MyRadioButton.xaml(.vb)`：PanFilter 为 Horizontal／Left／Top 的 StackPanel，边距 `10,10,0,10`；按钮为 Highlight 单选、边距 `2,0,2,0`、文字边距 `-2,0,10,0`、高度 27 DIP 和圆角 13.5 DIP。当前已有胶囊绘制继续保留，本轮将原 WrapPanel 改为同源的横向 StackPanel；普通筛选卡为 47 DIP 高。
- 按用户截图的“全部、26.3、26.2、26.1、1.21、1.20、1.19、1.18、1.17、1.16”验证：850／810 DIP 宽度下完整单行显示、无多余水平滚动条。上游的四阶段分组、目标版本选择、仅一组隐藏筛选栏继续保留；筛选／返回全部仍复用本工具已有卡片和行，避免破坏展开状态及安装版本身份。
- 超宽容错属于本工具适配：横向视口只在溢出时启用已有 8 DIP PCL 样式滚动条，支持原生滚动命令和 BringIntoView；适配行消耗横向滚轮，普通行把滚轮交回外层详情页。页面门控／禁用阻止横向输入，重新初始化版本列表才清零横向位置。固定源码和验证见 `artifacts/filter-bar-alignment/`、`docs/reviews/filter-bar-alignment-2026-10-23.md`；此前“保留 WrapPanel”的阶段记录为历史实现。

## 整合包发布版本的下载量与相对更新时间

- 重读固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Modules/Resource/ResourceVersion.vb`：分别读取 CurseForge downloadCount／Modrinth downloads；ToListItem 仅显示大于零的计数，超过 100000 时用 Math.Round(count / 10000) 显示“万次”，并调用 StringUtils.FormatTimeSpan(ReleaseDate - Date.Now, False)。保持 42 DIP 行、标题消歧、文件名及加载器说明、发布类型和原平台 ID。
- 上述 StringUtils 位于该 PCL 提交的 MeloongCore 子模块，Git 对象实际固定为 `c774ae4c85fd94e0eaa171db21e78a65870df19b`。读取此提交的 `Shared/Utils/StringUtils.cs` 和 Apache-2.0 许可；仅把详细时间分支适配为显式 DateTimeOffset 参数的 VB.NET 纯格式化器，没有引入运行时程序集。许可和修改说明已加入 THIRD_PARTY_NOTICES.md；固定函数的 1120 组边界／随机对照另有回执。
- 本工具适配：计数使用可空 64 位整数，缺失或异常辅助统计降级为未知，不影响文件身份、大小和摘要的严格校验；零计数与未知仍在模型中区分，界面均省略。完整数字和绝对本地时间保留在提示中，单张卡片以同一个时间点生成文字；不增加后台计时器或每帧布局。源码及 Git blob／SHA-256 记录位于忽略目录 `artifacts/release-metadata/source/`，详见 `docs/reviews/release-metadata-2026-10-23.md`。

## 搜索项目行的输入归属与独立背景动画

- 重读固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `MyResourceItem.xaml(.vb)`：Button_MouseLeave 在移出和松开时清除 IsMouseDown；RectBack 是 3 DIP 圆角、0.8 初始缩放的独立背景。RefreshColor 进入／按下使用 120ms 颜色和透明度、144ms 背景缩放，悬停 ColorBrushBg1、按下 ColorBrush6；离开 180ms 淡出并接近 0.996，之后 1ms 回到 0.8。重读 ModAnimation 的默认 Fluent 三次方曲线和 After 分组规则，本工具资源行不再共用 MyListItem 的整行 0.98 缩放及 541ms 离开尾段。
- 新 `PclResourceHoverMotion` 保留当前合成起点、固定画刷、同状态去重和旧时钟所有权；沿用已有 ARGB 颜色适配器。鼠标移出取消按下；键盘记录精确 Space／Enter，不能由另一按键或鼠标松开完成。键盘焦点高亮、系统动画偏好、门控／隐藏／卸载／主题清理和关闭动画时的即时反馈是本工具的 WPF 适配，不声称上游已有同一键盘与策略实现。
- 三份源码与摘要保存于忽略目录 `artifacts/resource-row-motion/source/`。已有 44 份固定快照不改；详见 `docs/reviews/resource-row-motion-2026-10-23.md`。

## 整合包版本筛选胶囊与颜色动画

- 再次读取固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `PageDownloadCompDetail.xaml(.vb)` 和 `MyRadioButton.xaml(.vb)`：详情筛选使用 Highlight 单选按钮、27 DIP 高度、13.5 DIP 圆角、按钮边距 `2,0,2,0`、文字边距 `-2,0,10,0` 和筛选容器边距 `10,10,0,10`。选中为 ColorBrush3 背景／白字 120ms，按下 ColorBrush6 90ms，悬停 ColorBrush7／主题文字 90ms，离开半透明背景／主题文字 150ms；按下分支只改变背景，不重启文字过渡。
- 读取同提交 `ModAnimation.AniType.Color` 与 `ModBase.MyColor.Lerp/ClampToByte` 的 ARGB 通道插值和字节舍入。新 `PclRadioColorAnimation` 使用线性字节通道插值，并沿用本工具的 `PclMotionSlot` 所有权与完成清理；修复旧顶部选项在颜色过渡结束后仍保留 HoldEnd 时钟的问题。原始 MyRadioButton XAML 保持不变，White 模式保留既有顶部导航色彩和 60ms 按下时序。
- WPF 适配：筛选容器保留 WrapPanel，窄窗口可以换行；键盘焦点使用悬停色，Space／Enter 交给当前详情所有者决定是否切换，不由控件自动修改 Checked。保留已有版本卡片／行及请求代次，忙碌、页面门控、隐藏、禁用、卸载与主题切换遵守统一控件动画策略。源码快照及六项摘要保存在忽略目录 `artifacts/pack-filter-capsules/source/`；详见 `docs/reviews/pack-filter-capsules-2026-10-23.md`。

## 历史补充版本首批接入

- 继续参照固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `DlClientListBmclapiMain` 附加清单合并、`ShowMinecraftVersionList` 分类排序、`McDownloadListItem` 的 42 DIP 行、远古圆石／快照命令方块图标及日期。补充卡片仍延迟构造、复用现有进出动画，异步补充不重建普通行，不参与最新推荐。
- 2026-10-23 从 [UVMC 清单](https://zkitefly.github.io/unlisted-versions-of-minecraft/version_manifest.json) 及对应 `files` 元数据逐项核对 `c0.0.12a_03`、`a1.1.1`、`b1.9-pre6`、`13w12~`。前三者保留 old_alpha/old_beta，后者保留 snapshot；路径分别固定，后两者使用 `b1_9-pre6`、`13w12-`，不根据显示名称推导任意路径。UVMC 是独立公开档案，不代表恢复 PCL 私有特供目录。
- 四份实际元数据保存于测试专用 `Resources/Historical/`，不编入产品。当前清单没有元数据 SHA-1，按已核对来源／身份／结构读取；客户端、支持库、资源计划仍要求摘要和大小。新增历史条目缺少支持库信息时明确拒绝，不复用 2.0 的参考清单。真实下载后的客户端大小／SHA-1 与来源声明吻合；这不等于 Mojang 签名认证或实际启动验收。

## 特供版本范围调整：2.0 与官方 rd 版本

- 再读固定 PCL `5ef81e4` 的 `ModDownload.DlClientListBmclapiMain`：上游合并 `Cache/download.json`；本机该缓存当前为空数组。`ModDownloadLib.McDownloadListItem` 根据来源地址添加特供提示；`ModMinecraft.GetMcFoolName` 使用 `2.0` 前缀识别 2013 愚人节描述。当前卡片延用 PCL 折叠、按需创建行及普通列表先显示的机制，来源明确标记 UVMC，未伪造 PCL 的私有缓存内容。
- 2026-10-23 实读 Mojang `version_manifest_v2.json`，`rd-132211` 已有官方 `old_alpha` 条目及版本 JSON 摘要；它保留在远古版，不合成重复补充条目。UVMC 的 `2point0_blue/red/purple` 对应实际元数据 `2.0_blue/red/purple`。该阶段补充允许表仅保留这三个显式映射，后续扩展见上节；下方 20 个战斗/实验条目的记录为先前阶段，现已替换。
- 三个 2.0 JSON 的旧 Maven 声明均缺少下载记录。逐项对照 [Mojang 1.5.1 元数据](https://piston-meta.mojang.com/v1/packages/3c514114d9c2a3ea78f72c4f9fb4eeb56747135a/1.5.1.json)，以 URL 中对应的 `3c514114d9c2a3ea78f72c4f9fb4eeb56747135a` 校验原始字节，再按去除 downloads 后的完整库声明匹配，补齐官方 artifact/classifiers 信息。它是本项目为保持文件校验而做的适配，未复制其他版本的客户端或启动参数，匹配不到或存在矛盾时停止。
- 测试资源 `Resources/SupplementalLibraryReference.json` 保留上述官方 JSON 原始字节；仅嵌入测试程序集，生产按选源策略请求该固定地址并验证 SHA-1。控制测试覆盖坐标、仓库、平台规则、本机库声明的变更、参考清单损坏及取消重试。

## Beta 2.0 后续：CurseForge 目录接口与空值

- 再读固定 `Meloong-Git/PCL@5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Modules/Minecraft/ModDownload.vb`：`DlModRequest` 通过 `DlSourceModGet` 在 Core API 与 MCIM 之间选源，`Resource/ResourceVersion.vb` 的 `FromProjectId` 消费该格式并检查列表完整性。PCL-DB 保留现有分页、取消和身份校验，移除目录请求中错误混入的网页 API 通道。
- [CurseForge Core API 文档](https://docs.curseforge.com/rest-api/#get-mod-files) 的文件结构含 `modId/fileDate/hashes/downloadUrl`。2026-10-23 真实网站文件列表返回 `projectId/dateCreated/status` 且缺少下载与摘要字段；它需要独立适配，不能直接按 Core API 解析。本次只修正整合包目录，现有单文件导入适配器继续保留。
- MCIM 当日实际响应中的 `isEarlyAccessContent` 为 JSON null；三个可选布尔标志按本项目原有缺失字段的默认语义读取，并保留显式布尔值及类型检查。这是对实际响应的兼容修正，不声称上游已有完全相同的辅助函数。目录回归同时验证 ID、摘要、密钥主机绑定和取消重试。

## 审查第三批：归档兼容、搜索验收与 VS 入口

- 再次从固定 `Meloong-Git/PCL@5ef81e490ae46b1100d61e33d0183094c7a80ca8` 读取 `Plain Craft Launcher 2.vbproj`、`Application.xaml.vb:217–232` 和 `.github/workflows/build.yml`：上游以 net48 WPF EXE 为交付主体，分别构建 Debug/Release，并保留资源嵌入、启动期 AssemblyResolve / Lazy 缓存及构建版本身份。本项目继续使用现有资源解析器和独立单 EXE 候选；不复制上游私有配置、账号字段或发布流程，也不据工程的签名开关推断正式发行版签名状态。
- v3 候选、四组实际 EXE 摘要绑定回执、v2 历史归档兼容与统一验证策略，是针对本项目审查 F-04/F-05/G-01 的独立修正，并非声称 PCL 已有同一套清单格式。归档完整性与当前源码候选验收分开；原 Beta 及其历史验收数字保留原字节。搜索检查在 Windows 动画关闭时核对静态状态，避免少执行一项而迫使验证脚本放宽门槛。
- B-03 的 `Images/ReleaseTypes/{Release,Beta,Alpha}.png` 与 `Images/Icons/NoIcon.png` 再次逐字节核对固定 Git blob，补入 `reference/pcl-assets.json`，总计 19 项资源。它们在本仓继续位于 `Images/Blocks/`，导入器显式记录原路径到目标路径的映射；原图未改写，44 份源码快照保持不变。
- 本批只修改构建/验证入口、调试配置、来源清单与检查回执；不修改下载、版本隔离、共享 Mods、实例发布、搜索排序和生产动画机制。新候选与发行实测留待明确要求打包时执行。

## 审查第二批：标题栏、滚动条和个性化单选框动画

本轮再次直接读取 `Meloong-Git/PCL@5ef81e490ae46b1100d61e33d0183094c7a80ca8`，原 44 份固定源码快照保持原字节。新增依据包括同提交的 `Controls/MyScrollBar.vb`，通过本地 PCL Git 对象读取；原启动器工作区不修改。

| 上游源码 | 本仓实现与保留行为 |
| --- | --- |
| `FormMain.xaml:107–110`、`MyIconButton.xaml.vb:56–157` | `PclIconButton` 的 White 角色及两个标题栏按钮；原 28 DIP 点击区、0.72 图标比例和矢量；400 ms 缩至 0.8，250 ms 弱 Back 与强 Fluent 并行回弹；120/150 ms 背景变色，悬停 alpha 50。继续复用 `PclPressMotion`。 |
| `MyScrollBar.vb:10–43`、`Application.xaml` 中 MyScrollBar / MyThumb | 空闲 0.5/180 ms、悬停 0.9/130 ms、捕获 1/50 ms，ColorBrush4/3/4；8 DIP 点击宽度和 2 DIP Thumb 内边距。保留 WPF 原生 Track、分页命令、拖动和横向滚动。 |
| `MyRadioBox.xaml`、`MyRadioBox.xaml.vb:102–152,169–236`、`ModAnimation.AaScale` 与 Fluent/Back | 18 DIP 外环、9 DIP 圆点、1.1 DIP 描边；150 ms 基准、90 ms 延后、300 ms 外环回弹，完整选中动画 390 ms；取消选中 150 ms，按住/离开 1000/400 ms；颜色 100/200 ms、填充 100 ms。`PclRadioAnimation` 保留并行增量与中途反向的当前起点。 |

有意适配：标题栏使用持续浅色图标，避免本项目深色 ColorBrush8 令图标变暗；滚动条状态透明度放在模板内部，页面进出的外层透明度与位移保持独立；不可见时立即归零并清时钟，上游隐藏分支的 20 ms 不保留为后台动画。单选框保留原生 RadioButton 互斥组、方向导航及自动化选择规则，环/点改在固定 20×20 DIP 区域绘制，描边不随缩放变粗细。空格待释放输入单独归属当前控件，页面门控、失焦、隐藏或卸载可取消；自动化操作也遵守同一门控。

三类控件均按 Windows `ClientAreaAnimation` 决定局部动画；变化时收束终态，卸载时取消系统偏好订阅。颜色与几何各自拥有时钟，主题更换即时更新颜色而不重启正在选中的环/点；旧时钟完成仅能处理自己的属性。`PauseForChecks` 仅为内部隔离检查开关，正常运行不暂停动画。本轮不改变下载、搜索、隔离、安装发布或已有实例设置。

## 审查第一批修复：运行库内容身份、CF 精确版本和分页边界

- 再次直接读取固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `ModDownloadLib.McInstallLoadersGet`：组件先在私有树处理，再迁移运行库、按 `PathIndie` 放置附属 Mods，最后补支持库。本工具保留私有组件树和最终实例目录发布；本次为无上游摘要的私有运行库固定本地 SHA-512/长度用于内容一致性比较，避免将同长度共享文件直接认定为正确。这个本地摘要不是上游声明或额外来源认证。无摘要获取不使用共享根的弱供体或跨安装弱缓存；已有不同内容明确冲突并保留原文件。组件发布及最终实例提交各自持短期读租约，后者只复核已固定内容，不从当前共享文件重新建立信任。
- 对照 `ResourceVersion.FromPlatformJson` 的 CF `gameVersions` 拆分和 `McVersion.IsStandardFormat` / `GetVanillaName` 中的正式、pre/rc、年式快照识别。上游会为了显示归一化预览版本；本工具的目录到包清单核对需要保留完整原 ID，因此以整串识别区分 MC 与 Forge/Java 等标签，不把显示族名当安装身份。缺失/null 声明保留原先由归档决定的路径；显式空、错误类型或全未识别声明保留条目并阻止自动安装，不使同页正常版本失效。
- 对照 `PageResource.Load_OnFinish` 按已有结果和来源剩余量决定页码入口的机制。保留本工具每调用四轮的有界查询，并统一 `MaxPage=500`：满上限页不提供非法下一页，未满仍可继续补查。界面拒绝非法请求时不撤销有效查询、图标请求或当前结果；这属于本工具自己的边界修正，并非复制上游页码上限。
- 既有隔离优先级、共享 Mods、已有实例补全和设置字节保持策略沿用原实现；本批不移植完整启动器设置管理，不修改动画曲线或扩展打包功能。

## Beta 1.0 后续修复：详情信息与在途缓存

- 再次读取固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `PageDownloadResourceDetail.Init`：由当前 Project 建立不可交互的项目简介行；核对 `ResourceProject.FromProjectId`、`ResourceVersion.FromProjectId` 的项目及版本缓存，以及 `PageResource` 通过已接受查询传递详情筛选目标的方式。保持现有成功详情及版本卡片的缓存策略，同一项目尚在加载时往返复用已接受请求，切换项目和失败重试继续按请求所有者判断。
- 本工具原先会额外请求项目详情，却只把返回简介写入隐藏标签。现在保留原搜索对象作为请求身份，另存经同一次流程核对的项目信息；通过既有 `ChangePackContent` 接受项目与版本后，更新可见标题、简介、统计和提示。可见简介、转到项目页面、复制名称及安装命名统一读取与当前平台/项目键匹配的详情，重开时不再被缓存搜索摘要覆盖。搜索结果本身保持原快照，不原地改写排序依据。
- 安装确认前固定项目键和名称，继续优先保留对应发布版本的自定义名称；版本 ID、归档、隔离与发布机制沿用原实现。搜索缓存的 60 秒窗口增加非负年龄约束，系统时钟回拨导致的未来时间戳会重新查询。这是本工具采用 UTC 计时的边界修复，没有改变 PCL 的项目缓存实现。

## Beta 1.0 后续修复：搜索分页边界与异步内容动画

- 再次读取固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `PageResource.LoaderInput` / `StartNewSearch` / `ResetFilter` / `Load_OnFinish`，以及 `ResourceSearcher.SearchRequest.CanContinue`、来源类型检查和原始游标推进。每页展示 40 项、条件快照及各来源独立游标保持不变；来源与类型没有交集时，参照上游明确的不兼容提示，在替换已接受查询之前拒绝条件，避免误报普通无结果。
- PCL 的页码区域按已有结果数量及来源剩余内容决定显示。本工具保留有界查询批次：经过过滤后暂时为空但游标仍能前进时，显示「继续加载」而不是隐藏入口；来源提前结束或下一页失败时回到缓存中的最后一个有效展示页，保留排序及来源错误。这一空页回退是本工具的边界修复，没有复制上游用最后一条结果填补越界页的表现。
- 读取 `MyPageRight.PageLoaderState` / `PageOnExitAnimationFinished` 的加载完成到内容入场，以及 `TriggerEnterAnimation` / `TriggerExitAnimation` 的时序。详情的版本、筛选、错误提示现在有独立内容动画区域，继续使用已有 `PclSurfaceMotion` 的 PCL 曲线和错峰时序，项目简介与导航保持可用；入场最初阶段的安装、筛选及重试遵守同一输入门控。
- 异步结果在分类或主页面入场期间完成时，先由正在拥有视口的控制器捕获当前合成画面，再释放旧控件时钟、替换内容并续接入场；不另叠加一个子页面动画。隐藏响应只更新所属内容。主导航、下载侧栏、安装进度及关闭窗口统一清理搜索与详情两个内容控制器，进度切换先捕获详情当前画面再释放它。这些控制器所有权和冻结画面是本工具的 WPF 适配，未声称上游使用相同实现。

## Beta 1.0 后续修复：整合包发布版本的辨识与确认

- 对照固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `PageDownloadCompDetail.InitializeStackResourceVersions`：每张卡片按发布时间排列，存在相同 Display 时以 IsBadDisplay 要求版本行显示文件名。本工具按去除 ZIP/MRPACK 扩展名及首尾空白后的可见标题检查重名，使用大小写不敏感比较；文件名仍重复时再追加声明版本号、平台版本 ID，最后防止生成的标签互相碰撞。这些额外兜底属于本工具的展示适配，不替代真实版本身份。
- `ResourceVersion.ToListItem` 的 42 DIP 行、发布类型图标、加载器、不同于标题的文件名、纯快照/预发布版本的游戏目标、更新时间和 ReleaseTypeDisplay 是信息层级依据。正式版省略英文 release，测试版/早期测试版按上游中文文案显示。继续使用静态本地日期，尚未复制上游相对时间和版本下载次数；完整文件信息放在 ToolTip，不增加行高，也不将整合包内的 Mods 当作项目依赖数。
- 名称确认仍使用已有 PCL 风格弹窗和入退场时钟，补入可见版本标题及完整文件名；任务的可复制信息保留相同内容。平台、项目、版本 ID 与文件元数据在弹窗前复制，名称记忆也使用此副本 ID，避免 Await 期间目录对象变化后将名称归属到另一个版本。显示标签只用于展示，不传给归档缓存、文件校验或实例发布作为身份键。

## Beta 1.0 后续修复：在线整合包的加载器兼容性

- 继续读取固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `ModModpack.InstallPackCurseForge`、`InstallPackModrinth`、`NotifyIncompatibleLoader`，核对从包内清单选择固定 Forge、NeoForge、Fabric 依赖的顺序；详情行继续以 `ResourceVersion.ToListItem` 的 42 DIP 行、标题、次要说明及发布类型图标呈现。上游提供不兼容时跳过加载器的手动选项，本工具保留原有“所需加载器不能跳过”的自动安装约束。
- [Modrinth 版本接口](https://docs.modrinth.com/api/operations/getversion/) 将 `loaders` 定义为该版本支持的加载器列表。本工具据此把多个已识别标签当作兼容候选集合，而非同时安装的依赖：Fabric/Quilt 混合标签允许继续核对 Fabric 清单；只有已识别且全部不受本工具支持的标签集合才在自动安装前阻止。缺失或出现未知标签时交给已有包解析器判断，不以目录标签生成依赖或绕过包内必需加载器。
- 新增 `ModpackCompatibility` 供页面与安装入口共同使用；已知不支持的版本保留为灰色行，并在说明与禁用状态下的 ToolTip 中展示原因。安装核心在下载/创建缓存前复查，并在校验归档、解析清单后核对 MC 版本及已完整识别的加载器候选集合，早于可选文件询问、游戏与组件传输及原子发布。标签别名归一化仅用于兼容性核对，实际依赖版本仍完全来自包内清单。
- 兼容性页面快照另发现首次展开的版本卡片仍显示向下箭头。建立卡片时先确定折叠态，再设置最终展开态，使懒加载内容、箭头角度与高度状态一致；继续沿用既有 `PclCard` 和动画曲线。箭头方向依据固定 PCL 的 `SwapLogoRight` / `IsSwapped` 规则，展开态为 180°。

## Beta 1.0 后续修复：搜索异常恢复与安装重试

- 继续读取固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `PageResource.xaml.vb`（`StartNewSearch` / `ResetFilter`）、`ResourceSearcher.GetLoader`（60 秒 ReloadTimeout）和 `MyPageRight.vb`（`PageLoaderState` / `PageLoaderRestart`）。本工具先验证新条件，再替换旧请求；无效编辑留在搜索表单内，以既有 PCL 红色提示控件呈现，不取消有效查询或覆盖平台状态。重复提交相同的在途条件复用请求，缓存命中不再把有效期向后延长；结构化条件键避免名称和版本中的分隔符产生身份碰撞。这些为本工具异步实现的适配修复，没有移植上游 Loader 类。
- 搜索及详情按当前请求的所有权区分主动取消和异常取消：已被替换或关闭的请求不能提交界面，当前请求意外取消则必须进入可重试错误状态。搜索错误恢复完整宽度和内容，诊断详情放在提示的 ToolTip；退出加载时仍通过既有 `PclSurfaceMotion` 交接，保持原 PCL 退场/入场动画的时钟所有权。没有把非用户取消误报为完成，也不无限停留在镐子加载页。
- 再次核对 `PageDownloadCompDetail.Install_Click` 的名称确认、原始包下载及统一安装流程。本轮未改变归档或安装内核，只验证失败任务返回详情后保留版本卡片和自定义名称、重试创建独立取消令牌，以及错误动画切换到设置后不能覆盖新页。该失败/重试界面检查使用受控安装委托；它不替代真实包安装验收。

## Beta 1.0 后续修复：整合包版本筛选与卡片状态

- 继续读取固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Resource/MyResourceItem.xaml.vb`：整合包详情的目标 MC 版本来自 `FrmDownloadPack.Content.Loader.Input.GameVersion`，不是尚未提交的搜索编辑框。`ModpackSearchSession.Query` 现在只返回已提交条件的副本，详情据此固定目标版本。
- `PageDownloadCompDetail.xaml.vb` 的 `Load_OnFinish`、`UpdateFilterResult`、`GetGroupedVersionName`、`CardSorter` 是本轮依据。按“精确版本 → 主版本 → 远古版本合并 → 两者同时”的顺序，在筛选项达到九项时收拢；只有一个筛选组时隐藏筛选卡。文件按所有兼容版本逐一归类，同一张卡内去重；命中的目标版本置顶展开，没有匹配文件则选择“全部”。继续使用已依据 `PCLCS/Minecraft/McVersion.cs` 移植的 `MinecraftVersionRules.ReleaseFamily`，保留新版本编号及快照分类。
- 每个项目的版本卡片和筛选按钮保留实例，切换筛选只改变显示和高亮；展开状态、已构造的版本行及原有 PCL 折叠动画继续使用。筛选后隐藏的旧行、已被替换的旧按钮不能触发安装或修改新详情。新项目或重新取得版本清单时重建缓存，旧请求仍受项目和请求所有权校验。没有为筛选增加整页退场动画，卡片内容边距对照上游 `20,40,18,18`，标题保持原版版本标签。

## Beta 1.0 后续开发：整合包搜索与在线安装

- 主参照仍固定为 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`。读取 `Resource/PageDownloadPack.xaml`、`PageResource.xaml/.vb`、`ResourceSearcher.vb`、`ResourceProject.vb`、`ResourceVersion.vb` 和 `PageDownloadCompDetail.xaml/.vb`，按搜索、项目选择、版本选择、名称确认、下载原始包、统一安装的顺序接入。当前 PCL 只在安装动作上增加限制判断，搜索和另存路径仍保留；未把当前受限点击行为作为在线安装依据。
- 搜索采用独立平台偏移、40 项显示分页、按平台映射的分类、部分失败提示和查询缓存；项目/文件身份始终带平台。与上游的相似项目合并和下载量加权有所区分：同名跨平台结果不合并，下载失败不切换到另一平台的同名整合包。Modrinth 在有效 MRPACK 候选中选择 primary，不照搬上游的 `files(0)`；外层归档严格校验声明大小及受支持的摘要，包内文件继续使用既有规则。
- 参考 `ModDownload.DlModRequest` / `DlSourceModGet` 的社区请求通道划分，CurseForge 目录查询支持自己的 API 密钥、网页公开接口和 MCIM 回退；独立于原版 BMCL 设置，不复制 PCL 凭据、设备身份或下载追踪参数。版本列表使用有限分页并检查重复原始 ID。缓存放在 `.vanilla-downloader/pack-archives/`，继续复用原有格式解析、组件安装、隔离、校验和原子实例发布；未将上游提前创建最终版本目录的做法移植过来。
- `MyResourceItem.xaml` 的 64 DIP 行高、50 DIP 图标及 14/12 DIP 字号，`ResourceVersion.ToListItem` 的 42 DIP 版本行，`MyHint` 的蓝/黄/红颜色和 `MyPageRight` 的切页机制作为界面依据。新增 ReleaseTypes 的 Release/Beta/Alpha 和 NoIcon 四张原始资源。`MyLoading.xaml/.vb` 与 `ModAnimation` 的镐子动画按 250ms 等待、350ms 弱回摆、900ms Fluent+弱 Elastic 并行动画及碎屑时序实现；控件隐藏/卸载时释放时钟。搜索结果、右页分类与主导航切换按当前合成画面交接，旧动画和旧请求不能覆盖新页面。
- `ModModpack.InstallPackModrinth` 的客户端 optional 逐项询问用于在线安装；CurseForge 按其 ModList 默认包含可选项。本地导入复选框仍由用户选择。名称确认在归档下载前完成，取消不创建安装任务；归档下载、清单解析及现有安装内核构成同一可取消任务，任务页另列归档阶段。
- 命名及可选文件弹窗另对照 `Controls/MyMsg/MyMsgInput.xaml/.vb`：23 DIP 标题、2 DIP 分隔线、22 DIP 内边距、左侧中心旋转原点；入场从 -4°/Y=40 开始，60ms 后执行 120ms 淡入与 300ms Weak Back/Fluent 位移和旋转，退场按 80/150ms 动画及 200ms 遮罩淡出处理。新弹窗取得所有权后，旧进入/关闭时钟不能再隐藏遮罩或解锁背景。

## Beta 1.0 后续修复：整合包重读状态与页面键盘输入

- 继续依据固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `ModModpack.InstallPackCurseForge` / `InstallPackModrinth`：可选文件身份与用户选择分离，安装仍查询指定文件并校验。CurseForge 卡片现在按原包 SHA-256 保留名称、选择及已解析的显示名称，新的查询失败仍允许取消已选项；不同内容不继承。它是独立导入卡片的状态管理修复，不把保留的显示数据交给安装解析器或缓存为可信下载元数据。
- 读取 `PageDownloadLeft.PageChangeRun` 和 `MyPageRight.TriggerExitAnimation/TriggerEnterAnimation`：保留退场时停止内容命中、110 + 30 ms 交接和 25 ms 逐项入场。本工具的 WPF 文本框及原生 CheckBox 能在 IsHitTestVisible 为 False 时继续收到键盘，因此在 Shell 的预览路由中拦截受限内容的按键、组合文本和新焦点请求；Escape 仍交给既有导航。复选框在 OnClick 中额外复核，防止入场前按下、退场后释放的空格触发选项。这是 WPF 适配补充，未声称上游有同样的键盘事件实现。
- `MyCheckBox.xaml` 的 13 DIP 文本及 MyListItem / 安装页的标题与次要说明作为样式依据，CurseForge 的标题、12 DIP 统计和可选项标题与 Modrinth 卡片一致。切换选项同时更新当前表单归属；旧控件回调不能覆盖重读后的选择或夺走另一张卡片的归属。保留原动画曲线和下载边界，未重写侧栏或引入新页面。

## Beta 1.0 后续修复：Modrinth 选择状态、页面与隔离复核

- 再次核对固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `ModModpack.InstallPackModrinth`（361–485 行）：先跳过客户端不支持或未选择的可选文件，再检查实际下载路径。清单读取仍校验所有条目的格式、摘要、地址和路径；文件与目录冲突按必需文件及实际选中项判断，在请求元数据或写入游戏目录前拒绝互相冲突的选择，未勾选项不阻止有效安装。
- `MyCheckBox.xaml` 使用 FontSize 13 的 TextBlock、26 DIP 文本留白和 18 DIP 勾选框；本工具移除会把下划线当访问键的 AccessText，保留既有选择动画，增加省略及完整路径提示。可选文件按上游提示使用文件名，同名文件才显示相对路径以区分。`PageDownloadInstall.xaml` 的名称及次要说明、`MyListItem.xaml` 的文本省略作为信息层级参照；独立导入卡片适配标题、简介和统计，并非声称上游已有相同的本地导入页面。
- `CopyOverrideDirectory`（121–144 行）为整合包同时设置 `VersionArgumentIndie:1` 和 `VersionArgumentIndieV2:True`。发布前现在始终重读并校验这两个值，即使包内未附带 Setup.ini；沿用 PCL 的冒号、区分大小写和后值覆盖规则。设置被删除或变为无效/非隔离时不发布实例，缓存保留供重试；原有实例和全局设置不改写。共享的整合包校验同时用于 CurseForge。
- 实际选择立即重算可选文件数量及预计载荷；重新读取相同 SHA-256 的原包保留自定义名称和选项，不同内容重新初始化。这是本工具独立卡片的状态修复，未归为 PCL 原有机制；统计继续注明不含游戏和加载器，详细校验信息放在提示中。

## Beta 1.0 后续开发：Modrinth MRPACK 导入

- 主要参照固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `ModModpack.InstallPackModrinth`（361–485 行）、根清单优先/单层包装目录探测和 `CopyOverrideDirectory`（121–144 行）：固定加载器、client required/optional/unsupported、downloads 数组、overrides 后 client-overrides、隔离设置，以及整合包与游戏安装的非阻塞组合。`ModDownload.DlSourceModGet` 和 `ResourceVersion.ParseModrinthTrackArguments` 用于比较选源；本轮使用包内原始地址和顺序回退，未把 Modrinth CDN 错配为 BMCL，也未把未经实测的 MCIM 映射当作可用来源。
- [Modrinth 官方格式](https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack)用于核对 SHA-1/SHA-512、HTTPS 地址列表、客户端环境和覆盖层。沿用 PCL 不把 fileSize 用作硬性校验的行为，服务端长度仅用于 Range/续传，最终验证两种摘要；补齐域名及重定向检查。
- 与 CurseForge 共用 ModpackInstallPlan、精确组件安装、下载预算、只读原包绑定、隔离暂存和最终目录发布；格式解析、可选文件身份及安装记录各自独立。以胜出覆盖文件的摘要验证最终内容，保留清单下载的双哈希和覆盖来源，不因合法覆盖而错误套用旧哈希。
- 实包的 1176 个覆盖文件暴露了每个循环未归零的复制长度累计；已修复公共复制流程，并用连续普通文件和跨层覆盖用例复现/回归，CurseForge 同时受益。两张卡片沿用原 PCL 控件、25ms 逐项入场与现有导航动画，不增加新的下载侧栏分类。


## Beta 1.0 后续修复：整合包目录与动画交接检查

- 再次读取固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `ModModpack.CopyOverrideDirectory`：PCL 在 Windows 临时目录中检查并复制覆写目录，再处理 PCL/Setup.ini。本工具直接读 ZIP，却曾用区分大小写的前缀筛选，导致 `Overrides` 与清单 `overrides` 不一致时静默漏装。现在清单规划和实际复制同时按 Windows 的大小写规则匹配，保留原始文件名，继续拒绝重复/大小写碰撞和越界路径，不放宽已有实例覆盖或原子发布边界。
- 核对 `PageDownloadLeft.PageChangeRun`、`MyPageRight.TriggerExitAnimation/TriggerEnterAnimation` 的退出、延迟入场和输入锁定关系，以及固定 `PageDownloadInstall.EnterSelectPage/ExitSelectPage` 的横向切换。独立 WPF 适配层新增的修复是：父页面切换前先捕获当前合成画面，再释放右侧动画；原版列表在类别入场期间完成刷新时，由已经拥有右侧视口的动画控制器续接。未声称 PCL 原版采用相同的冻结位图实现。
- PclListItem 的键盘适配已遵守 IsHitTestVisible；动作/图标按钮及名称提交入口原先未统一遵守。现在键盘激活和安装入口同样尊重页面输入锁定，解除后恢复正常，不借禁用所有控件改变入场时的外观。新增真实 WPF 路由事件、两个层次的输入锁定和像素区域比较检查；不把离线 HWND 测试称为桌面帧率测试。

## Beta 1.0 后续开发：下载侧栏与 CurseForge 独立卡片

- 再次读取固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Pages/PageDownload/PageDownloadLeft.xaml/.vb`：使用原版游戏和整合包的原始图形路径、36 DIP 行高、顶部 12 DIP 留白与禁用整行缩放的侧栏行为；页面保留实例，切换时不重建。这里只保留用户要求的两项，135 DIP 固定侧栏宽度是本项目对两项导航的适配。
- 参考 `Resource/PageDownloadPack.xaml`、`MyListItem.xaml.vb`、`MyPageRight.vb` 和 `PageDownloadLeft.PageChangeRun` 的卡片、选中标记与右页退出/入场关系。复用已按 PCL 实现的 PclListItem、PclCard、PclSurfaceMotion；侧栏切换使用同一 110 + 30 ms 交接、卡片入场曲线，保留顶部主导航和原版安装页原有横向动画。新增动画所有权、快速反向、隐藏列表刷新、设置页交接和最小尺寸检查，避免两个控制器同时修改右页。
- 重读 `ModModpack.InstallPackCurseForge` 的实例名称验证、固定加载器、ModOptionalList 与缺失文件处理。独立卡片保留整合包名称/可选文件；原版的组件、隔离和补全设置单独保留，开始任务时从当前类别复制输入。可选文件展示使用相同文件身份/摘要查询器，最多 4 个并发，只有当前请求能更新选项；并非把原版组件选择拿来替代清单中的加载器。
- ZIP/文件信息查询、校验缓存、版本隔离和原子发布仍沿用前一轮已验证的核心实现；这轮没有更换校验规则、下载源或发布边界。公开接口的少量真实复核与回环/预览验证分别记录在 VALIDATION.md。

## Beta 1.0 后续开发：CurseForge ZIP 导入

- 主要依据仍为固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`，不是受限后的安装入口。重读 `Modules/Minecraft/ModModpack.vb` 的根目录/单层目录类型判断、InstallPackCurseForge、CopyOverrideDirectory、ModOptionalList、缺失文件处理及 McInstallLoadersGet 组合；参考 `ModDownload.vb` 的 DlModRequest / DlSourceModGet，以及 ResourceVersion 的 SHA-1/MD5 和资源文件类型处理。源码摘录保存在本轮 artifacts；未修改 PCL 或其他参考仓库。
- 保持指定 MC/加载器版本、projectID/fileID、可选项、overrides 和整合包开启隔离的关系。模组分支与原版/资源/加载器共用 DownloadBudget，整体等待并取消同伴后才提交；配置在加载器处理后合入，防止组件生成的 Setup.ini 覆盖包内偏好。保留其余包内 INI 项，但固定 VersionArgumentIndie/V2 为隔离并取消星标。已有实例和全局设置均不改写。
- 文件信息使用独立的官网公开接口适配及 MCIM 回退，可选自己的官方 API Key；没有复制上游密钥、身份或鉴权代码。PCL 通过官方 API 批量查询；本工具以最多 4 个并发按项目/文件 ID 精确查询，逐项验证 ID、MC、客户端可用性、路径、长度与摘要。PCL 允许用户确认后继续缺项，本阶段明确失败，避免把不完整实例当作成功。
- 官网接口缺少 hashes 时，以官方文件下载入口 HEAD 的受限重定向获取对应 forgecdn 对象。仅在文件身份/长度、AmazonS3、SSE-S3 AES256 和强 ETag 全部匹配时使用单段 MD5 或 `-1` 单分片组合 MD5；不接受未知多分片边界、KMS、弱 ETag 或任意站点 ETag。依据为 [AWS 对 S3 ETag 的定义](https://docs.aws.amazon.com/AmazonS3/latest/userguide/checking-object-integrity-upload.html)及[单段/多段说明](https://docs.aws.amazon.com/AmazonS3/latest/userguide/mpuoverview.html)，这属于本工具的独立渠道适配，不称为 PCL 原有实现或 API 提供的 SHA-1。VerifiedComponentCache 对组合 MD5 使用独立命名空间，复用和续传仍按完整校验值验证。
- ZIP 输入先验证重复/大小写、路径片段、文件/祖先冲突、链接、条目和解压大小上限；只从已固定摘要并持有只读句柄的原包复制 overrides。下载与配置均在实例私有载荷中完成，最终再次校验；同名外部实例保留，失败回收自有载荷，正常共享文件和可信缓存保留。沿用原 Directory.Move 发布边界，不声称能把共享库、资源和实例组成断电安全的跨目录事务。
- [CurseForge 官方 API](https://docs.curseforge.com/rest-api/)和 [MCIM 当前服务状态](https://github.com/mcmod-info-mirror/mcim-rust-api)用于核对渠道。2026-10-22 的 BetterMC 实测区分了「85 个可用载荷校验通过」与「原包因一个必需文件缺失而无法完整安装」，没有跳过该条目或将下载检查称为游戏启动成功。

## Beta 1.0 后续修复：安装参数按任务固定

- 重读固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `ModDownloadLib.vb`：McInstallRequest（约 1687–1728 行）表达实例名、目录、Minecraft/组件版本和附加组件；McInstallLoadersGet（约 1733–1777 行）在建立加载器时确定版本与临时目录；McDownloadFabricLoader（约 1618–1635 行）使用传入的版本字符串构造 ID 和清单 URL。本工具继续保持这些版本/目录关系，并在应用提交、核心安装入口和独立组件安装器入口复制各自需要的输入。深复制条目与嵌套 JSON 是本工具为可变对象增加的保护，未声称上游对整个请求实现相同深拷贝。
- 原 ComponentSelection.Copy 只复制字典，条目及 Data JObject 仍与目录缓存共用。现在条目值和嵌套 JSON 独立，Java 选择及条目描述按任务保留；VersionEntry 的 ID、来源、摘要和补充身份在异步阶段保持一致；可选复用目录列表在首次回调/await 前读取一次，后续分支只用内部列表。复用内容仍须按原摘要验证，损坏源仍回退 HTTP，不把目录列表快照等同于文件内容可信。
- 新任务重新复制当前输入，不将上次任务的对象或文件名缓存到安装器；取消后更换附加组件文件名仍可复用相同摘要对应的有效内容。输入对象复制不回写或恢复调用方状态，不修改 PCL 的 INI、注册表或已有版本配置，也不改变既有共享 Mods 冲突、短暂读保护和原子发布/回收机制。

## Beta 1.0 后续修复：共享 Mods 提交边界与已有实例分类

- 固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `ModMinecraft.vb` 约 240–277、316–321、697、1348–1357、2025 行共同构成依据：PathIndie 保留已保存的隔离值，Modable 除加载器 flags 外还包括实例手动分类 `DisplayType = McInstanceCardType.API`，枚举值为 2；旧资源仍写入 PathIndie/resources。补全的全局策略 1/3 原先漏掉此分类，本次补齐；已保存 V2、旧版显式设置和非空 mods/saves 仍先于全局推断。分类来自原样读取的已有 INI，参与同一提交前配置复核，不写入或迁移已有设置。仅在使用 Modable 的全局策略 1/3 中拒绝不可解析分类，已有明确隔离和不使用 Modable 的策略不受影响。
- 固定 `ModDownloadLib.vb` 约 1835–1866 行先合并版本再按 PathIndie 安置 Mods、资源包和共享支持库。本工具继续沿用其目录关系，不扩展已有加载器实例的补全范围。本次修复的是本工具新增的暂存/原子提交保护，未把读锁或失败回收归于上游原有机制。
- 共享 Mod 的原有摘要/长度验证和最终 SHA-1 内容记录持有同一个 FileShare.Read 句柄，防止两次打开间的改动变成新的可信摘要。发布时对已有同内容 Mod 和本次新移动的 Mod 保持短暂读锁，允许读取，阻止其他步骤期间改写、删除或重命名；锁覆盖最终实例目录发布，成功/失败均释放，异常或取消时先释放再回收本次新增且内容仍匹配的文件。仅对少量共享 Mods 启用，不给大量旧资源别名新增逐文件常驻句柄，也不在下载/处理器执行期间长期锁定用户 Mods。
- 无上游摘要或长度依据的 OptiFine 不会因同名文件或上次中断而获得跨任务可信缓存身份，重试继续重新获取并按既有规则验证；有摘要的客户端、API、OptiFabric、支持库和资源对象继续校验复用。临时哈希算法回调仅存在于测试进程，生产代码未新增测试钩子。

## Beta 1.0 后续开发：安装页三态隔离与共享 Mods

- 继续以固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 为准。`ModMinecraft.vb` 约 240–277 行的 PathIndie、316–321 行的 Modable 和 633–664 行的状态分类共同决定隔离：显式新安装选择优先，跟随模式读取 PCL 全局策略；已有实例补全始终保留原有设置和优先级。组件状态会覆盖快照/愚人节状态，不能仅用原版 type 判断非正式版。另核对固定 `PCLCS/Constants.cs` 的 ModLoaders 和 `PCLCS/Minecraft/McVersion.cs`：OptiFine 独立于 ModLoader flags，单独安装不算 Modable。未读写运行中 PCL 的设置缓存或注册表。
- `ModDownloadLib.vb` 约 1835–1873 行先合并版本、确定 PathIndie，再复制 Mods、按 Modable 创建 Mods 和创建资源包目录；OptiFineAsMod 与 LiteLoader 的 `mods/<mc>/` 子目录保持。共享 libraries、assets 的存储与跨阶段并行不变。原先组件强制开启隔离的实现由此替换；前一阶段记录保留在下节。
- `PageDownloadInstall.xaml.vb` 约 1161–1173 行提醒关闭隔离会共用 Mods。本工具在三态控件下显示兼容性提示，补全模式禁用控件，任务提交时固定当次选择并禁用更改。使用既有 PclComboBox、PclCard 样式和动画，不新增经典系统选择框。
- 本工具在 PCL 行为基础上增加共享文件保护：组件 Mods 下载至私有暂存，发布前固定已校验内容的 SHA-1 和长度，拒绝不同内容、重复/大小写文件名、文件目录及联接冲突。无远端摘要的 OptiFine 仍先按既有安装器内容校验，再以暂存内容摘要比较共享目标，不能只比较文件名或大小。只有本次新建的共享文件参与失败回收，同内容已有文件保持字节、修改时间和只读属性。
- 旧资源与 Mods 的逐文件原子移动处于同一最终提交范围，实例仍最后使用同卷 Directory.Move 发布；异常/取消触发两组自有新增文件回收。共享文件与实例目录不是跨目录的单一文件系统事务，断电/进程强杀后可能保留共享文件或空目录；既有修复仍保留已完成的逐文件修复。未声称与 PCL 安装器完整事务等价，也未进行真实游戏启动或真实 OptiFine 处理器验收。

## Beta 1.0 后续开发：旧资源的隔离目录与已有实例补全

- 重读固定 `5ef81e4` 的 `McInstance.PathIndie`、`McAssetsListGet` 和 `Settings.vb`：已保存的 VersionArgumentIndieV2 优先，随后考虑旧版显式隔离、实例内非空 mods/saves 及五种 LaunchArgumentIndieV2 策略；默认值 4。map_to_resources 使用 PathIndie/resources，objects、indexes 和 virtual/legacy 保持共享。另用固定提交的 ModBase.IniGetContent 确认配置只解析冒号和区分大小写的键、重复键以后项为准，未把 PclFolders 目录兼容读取器的等号扩展用于隔离判断。
- 本工具仅在确有 remap 文件时解析隔离路径，主应用显式传入 PCL 所在目录。已有实例配置只读，包含缺失、原始字节、编码及只读属性；新建原版 remap 实例在私有载荷中保存已解析的隔离值，再随实例发布，防止第一次 PCL 初始化选择与已落盘目录不符。现代 object-only 安装不新增隔离偏好；组件安装维持原有明确开启隔离的策略，本阶段未扩展安装页的三态选择。
- 对象与 virtual 仍由共享资源引擎下载，remap 别名随后从已验证对象复制到暂存载荷，不重复请求同一资源；客户端/支持库/组件与资源分支继续共用连接及速度预算并行推进。新隔离实例用原 Directory.Move 一次发布。新非隔离实例拒绝覆盖不同的共享旧资源，最终发布失败只回收本次创建的文件；补全在元数据和隔离输入复核后逐文件原子替换，已完成的修复保留。路径冲突、原子提交、输入保护和回收均为本工具适配，未声称复刻完整 PCL 下载/安装事务。
- 支持库、日志和旧资源映射的逻辑路径拒绝父级路径、绝对路径、Windows 保留名称及尾随点/空格，保持在各自存储目录内；尤其避免动态组件清单提前写入最终实例目录，破坏暂存发布。复制继承的只读属性只在删除自有暂存副本或回收本次新增文件时清除，已有用户文件、资源对象和配置属性保持。

## Beta 1.0 后续开发：校验补全的元数据提交保护

- 重读固定 `5ef81e4` 的 `ModDownloadLib.McDownloadClientLoader`：新安装读取客户端元数据，设置实例 id 和 clientVersion，并组合客户端、支持库与资源流程。本工具继续保留这一身份约定；校验补全则额外要求已有原版 JSON 与所选元数据相符，并保持已有 JSON 和 PCL 偏好原样。
- 补全开始时从同一份原始字节解析并记录 JSON，保留 StreamReader 的 BOM 编码识别；最终替换客户端之前，再读取和比较确切字节。仅最终检查及客户端替换期间持有允许读取、阻止写入/删除的文件句柄，下载期间不长期占用 JSON。发生外部变化即停止最终替换，已校验客户端缓存可在解决冲突后重试复用。这是本工具为独立补全流程增加的并发保护，不宣称上游提供同样的字节快照或提交锁。

## Beta 1.0 后续开发：缓存复用与目标替换错误

- 重读固定 `5ef81e4` 的 `ModNet.CheckExistingFiles`：已有文件检查/复制失败时回退下载。PCL 没有本工具的摘要寻址私有组件缓存；本工具继续保留“可选缓存读取或复制不可用时，尝试下一缓存或下载”的机制。
- 私有缓存现在与既有普通文件复用保持相同的错误边界：仅缓存查找、检查和复制位于可选失败范围，最终目标替换发生的真实错误直接返回，不被当作缓存失效而重复下载。已校验副本在成功、失败、取消后都由原 finally 清理；取消仍向调用者传播。该目标替换分层属于本工具适配，不宣称上游使用同样的原子提交或私有缓存。

## Beta 1.0 后续开发：可靠分段响应与单连接回退

- 重读固定 `5ef81e4` 的 `Modules/Base/ModNet.vb` 第 917–977 行：后续线程从 DownloadStart 请求 Range，响应长度与预期剩余长度不符时抛出 RangeNotSupportedException，进入不支持分段的处理；不会将明确错误的长度仅当作普通连接中断。本工具继续使用自身“结束并等待分段工作，再回退单连接”的结构。
- 分段响应现在和既有单连接续传检查一致：要求 206、bytes 单位、明确且匹配的起点/终点/总长；如有 Content-Length，须等于本次请求的完整区间长度。验证在打开已保留分片之前进行，不使用并行切分后可能缩短的动态边界。没有 Content-Length 但有完整有效 Content-Range 的响应仍按边界读取并校验最终文件。单位和字段存在性门禁属于本工具对 HttpContentHeaders 的适配，未宣称原 PCL 使用相同解析代码。

## Beta 1.0 后续开发：资源对象地址、校验与复用

- 重读固定 `5ef81e4` 的 `ModMinecraft.McAssetsListGet/McAssetsFixList`：资源对象使用哈希前两位及完整哈希寻址，旧版 virtual/remap 使用逻辑名称，下载器核对声明大小和哈希；`ModDownload.DlSourceAssetsGet` 将资源地址映射到官方/BMCLAPI，并沿用来源顺序。片段保存在 `artifacts/beta1-asset-object-validation/pcl-assets.txt`。
- 本工具原先允许大写 SHA-1，却直接将其拼入大小写敏感的下载地址。现仅将计划中的对象哈希、地址及对象路径归一为小写，保留索引原始字节和逻辑名称，旧版映射及资源复用仍使用既有下载器。额外严格要求 objects/条目为对象、hash 为 40 位十六进制字符串、size 为非负 Int64 整数，在任何载荷下载之前拒绝隐式字符串/布尔转换、小数取整及溢出。这些归一化和结构门禁属于本工具适配，不宣称上游执行同样的严格解析。

## Beta 1.0 后续开发：UTF-8 BOM 元数据兼容

- 重读固定 `5ef81e4` 的 `Modules/Base/ModNet.vb`：`NetRequestByClient` 在响应解码后检查 JSON 格式，失败可由重试/备用来源处理。本工具保留“读取完整响应、校验摘要、解码解析、检查结构、再确认来源”的流程，仅修复自身 UTF-8 解码到 JSON 解析之间未处理前导 BOM 的兼容缺口。
- SHA-1 始终覆盖完整原始字节；通过后仅跳过字节流最开头的一次 UTF-8 BOM，不改 JSON 字符串中的 U+FEFF，JSON 解析仍拒绝重复/截断或出现在空白之后的 BOM。下载到磁盘的资源索引保留原始字节及其摘要；这是本工具适配，没有宣称移植或复现上游 `GetString` 扩展的所有编码行为。

## Beta 1.0 后续开发：补充版本清单

- 安装后续检查阅读固定 `ModMinecraft.vb` 的 `McAssetsGetIndex/McAssetsGetIndexName`，以及固定 `ModLaunch.vb` 生成 `${assets_index_name}` 的路径：PCL 优先读取 `assetIndex.id`，该名称与索引 URL 相互独立。同一上游资源索引 ID 在不同战斗测试版中可能对应不同 SHA-1；本工具因此仅为新补充实例改写索引名为 `pcldb-` 加完整小写 SHA-1，仍保留原 URL、摘要、大小、assets 字段及客户端身份，普通实例不改名，资源对象库继续共享。旧补充实例的补全保留 JSON；遇到已有共享索引内容不匹配时拒绝覆盖。独立命名及旧格式保护属于本工具的冲突处理适配，不宣称 PCL 上游采取相同命名。

- 后续恢复检查再次阅读下述 PCL 合并及 PCL-CE 忽略 UVMC 异常的路径。补充来源的响应解码失败同样属于可选来源失败：核心层处理 net48 对无效字符编码的异常包装；界面层单独隔离整个补充完成过程，保留普通清单成功状态并记录意外异常，过期、安装中与关闭中的失败不覆盖当前状态。数量与分类提示以合并后的实际补充项为准。这些错误隔离及提示属于本工具适配，不改变上游版本身份或文件校验要求。

- 重读固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Modules/Minecraft/ModDownload.vb` 中 `DlClientListBmclapiMain`：读取正常列表后合并 `Cache/download.json`。`Pages/PageDownload/ModDownloadLib.vb` 的 `McDownloadListItem` 使用原条目 Tag、42 DIP 行、分类方块图标、说明文字及来源前缀，安装继续进入正常客户端/支持库/资源流程；`PageDownloadInstall.xaml.vb` 按 release/snapshot/special/历史版本分类，并避免把 combat、experimental 名称误改成正式版。
- 本工具采用“合并额外条目、沿用原版安装器”的机制。正常官方/BMCL 清单及其 SHA-1 要求保持原实现；补充请求独立限时，正常列表先显示，结果以两个折叠卡片追加，避免改变用户已经展开、聚焦的普通行。过期代次、安装期间、关闭期间和可选请求取消不会覆盖正常结果。这些异步交接、去重和身份门禁属于本工具适配。
- 补充数据来自公开 [unlisted-versions-of-minecraft / UVMC](https://zkitefly.github.io/unlisted-versions-of-minecraft/)，使用其 [version_manifest.json](https://zkitefly.github.io/unlisted-versions-of-minecraft/version_manifest.json)。另外阅读本地 PCL-CE `1a7653127d578efdb2a9388999a3fdc67d9e49d5` 的 `Modules/Minecraft/ModDownload.cs`：其官方/BMCL 列表会合并公开 UVMC 镜像清单，补充异常被记录并忽略；本阶段使用 UVMC 项目自身端点，没有移植其缓存和镜像实现，也不宣称恢复了 PCL 私有特供数据。
- 2026-10-22 逐个核对 12 个战斗测试版及 8 个实验快照的列表 ID、元数据 ID、客户端和资源索引摘要；明确映射战斗测试条目的 `1_14/15/16` 前缀至实际元数据的 `1.14/15/16`，其余下划线保持原样。不进行通用下划线替换，不扩展到未经核对的历史版本。普通清单优先、矛盾补充条目排除、补充元数据结构和实际文件的完整校验在本工具中独立实现。
- UVMC 当前没有为这些 JSON 提供清单 SHA-1。该阶段依赖固定公开来源与结构/身份门禁，不能将文件声明的 SHA-1 等同于上游签名或清单外的独立真实性证明；声明摘要时仍必须通过验证。真实端点检查只读取版本 JSON 和有摘要的资源索引，不下载游戏载荷或验证游戏启动。详细离线安装与界面证据见 `VALIDATION.md`。

## 0.57 收起期间的组件刷新

- 重读固定 MyCard.IsSwapped、StartHeightAnimation 的完成回调及 PageDownloadInstall 的选中/折叠路径。上游在切换时保持 SwapControl 可见，到收起完成才设为 Collapsed。本工具原 RefreshBody 在 Collapsed=True 时立即移除内容，即使旧高度动画仍在进行；现保留旧内容完成退出，保持既有高度曲线、目标和箭头时序。
- 组件响应立即接受新列表并按原规则重绑定逻辑选择；正在退出的旧正文只供显示，临时禁用操作且不再用新条目身份重写其选中标记。正文工厂仍延后到下次展开，连续刷新只保留最后一份；提前反向从当前高度构建最新正文。完成、隐藏或卸载后释放旧正文及其控件时钟，不在后台构建不可见替代内容。
- 临时禁用作用于本工具私有 ContentPresenter，释放时清除本地值并显式 CoerceValue，重新核对祖先禁用约束；不会通过恢复默认值绕过外层禁用。这一输入保护、正文工厂延迟和请求归属属于本工具适配，不宣称是上游的同一实现。
- 新增 27 项动画/集成检查，当前总计 322 项，经过真实 AcceptComponentList 与选择入口验证收起中刷新、最新响应、过期代次、反向、名称与选择保留；另验证重复工厂、鼠标/键盘合成输入、隐藏/卸载及祖先禁用。新增 13 帧 fold-refresh-frames，不作为桌面帧率或实际设备验收。

## 0.57 选中标记与文字颜色

- 读取固定 MyListItem.SetChecked 与 RectCheck 初始化：选中时将高度差的 40% 交给 200ms Fluent(Weak=2)，60% 交给 300ms 余弦 Back(Weak=2)，两条同时相加；目标高度为行 ActualHeight 减 12 DIP。透明度 30ms 到 1，Foreground 200ms 到 ColorBrush2（Height < 40 时为 ColorBrush3）。取消时高度在 120ms 内以二次方 EaseIn 到 0，透明度延迟 40ms、再用 70ms 到 0，文字在 120ms 内回到 ColorBrush1。
- PclListSelectionMotion 为高度、透明度和文字颜色单独拥有 WPF 时钟组，和悬停组并存；相同逻辑选择不重播。快速反向采集实际显示的高度、透明度和颜色，新时钟生效前的基值保持相同；过期完成回调无权修改新状态。文字与图标继续使用原 Foreground 绑定，颜色插值仍为本工具 WPF ColorAnimation 适配。
- 原版即时分支使用选中 Auto/Stretch、上下各 6 DIP；本工具在动画完成和生命周期清理时也恢复该布局，防止保留固定高度。活动动画中的行高变化从当前高度连续重定向；稳定行高变化只更新布局及紧凑行强调色，不创建空动画。非负高度钳位、动态重定向和完成清理属于本工具适配，不移植上游线程逐帧调度器。
- 页面输入屏蔽、隐藏、祖先禁用、卸载及主题替换选择稳定终态，保留 Selected；只读及未加载控件直接使用终态。标记零高度/零透明度承载取消状态，原 XAML、字体、图标和五列布局不变。
- 该轮新增 41 项动画检查，当时总计 295 项：独立公式/冻结副本、真实 WPF 高度和延迟透明度采样、500 次相同选择、与悬停并存、快速反选、尺寸重定向、过期时钟、生命周期和实际主题切换。另有 13 帧 selected-marker-frames；不作为桌面帧率或输入设备实测。

## 0.57 版本行与顶部选项的状态交接

- 重读固定 `5ef81e4` 的 MyListItem.RefreshColor / StateLast、MyRadioButton.SetChecked / RefreshColor，以及 ModAnimation.AniStart / AniTimer 的 IsAfter 分支。列表行只在视觉状态改变时重启悬停组；顶部选项重复设置相同 Checked 值不重播颜色动画。本工具同时跳过相同 Selected 值，实际选择变化仍更新标记和文字颜色，不重启独立悬停组。
- 列表进入采用 120ms 三次方透明度、192ms 三次方背景缩放；整行按下以 108ms 到 0.98，悬停松开以 144ms 回到 1。退出采用 180ms 线性透明度、180ms 三次方背景到 0.996、540ms 三次方整行回位，之后才在 1ms 内把不可见背景复位到 0.75。After=True 等待组内全部前序条目，不能只等背景的 180ms。WPF 使用 541ms 理想时间线承载该关系，不宣称复现上游逐帧调度的量化时刻。
- PclListHoverMotion 独立持有一个含透明度、背景缩放、整行缩放与颜色的 WPF 时钟组；X/Y 共享各自时钟，快速反向从显示值接续，完成释放全部属性时钟。相同视觉状态保留组和背景画刷，主题刷新或明确生命周期中断直接选择稳定终态。原 XAML、字体、选中标记布局及键盘焦点高亮保留；颜色仍使用本工具 WPF ColorAnimation 适配。
- 顶部选项及列表行在页面输入屏蔽、隐藏、祖先禁用和卸载时取消按压并收束动效，恢复后忽略旧松开；保留 Selected/Checked 的逻辑状态。只读关于行继续不参与命中和键盘焦点；显式卸载按不活动状态清理，不依赖下一次鼠标移动来纠正缓存。
- 该轮新增 40 项动画验收，当时总计 254 项，覆盖生命周期、500 次相同状态更新、实际 WPF 时钟曲线、快速反向、旧时钟和选中/只读状态；另输出 13 帧 row-hover-frames。采用合成路由及离屏渲染，未移动真实桌面指针，不作为设备或桌面 FPS 验收。

## 0.57 按钮按压与回弹

- 读取同一固定提交 `MyButton.xaml.vb` 的 Button_MouseDown/MouseUp/MouseLeave、`MyIconButton.xaml.vb` 的对应事件和 ModAnimation 的 AaScaleTransform、AniEasePower、AniEaseOutFluent、AniEaseOutBack。AaScaleTransform 的 After 默认 False；同组无延迟的条目是并行增量，不能按关键帧顺序串联。
- `PclPressAnimation` 将普通按钮的两条按下增量相加：80ms、ExtraStrong=5 次方到 0.955；700ms、Middle=3 次方再减 0.01，长按终值 0.945。松开采用 10ms 延迟加 300ms 三次方回到 1；移出采用 800ms、Strong=4 次方回位。
- 图标按下为 400ms、四次方到 0.8；松开同时叠加 250ms 的 `(1.05 - 当前缩放) × Back(Weak=2)` 与 `-0.05 × Fluent(Strong=4)`，总时长 250ms、终值 1，替换此前的 WPF BackEase 和 500ms 串行近似。移出为 250ms 三次方回位。曲线复用 PCL 的余弦 Back 公式；没有改动原 XAML、图标或色板。
- `PclPressMotion` 为每个按钮拥有一条共享 X/Y 的 WPF 时钟，替换时从实际显示值接续，并将新时钟生效前的基值设为同一值。返回动画完成只清理自己仍拥有的时钟；页面输入屏蔽、隐藏、祖先禁用或卸载时取消按压、释放缩放并恢复 1。这些生命周期保护为本工具适配，未移植上游的线程动画调度器。
- 该轮新增 56 项隔离检查，当时动画验收总计 214 项：独立公式核对六种曲线和冻结副本、实际 WPF 时钟采样、重按衔接、过期时钟、输入屏蔽及隐藏/卸载后的防误触。额外输出 13 帧 `press-motion-frames/`；输入为夹具合成路由事件，不作为真实鼠标/设备验收或桌面帧率证明。

## 0.57 页面与滚动动效

继续读取固定提交的 [MyPageRight.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Controls/MyPageRight.vb#L392-L460)、[MyScrollViewer.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Controls/MyScrollViewer.vb#L7-L32)、FormMain.PageChangeAnim（1503–1553 行）与 ModAnimation.AniEaseOutBack（674 行起）。与本地 PCL 当前提交核对，相关控件源码一致；不修改上游工作目录或既有 44 份固定快照。

- `PclSurfaceMotion` 采用 70ms 线性淡出/上移 6 DIP，110ms 后移除旧画面，再等待 30ms 入场。新卡片透明度 100ms、Weak 次方 2；初始 Y=-16，同时叠加 250ms 位移 +5（Fluent 次方 3）与 350ms 位移 +11（PCL 余弦 Back 曲线），每项错开 25ms。自定义 DoubleAnimationBase 保留相加曲线，未用 WPF BackEase 代替。
- 第二轮按同一冻结位图的互不重叠区域实现逐项退出：70ms 淡出/上移 6 DIP，项间错开 15ms；滚动条 90ms 从 X=0 移至 10（Fluent 次方 3、EaseIn），进入时 350ms 从 10 回到 0（次方 3、EaseOut）。与 FormMain 一致，旧层在 110ms 强制移除，延迟较大的项也不能拖长页面替换时间。快速反向和整份版本列表替换使用当前合成画面整体退出，避免按另一个页面的布局切碎旧帧。
- 逻辑页面立即切换，异步回调和键盘路由不归属旧截图；整页新内容在 140ms 前保持透明，避免工具条从退场间隙露出。入场与退场均限制视口内最多 16 项，不递归展开卡片或创建列表行。所有退出片段共享一张冻结位图，按向下取整保证四百万像素上限；掩膜包含卡片阴影并保留不随位移移动的祖先视口裁剪，防止卡片越过固定工具条。绝对 VisualBrush 视框补偿源控件布局偏移，避免标题栏高度重复应用。
- 滚动条变换附加于其原变换后；指针操作、模板卸载或隐藏会只回收该变换，保留卡片入场。窗口尺寸变化立即结束本层位图、根透明度、卡片与滚动条动效，防止旧尺寸画面残留。异常路径也回收已建立的动效；旧完成回调不结束新组。预设初态在附加时钟后完成，不使用立即定位时钟的方法，以免把截图准备耗时计入已经播放的时间。
- 入场位移追加在控件已有 RenderTransform 之后，结束时只回收本层时钟和变换；卡片高度、悬停与安装页横移各自持有时钟。程序恢复滚动及定位错误字段先于入场单元选择，键盘焦点延后到入场结束；失效时钟不能结束新切换。关闭系统客户端动画时退回即时模式。
- `PclScrollViewer` 对应 `PerformVerticalOffsetDelta(-e.Delta)`：300ms、Fluent 次方 6，以剩余增量叠加连续输入并逐帧截断边界。滚动条、按键、触摸、程序定位、视口高度变化与卸载取消本层滚动；内容范围变化保留余量并限制到新边界。下拉框、多行文本框、弹出项目和有自己滚动范围的子控件不接管。该逻辑使用独立可冻结的 WPF 时钟，未移植 PCL 线程动画循环。
- 本工具额外跟踪排队滚动命令的回执，区分动画自身位置变化和外部定位；替换滚轮时钟前先应用未完成的滚动布局，避免旧回执取消新输入。这是 WPF 适配的生命周期处理，不宣称上游使用相同的回执队列。
- 收尾继续读取同一固定提交 MyPageRight.PageOnEnter/PageOnContentExit/PageOnForceExit/PageLoaderState（167–279、347–386 行）与 ModAnimation.AniStart/AniStop（753 行起）：重复普通进入保持当前状态，明确内容替换停止旧组后进入新内容，强制退出清理命名动画。完整版本列表按接受结果显式重建入场，先截取当前合成画面，再结束安装页横移并替换控件；隐藏刷新只更新隐藏内容，组件卡片内刷新不重播整页。已有请求代次、取消和安装数据归属不改。
- 安装选项页保留 PageDownloadInstall 的 260ms 四属性时序，额外统一系统客户端动画开关及关闭/隐藏/缩放清理；滚动条鼠标、键盘、触控只结束所属横移，卡片入场继续。空版本提示作为 MyPageRight 支持的 TextBlock 入场。沿用现有文字加载提示，没有移植上游完整加载环及 400ms 等待/驻留状态机；系统设置接入与视觉生命周期保护属于本工具适配。
- `--motion-checks` 为独立 322 项隔离验收，真实运行和暂停/定位 WPF 时钟验证参数、输入归属、快速反向、焦点、模板与卸载；补充像素对照、掩膜覆盖、长列表尾部、祖先缩放、位图预算、异步代次、关闭和合成触控路由。关于页再核对延后焦点、用户新选择、原滚动位置、过期返回和提示框的实际命中；滚轮覆盖同帧合并、不同输入年龄、偏移连续性及展开中的范围变化，卡片覆盖内容替换、缩放换行、隐藏和真实组件响应。与既有 658 项界面检查分别记录。输出三段 90 帧切页、一段 30 帧异步衔接和 15 帧卡片高度重定向，不将离线帧序列当作桌面帧率证明。
- 再读固定 `MyScrollViewer.PerformVerticalOffsetDelta`、`ModAnimation.AniEaseOutFluent` 与 `MyCard.MySizeChanged/StartHeightAnimation/TriggerForceResize`。同年龄滚轮增量共用相同曲线，精确合并而不对不同年龄取整；私有偏移 DP 使用真实基值防止时钟替换瞬间回零，属于 WPF 适配修复。组件异步替换从当前显示高度重新测量并衔接，沿用 PCL 的高度距离分段与小于 1 DIP 的忽略规则；上游 TriggerForceResize 本身是立即更新，此处的连续重定向是本项目的衔接策略，不宣称该分支逐行相同。
- 固定 MyScrollViewer.ScrollChanged 会更新 RealOffset，未因 ExtentHeightChange 停止增量缓动。本项目相应保留卡片展开/收起中的滚轮输入，将待处理位置限制到新范围，并区分原生边界钳位、自身滚动回执与主动定位；视口高度改变仍结束缓动。宽度重排发生在高度动画中时，沿用上一条的连续重定向策略；仅在测得高度目标变化至少 1 DIP 时替换，目标未变或正在收起时不重启。上游 MyCard 本身在 IsHeightAnimating 时跳过尺寸事件，此处为本项目对重排的适配，未宣称逐行等同。

主参考仓库：`../PCL`（Meloong-Git/PCL）。固定基线：

`5ef81e490ae46b1100d61e33d0183094c7a80ca8`（2.14.0.0，下载限制前）。

开发时实际阅读了以下代码；以下对应关系说明实际实现依据，并非只参考界面截图。

## 0.57 关于页信息行与头像对齐

- 再次直接阅读固定 PageOtherAbout.xaml 的「关于」「特别鸣谢」两组布局，以及 MyListItem 的主副文本设置。首卡按原作者、协力开发者、项目版本三行组织，采用 45 DIP 行高、40/弹性/150 DIP 三列、34 DIP 圆形头像、35 DIP 按钮、21/40/21/16 DIP 内边距和 15 DIP 卡片间距；信息仍复用原 ListItem、Button 适配控件与内置字体，不修改共享控件的 XAML。
- 原作者赞助按钮显式放入独立操作列并使用主题强调色，修复旧作者卡按钮缺列号造成的文字重叠。补充 HerobrineXia 的上游协力开发者信息，将重复来源和项目定位收拢到「特别鸣谢」「项目与许可」，运行环境和第三方身份放在页尾。信息区剪裁与独立列约束防止长文案挤入按钮；两页主导航、返回设置滚动位置、任务保留及原链接白名单继续使用既有机制。
- 三张头像通过 git show 从固定 5ef81e4 Git 对象读取原始字节，不从本地检出文件或网络头像读取。资源清单从 12 项扩展为 15 项，导入器与验证器同步支持新资源；44 份固定源码及既有 12 项资源不变。项目 DB 标志与额外线条图标为本项目自己的适配，没有复制 PCL 产品标志来标识本项目。
- 隔离快照支持 about / about-dark / about-colorful；实验性彩色只在预览模式内开启，不写真实用户的主题设置。这些截图与几何检查用于局部对齐，未声称全窗口或不同 DPI 下与原版逐像素等价。

## 0.57 简化显示全称

- 当前显示全称改为 Plain Craft Launcher - Download Bridge，覆盖 ProductInfo、关于页及版本信息、App 的 Title / AssemblyTitle / Product 和当前项目说明。PCL-DB 简称、PclDownloadBridge 技术标识、兼容设置路径、下载缓存和安装锁保持；历史记录保留各轮当时的名称。
- 许可原文、固定 PCL 快照、资源、上游署名及独立第三方声明保持。

## 0.57 单 EXE 分发接入与来源清单

- 延续前阶段实际阅读的固定 PCL build.yml 直接分发 EXE、写入构建提交的思路；本轮不修改下载 Core、VB/WPF 界面和资源，不引入新的上游实现。普通构建与 VS F5 保持多文件，显式打包改用已通过隔离检查的单 EXE。
- 构建前后核对 Git 完整提交、分支、工作区改动状态与所有已跟踪/未忽略新文件的内容清单，忽略 artifacts、dist 等生成输出；EXE ProductVersion 核对实际提交。打包前再次核对来源与载荷快照，新源码或文档不能继续使用旧候选。候选报告保存逐文件来源，公开 build-manifest.json 只保存来源摘要及分发载荷摘要，不包含本机绝对路径。
- ZIP 采用单 EXE、七份文档、构建清单的九文件格式，另输出同字节 EXE 与清单；四种输出顺序替换，包含旧输出备份、文件锁、目录联接预检及逆序回滚。来源清单、候选校验和多输出事务为本项目自己的交付机制，不能将哈希清单等同于签名或断电原子性。原 11 文件历史包保持。

## 0.57 单 EXE 构建与隔离验证

- 实际读取固定 Application.xaml.vb 约 217–232 行及 My Project/Resources.resx：PCL 在最早构造阶段注册 AssemblyResolve，从资源字节加载依赖，并使用 ConcurrentDictionary / Lazy 缓存。固定 .github/workflows/build.yml 直接上传构建后的 EXE。沿用 .NET Framework 4.8 和资源嵌入思路，没有迁移运行时或引入第三方合并/封装工具。
- App 可选 PclDbSingleExecutable=true，从实际 ResolveReferences 结果中嵌入 Core 与 Newtonsoft.Json 两个 DLL，校验数量并在 WPF 临时项目重复收集时清理旧项。RuntimeBootstrap 在构造 WPF App 前注册加载器，通过 NoInlining 边界避免过早解析依赖；按明确名称白名单、请求版本、区域和公钥标记核对身份，未知及卫星程序集返回 Nothing，缓存兼容请求的加载结果。这些严格身份检查为本项目补全，不声称 PCL 原先逐项执行同一检查。
- 程序启动显式设置原 exe.config 的两个长路径开关；正常多文件构建仍保留原配置。七份随附文档作为资源嵌入，原外部文档优先；缺失时只按名称白名单在应用文档缓存生成已知内容，预览走独立临时目录。第三方说明的内嵌回退包含完整 LICENCE。沿用原关于页按钮和文档打开方式，不调整页面布局。
- 验证器独立构建，仅将 EXE 复制并改名到仓库外的中文/空格目录，在没有 DLL、配置或外部文档时执行 25 项检查及完整 658 项 WPF 检查。另比对两份内嵌 DLL 和七份文档的原始字节 SHA-256，验证真实长路径文件 IO、实际回环下载引擎、Core 的 Java 桥资源以及关于页文档回退；不执行真实游戏安装。工具保留报告和候选，不生成 ZIP 或更新正式分发目录。

## 0.57 Java 处理器输出收尾与取消

- 实际重读固定 ModDownloadLib.vb 的 ForgelikeInjector（约 940–1025 行）：进程退出后，stdout / stderr 分别通过 AutoResetEvent 最多等待 10 秒，再分析末尾日志。本项目 JavaRuntime.RunAsync 原先在 Exited 后调用无时限的 Process.WaitForExit，辅助进程继承管道时仍会卡住；取消回调只终止已拥有的主进程，无法解除这种输出等待。
- 改为分别记录两路 EOF，主进程退出后合计最多等待 5 秒，同时监听调用者取消。正常收尾后才读取退出状态和末尾日志；超时则包含主进程退出码和已收到的日志并明确报错，不发布组件实例。取消不等 EOF，也不终止其他进程；收尾时停用并解绑回调和异步读取，避免返回后继续写旧任务日志。
- 5 秒共同预算和超时错误是本项目适配，未照搬 PCL 的 10 秒逐流等待或 true 文本成功标志；没有添加处理器总执行时间限制。正常错误码和无换行末行仍保留，异常继承管道下尚未送达的输出可能被丢弃。
- 22 项检查使用中文/空格临时路径中的托管进程模拟 Java，覆盖双流及末行、退出码 7、预先取消、运行时取消、退出后收尾时取消、两种退出码的 EOF 超时，以及安装锁释放和同名重试复用。产品代码只结束拥有的主进程；样本辅助进程由测试确认 PID 和实际可执行文件路径后自行收尾。保留原有真实 Java 处理器回归，不用模拟结果声称已验收真实上游安装器或完整游戏。

## 0.57 安装器依赖的重复校验声明

- 实际重读固定 ModDownloadLib.vb 约 1139–1177 行：合并 install_profile.json / version.json，提取 Forge / NeoForge 依赖和 Mojang mappings，再交由下载器处理；ModNet.vb 约 1395–1400 行在启动时按规范化 LocalPath 去重。PCL 此处没有实现本项目的重复摘要合并，不声称两者完全等价。
- 本项目的常规下载器已有 DownloadPlan.Merge，但 ComponentInstaller.FetchAsync 先按路径取第一条，会使后续重复声明的 SHA1 在进入下载器前丢失；安装器依赖的逐条内嵌检查还会提前丢掉可复用项，使冲突和首选地址不能按完整声明处理。
- 可下载依赖（包括映射文件）改为先按实际目标位置合并全部已知校验，再过滤已验证的内嵌文件；组件批次统一复用同一 DownloadPlan.Merge，保留第一条地址和类别，大小或同类摘要冲突在该批传输及 Java 执行前停止。生成且没有下载地址的输出仍由处理器和最终启动库校验流程处理。此为本项目稳健性修复，不更改 PCL 原始快照、依赖版本选择、并行调度和界面样式。
- 24 项检查使用回环 HTTP、合成 Forge / NeoForge 安装器及真实 Java 样本，覆盖同大小错误依赖、完整实例拒绝发布、私有校验缓存与重复安装、大小/SHA1 冲突、内嵌匹配/修复及大小写/斜杠/点路径别名；未运行真实上游安装器。

## 0.57 NeoForge 安装器校验补全

- 再次通过固定 Git 对象读取 Pages/PageDownload/ModDownloadLib.vb 的 NeoForge 下载分支（约 899–904 行）：安装器沿用 NeoForge.UrlBase 的 neoforge / 旧 forge 坐标，候选为 BMCLAPI Maven 镜像及官方；此处 FileChecker 只设置 64 KiB 最小大小，没有读取 SHA1。本轮补取校验值属于本项目的稳健性增强，不将其描述为 PCL 已有的 NeoForge 校验机制。
- 原 Forge 校验解析器统一为 ForgelikeInstallerChecksum，Forge / NeoForge 共用同一套已有值检查、所选文件 sidecar 请求、文件选源偏好、取消与错误处理。NeoForge 列表仍不逐条请求哈希，1.20.1 原始 forge 包坐标保持；只在实际安装时补取校验信息，不回写组件列表对象。
- 17 项 NeoForge 检查复用真实 ComponentInstaller 与回环 HTTP，覆盖 SHA1/MD5、选源回退、下载门控、取消、错误声明、文件不匹配及旧 Maven 坐标；Forge 原 22 项保持。完整安装缓存样本另验证：已有私有安装器缓存不能绕过缺失校验信息，补取有效摘要后可校验复用，失败期间保留已验证的暂存客户端和依赖，成功后释放客户端缓存副本。界面样式与下载并行调度保持。

## 0.57 Forge 安装器校验信息

- 实际重读固定 ModDownload.vb 的 DlForgeVersionOfficialMain（约 704–728 行）及 DlForgeVersionBmclapiMain，以及 Pages/PageDownload/ModDownloadLib.vb 将 Forge.Hash 传入 FileChecker 的下载路径（约 911、1128 行）：PCL 官方 HTML 提取对应文件 MD5，BMCLAPI 记录 MD5/SHA1。项目现用的 Maven XML 列表不带哈希，因此新增所选文件的校验补全；SHA1/MD5 sidecar 获取是本项目对 Maven 入口的适配，不声称 PCL 采用相同请求路径。
- ForgeInstallerChecksum 优先验证并使用已有 SHA1/MD5；缺失时先读取所选安装器的 .sha1，来源均不可用再读取 .md5。SourcePolicy 按文件下载源偏好排列候选，复用有大小/超时限制的元数据读取和可取消竞速，在接收有效的十六进制内容后才接受胜者；不逐条扫描整个版本列表，也不修改界面持有的组件元数据。有效 SHA1 下的文件不匹配仍报错，不退回较弱校验或无校验下载。
- BMCLAPI Forge 列表在参与竞速时验证结构和安装器校验字段，损坏字段不能先赢后失败；合法空列表仍返回空。规范化字段外围空白是本项目适配。所选 Forge 的校验信息全部不可用时明确停止，沿用已有下载引擎完成文件校验、缓存及续传；NeoForge 流程保持现有行为。
- 22 项检查通过真实 ComponentInstaller 和回环 HTTP 验证字段复用、选源与回退、MD5 兼容、下载门控、取消、不匹配文件和元数据竞速；原有旧 Forge 样本补充其合成安装器的 SHA1 sidecar。页面样式与已有界面检查保持。

## 0.56 Forge 文件坐标与刷新

- 实际重读固定 ModDownload.vb 的 DlForgeVersionEntry（约 587–640 行）、官方 HTML 分支提取（约 697 行）与 BMCLAPI 文件类型/分支读取（约 743–787 行），并通过固定 Git 对象再次核对构造器。PCL 将 VersionName 与 FileVersion 分开，强制修正 11.15.1.2318、11.15.1.1902、11.15.1.1890 的 1.8.9 分支，并为缺少分支的 1.7.10 revision >=1300 补后缀。
- 新增 ForgeRelease，官方 Maven XML 与 BMCLAPI 都通过同一映射生成纯数字显示版本和完整文件坐标；版本排序、OptiFine 要求与自动命名只用显示版本，URL 保留分支。官方仍沿用本项目的 Maven XML 入口，不声称移植了 PCL 的完整 HTML/推荐版本解析；可用 SHA1/MD5 元数据沿用现有校验流程。
- 按修正后的文件坐标去重，过滤无效数字和不能用于单段 Maven 路径的分支；空白分支视为缺失是本项目适配。Forge 刷新同时匹配版本号和规范化安装器 URL，分支不在新列表时保留选择并阻止安装；这一刷新保护为本项目稳健性补全。仍只列出 installer.jar，不将 universal/client ZIP 加入当前安装流程。
- 18 项核心用例通过实际 Catalog、下载引擎和旧版安装器解包路径，6 项实际 WPF 用例覆盖命名、OptiFine 选择及刷新、缺失/恢复和清除。全部使用合成文件与回环端点；字体、控件模板和卡片几何保持。

## 0.55 加载器的版本边界

- 实际通过固定 Git 对象重读 `PCLCS/Minecraft/MCUtils.cs` 的 CompareVersion，以及 `McVersion.cs` 的 NameToVersion / VersionToDrop；对照 PageDownloadInstall 中 VanillaDrop、Fabric/LiteLoader 显示条件及 OptiFine 的 Forge、Fabric 兼容区间。原 CompareMc 遇到预发布后缀退回字符串排序，导致 1.9-pre1、1.20.4-pre1 等边界误判。
- 组件比较复用项目已有、按固定 MCUtils 移植的比较器，保留 pre、rc、snapshot、experimental 权重与数字后缀排序；不复制第二份排序实现。原有 Nothing 比较作为本项目兼容处理保留。卡片使用按 NameToVersion / VersionToDrop 提取的代际值，1.13 的预发布不误入两边的卡片，愚人节名映射到 PCL 指定的正式代际；保留本项目对 old_ 远古版本的独立保护与超范围整数回退。
- 代际判断集中于 Core 的 MinecraftVersionRules，页面及 OptiFabric 的 1.16 起点共用；起点按代际、1.20.4 上限按完整版本比较，避免修正排序后误排除 1.16-pre1。新增 21 项核心检查覆盖实际 Validate、OptiFineFits 与回环 ComponentCatalog 请求；18 项实际 WPF 检查覆盖卡片矩阵、真实行点击、OptiFabric 自动选择及清除，不触发真实下载。控件模板、字体、图标和几何保持，本轮不修改来源调度或安装器处理器。

## 0.54 Forge 卡片的版本格式

- 实际通过固定 Git 对象读取 `PCLCS/Minecraft/McVersion.cs` 的 IsStandardFormat，并重读 `Pages/PageDownload/PageDownloadInstall.xaml.vb` 的 Forge 加载入口及卡片显示条件（约 79、224 行）。原本仅接受完整 `1.x` 数字的正则会隐藏新编号和预发布版的 Forge 卡片，即使列表已有适用项。
- 按 PCL 的前缀匹配实现：`1.` 后接数字，或 `[2-9]` 开头的两位主段及小数部分按 InvariantCulture 解析后不少于 26。保留上游对新编号快照、预发布后缀的接受方式，周快照、愚人节特殊名和远古编号继续排除；不改版本浏览器中仅针对 `1.` 的发布分类修正规则。
- 新增 14 项实际 WPF 卡片回归，核对版本矩阵、已有目录项的选择/自动名称及空列表状态；前后离屏渲染使用合成 Forge 元数据。沿用卡片、字体与原图标，不更改控件尺寸、传输调度或安装器实现，也不以合成行证明真实安装器存在。

## 0.53 NeoForge 版本映射与分类

- 实际重读固定 `ModDownload.vb` 的 DlNeoForgeListEntry（约 814–878 行）和 GetNeoForgeEntries（约 938–948 行），并通过 Git 固定对象读取 `PCLCS/Minecraft/MCUtils.cs` 的 CompareVersion。原实现仅拼接 `1.` 与前两段，不能对应 1.21、26.1、26.1.2、26.1-snapshot-1 和 25w14craftmine 等上游已处理的名称。
- 按上游结构分开 API 原始名、显示名、游戏版本与数值排序版本：旧版保留 1.20.1- 前缀的下载坐标；0. 开头使用嵌入游戏名；数值主段 >=24 使用新游戏编号，去掉末尾 .0 并保留 + 后的快照标记。alpha/beta 使用不区分大小写的分类，排序保留数值版本后比较版本名的顺序。API 坐标用于 URL，显示名不参与 Maven 路径重建。
- 沿用官方/BMCLAPI 现有选源与元数据验证流程，并保留 PCL 按原始 API 名排除 47.1.82 的规则，不把显示名归一化用作黑名单键。版本名字符约束、畸形数字过滤及不含 alpha/beta 的显式 snapshot 标记为测试版，是本项目的独立稳健性补全。14 项回环用例验证实际 Catalog，不从测试代码直接调用解析助手；异步代次保护和页面控件样式保持。

## 0.52 PCL 自定义游戏目录

- 实际重读固定 `Modules/Minecraft/ModMinecraft.vb` 的 SetMcFolder（17–23 行）与 McFolderListLoadSub（58–127 行）：路径保持尾分隔符；自定义目录匹配到已扫描路径后，直接更新目录名称并标记其来源，未匹配才新增；无效自定义项单独处理。PclFolders 原先对重复路径只去重，导致用户别名丢失，本轮补齐相同路径的名称/来源更新。
- 本项目继续保持读取原 PCL 设置与注册表，不采用上游同步重写 LaunchFolders 或创建默认 .minecraft 的行为。当前选择无效、文件占用和空列表的恢复、14 项临时目录回归及保留盘符根路径是本项目的独立适配，不声称原版已有同样错误处理。
- 盘符根目录原先在规范化时被截成 `C:`，Windows 会按该驱动器当前工作目录解释；现在保留 `C:\`。选择路径在比较前规范化，失败只记警告，不中断其他可用目录扫描。不改既有游戏内容、下载调度或控件样式。

## 0.51 普通按钮与图标按钮状态

- 实际重读固定 `Controls/MyButton.xaml.vb` 的 RefreshColor、ColorType setter、Button_MouseEnter/Leave：正常/高亮分别取 ColorBrush1/2，悬停取 ColorBrush3 描边和 ColorBrush7 背景，离开返回 HalfWhite；进入 100ms、离开 200ms，禁用取 Gray4。PclButton 原先只改描边，禁用设整体 0.45 透明度，Highlight setter 直接覆盖当前状态；本轮按这些上游规则修正。键盘焦点沿用本项目既有高亮提示，并使用相同活动底色。
- 实际重读固定 `Controls/MyIconButton.xaml.vb` 的黑色主题、120/150ms 颜色及离开取消按压行为。明亮模式继续保留黑色 160/230 alpha 和原 XAML。深色中性色、主题刷新订阅及禁用/卸载时取消待点击属于本项目的独立补全，不声称上游已有相同深色模式实现。
- 14 项独立控件检查使用同一组用例对比解压的原 0.50 与新控件；固定 44 份快照、12 项资源及四种原控件 XAML 不改。本轮不修改文件选源、并行传输和游戏安装流程。

## 0.50 外观保存与改名复核

- 沿用固定 `PageSetupUI.xaml`、`ModMain.vb` 对应的主题和不透明度表现；本轮不改配色或页面几何。多窗口的字段差异合并属于本项目独立补全，不声称来自 PCL。
- 原外观自动保存写入整组已知值，导致只改不透明度也撤销另一窗口的新主题/深色模式。改为使用既有偏好基线，只合并本窗口实际修改的字段；失败不推进基线，显式恢复默认仍完整重置已知外观值。保留未知字段及下载偏好。
- 复核 PclDownloadBridge 的 VB RootNamespace、启动对象、程序集、WPF 资源 URI、VS 调试入口和导入/打包路径。PlainCraftDownloader、VanillaDownloader 的旧设置路径继续只作兼容读取。

## 0.49 PCL-DB 名称与外观设置

- 仓库与根目录更名为 PCL-DB，解决方案、程序集、命名空间和资源 URI 统一使用 PclDownloadBridge；显示全名为 Plain Craft Launcher (PCL) · Download Bridge（第三方下载补全版）。原固定 PCL 提交、44 份源码参考和 12 项资源保持。
- 蓝色默认参数继续使用 PCL 的 HSL2 公式；浅蓝、深蓝和深色界面为本项目适配。深色模式同步卡片、输入、下拉菜单与中性色，保留蓝色强调色；原按钮 XAML 和控件几何保持，通过运行时资源更新表面颜色。
- 开发者设置独立成卡片，常规部分提供下载诊断，实验性外观入口验证成功后仍使用同一个个性化列表。代码只含随机验证码的 SHA-256 摘要；输入及解锁状态不落盘，锁定、关闭开发者模式和重启后回到公开蓝色选择。这个功能分组不构成上游额外授权或安全隔离。
- 旧 PCD 偏好优先于最早的 VanillaDownloader 偏好，迁移保持原字节及未知字段；开发者偏好只保存本窗口修改的字段。游戏缓存、续传及安装锁的 .vanilla-downloader 路径继续作为兼容格式。关于页首位增加原作者署名与原赞助链接，来源参照固定 PageOtherAbout.xaml。
- 新增 36 项开发者、主题隔离、迁移、布局及保存冲突检查，包含于完整界面及外观检查。常规功能与额外配色的入口位置按当前项目需求适配，不声称完整复制上游设置页。

## 0.48 Java 版本探测

- 实际阅读固定提交的 `PCLCS/Java.cs`，其中 `CheckAsync`（约 34–55 行）通过有时限的程序执行获取 Java 信息，并兼容带引号版本行和 `openjdk <版本>` 格式。PCD 保持 VB.NET 实现；只补齐安装处理器所需的主版本探测，不引入完整启动器的架构筛选、Java 下载、属性列表或版本排序。
- 原 PCD 先等待进程退出，再读取重定向输出，真实 JVM 的长环境提示会堵住管道；同时没有检查退出码。现在 stdout/stderr 同时读取，进程等待和输出排空共用 5 秒预算，超时仅终止本次启动的探测进程，终止收尾另限 1 秒。后代持有管道时也不会无限等待。
- 每路流只保留最多 4096 字符的当前行，丢弃超长诊断行但继续排空；版本行在换行或 EOF 后解析，避免跨读取块时把 21 误读成 2。按 stderr 优先、stdout 备用取版本，检查退出码为 0；版本匹配定位在 Java/OpenJDK 行首，避免 JVM 环境选项回显中的版本文字干扰。
- 新增 20 项检查：1 项使用真实 JVM 的 16 KiB 环境提示，19 项使用隔离的模拟探测子进程验证格式、大输出、错误码、最低版本、超时及继承管道。该实现和缓冲上限为本项目自己的适配；44 份固定参考与资源保持。

## 0.47 Java 处理器输出与日志失败

- 重新阅读固定 `ModDownloadLib.vb` 的 Forge/NeoForge 安装输出回调（约 959–1007 行）：stdout/stderr 的接收有异常隔离，并保留末尾输出用于安装失败报告。PCD 原 `ProcessorLine` 将日志写入放在 UI 通知之前，仅外层回调拦截 IOException；只读日志导致的 UnauthorizedAccessException 会从异步回调逃逸，日志占用则会跳过实时输出。
- 将预期的 IO/访问异常限制在 `ProcessorLine` 的日志写入处处理，保持原日志与权限；UI 节流和输出通知仍执行。Java 退出码、末尾输出、取消处理、输出校验和实例发布条件沿用原实现；此处的具体日志恢复规则为本项目适配，没有复制上游全部异常处理或变更参考快照。
- 新增 12 项本地实际 Java 回归，覆盖日志只读/占用、成功安装与进度、解除限制后的追加、退出码 7 与 stderr、失败/取消不发布，以及同名重试。失败前的只读样本进程异常退出，日志占用样本则在实时输出断言失败；未用真实游戏文件。

## 0.46 元数据响应与备用来源

- 重新阅读固定 ModNet.SendRequest（约 256–274 行）：PCL 使用 ResponseHeadersRead，将响应头与正文读取分开处理；另对照文件下载 Thread（约 920–960 行）在正文前检查状态与长度的处理。本工具原 GetAsync 使用默认完整缓冲，直到正文结束后才检查 HTTP 状态及 32 MiB 限制；这会使已知不可用的首选响应拖延备用来源。
- GetTextAsync 与 JSON/数组/HTML 列表竞速统一使用 ReadMetadataAsync：先检查状态和声明大小，再按 16 KiB 缓冲读取，正文上限为 32 MiB；未知长度响应读取到超限的下一个字节即拒绝。大小限制为本工具自己的既有保护，不声称来自上游相同规则。
- 保持原始字节进行 SHA-1 校验，受限缓冲通过 ByteArrayContent 保留文本的 Content-Type 字符集与 BOM 语义；45 秒时限覆盖响应头及整个正文，取消会关闭响应并统一为调用者取消状态。此处没有改会话自动选源判定、延迟优先级、文件调度或续传格式。
- 新增 10 项回归：首选 HTTP 失败及声明过大但正文不结束时启动备用源、未知长度超限、边界大小、文本解码、无长度的完整 JSON，以及正文等待中的直接/竞速取消。VS 2026 增加 Metadata Transfers 启动配置；44 份固定参考不改。

## 固定来源导入工具的修复

- 原工具的 VB/XAML 使用固定 Git 提交，但图片、字体及纹理直接复制相邻 PCL 工作树；隔离复现证明工作树中的测试资源会被复制进应用，而源码清单仍标记固定提交。原工具还会无条件重写 7 份适配内容，隔离副本中的本地控件注释被覆盖。
- 导入器现以二进制流读取固定提交的 57 个 Git 对象：44 份源码按已有规则转换 UTF-8/LF，12 项资源与许可原文保持原始字节。两个清单按本轮实际读取的内容生成；已有清单经完整成员和哈希核对后原样保留，不把本地额外文件混入固定名单。
- 66 个候选包括上述 57 个文件、2 份清单及 7 份控件/阴影/配色/图标模板。已有适配文件保留，模板只用于补充缺失项，不能把导入检查通过当作界面完全等价。写入前先完成所有对象读取、候选生成和冲突检查；可只检查，或明确要求替换固定文件并备份。
- 后续写入会再次核对目标哈希；普通写入失败时逆序尝试恢复本次已写内容，报告保存失败也进入同一回滚。输出锁协调运行该脚本的进程，文件夹链接会在写入前拒绝；这不是针对外部编辑器竞争或进程强制终止的多文件原子事务。
- 本轮导入工具与回归逻辑由本项目独立实现；固定提交、44 份参考、12 项资源、7 份现有适配和应用下载实现保持。

## Git 提交后构建交付的补全

- 本轮只调整构建与打包工具，PCL 对照、应用和下载 Core 的业务实现、44 份固定快照与 12 项资源保持。源码快照的 44 项 SHA-256 校验现在也由默认界面对照脚本执行，不再只核对被读取的部分片段。
- 固定清单打包、候选 ZIP 与输出目录的逐文件校验、旧输出备份及失败回滚均为本项目自己的交付机制，不来自 PCL 下载器，也不表示上游授权事项已经解决。
- 上游导入工具属于维护入口，仍需要相邻 PCL 仓库；普通构建、打包和独立候选包检查只使用本仓库的固定参考、资源及清单。

## 0.45 更名后配置保存的冲突修复

- 使用 0.44 基线复查迁移后的保存路径，复现普通 BuildSettings 全量覆盖丢弃未知顶层/嵌套外观字段，以及启动后配置损坏仍被自动写入覆盖。`settings-conflicts-before-0.45/` 使用原保存方法提取的实际辅助函数执行临时文件写入；三个关键布尔值为 false，旧文件仍保持。
- 下载设置与外观保存统一使用 SaveChanges：在目标文件的跨进程命名互斥内重新读取最新配置、深度合并已知值，再使用已有原子替换。对象保留未知键，数组整体替换，显式 null 仍生效；不修改调用方 JObject。首次迁移也参与同一互斥，避免新建配置与正常保存相撞。
- 追加窗口基线后，普通自动保存只提交本窗口实际改过的已知字段；外观保存成功时同步该基线，未改内容不重写文件。主动保存仍提交用户已检查的完整已知设置。另覆盖启动时非法速度被修正后的差异，避免将修正误判为未修改。
- 自动保存确实需要写入而无法读取/解析当前设置时直接报错并保留原文件；主动点击保存设置才允许替换损坏 JSON。源代码中的参数 replaceMalformed 仅由这一主动入口传入 true，外观和目录/关闭窗口的自动入口均为 false。
- 互斥按规范化 Windows 路径区分，大小写/点路径别名共享同一互斥，不同配置互不阻塞；等待最多 2 秒，超时保留原文件，所有权异常退出后允许下一保存者恢复。此协调覆盖新版 PCD 流程；0.44 旧程序自身没有参与该互斥，不能据此声称拦住旧进程的全部覆盖。
- PCL 参考界面与下载 Core 本轮没有变化：固定参考仍为 44 份，下载/安装结果格式、缓存及安装锁保持 0.44。此处配置兼容和并发逻辑为本工具自身实现。

## 0.44 项目更名与兼容路径

- 正式采用 PCD / Plain Craft Downloader，当前源码路径与项目/程序集/命名空间由 VanillaDownloader 统一改为 PlainCraftDownloader。PCL 参考仓库与类型名称继续按原作者代码引用；原固定提交和 44 份快照不改。
- 本轮下载 Core 只更新项目名称、RootNamespace 和 HTTP User-Agent 标识。缓存、续传、暂存、处理器日志及安装锁的磁盘格式继续使用 `.vanilla-downloader`，避免旧版本与新版本并行安装时失去同一把锁。最终 Minecraft 版本文件结构沿用原实现。
- 偏好设置独立迁移到 PlainCraftDownloader 用户目录：先读取已存在的新设置，仅在新设置缺失时验证旧设置并以临时文件加不覆盖的 Move 发布。已有/并发产生的新设置优先；旧配置与未知字段保留，迁移受阻仍可使用验证过的旧配置。此逻辑为本工具自己的升级兼容，不来自 PCL。
- 下方各轮记录描述其当时版本。历史包、旧验证证据和 `.vs` 用户状态保留旧名称；当前代码依据中的源码路径已对应新项目位置。

## 0.43 版本列表与安装选项的转场

- 固定 `PageDownloadInstall.xaml.vb` 的 EnterSelectPage / ExitSelectPage（约 77–146 行）及 XAML 中 PanSelect 的初始 X=40：旧页透明度从第 10ms 起用 70ms 归零，横移从第 10ms 起用 90ms 到 -50 DIP（进入安装）或 +50 DIP（返回版本）。新页透明度从第 100ms 起用 70ms 到 1，横移以 160ms ExtraStrong=5 缓动回到 0，整段 260ms。进入安装时在 100ms 隐藏旧列表；返回时在 260ms 隐藏安装页。
- 本工具的 PclPageSwitch 在同一内容视口内控制四条 WPF 时钟，顶部目录与导航保持原位且不重复淡入；切换期间禁用视口命中测试并推迟键盘回焦。中途反向从实际透明度/位置继续，重复同页刷新不重播。被替换时钟的事件必须仍持有控制权才能更改页面；转设置、进度或卸载时归位并释放动画。
- `navigation-oracle-0.43.vb` 原样摘取原 Enter/Exit 中的两个 AniStart 表达式及原 AniEaseOutFluent 方法体，外围为记录桩，不执行原页面业务闭包。实际 Release App 的 8 组计划、832 个属性采样一致，包含延迟边界、冻结副本和中途反向起点。页面可见性、输入门控及任务保留由 31 项真实 WPF 离屏检查补充，已纳入完整界面检查。
- 操作差异明确保留：原 PCL 返回会 ClearSelected 并滚动共用 PanBack，本工具沿用既有行为，返回保留已选基版/组件/自定义名称和版本列表位置；选择另一个 MC 版本才初始化组件并将安装视口回顶。组件列表异步载入也保留本工具的代次检查，不移植原页面的业务闭包。进度页为独立下载任务的既有页面，进出直接恢复布局，避免动画改变运行中的任务。
- 本轮未改下载 Core、卡片/摘要动画或 44 份固定参考快照。任务布局、完整排程器、Java 自动下载及跨 DPI 精确视觉比对仍不等同于 PCL；此前各轮对照表按其版本阅读。

## 0.42 组件摘要显隐与操作保留

- 固定 `PageDownloadInstall.xaml.vb` 的 `SetPanelVisibility`（约 374–394 行）：相同目标状态不重播；隐藏时平移到 Y=6、透明度到 0，二者均为 60ms 线性；显示时透明度 60ms 到 1，平移以默认 AniEaseOutFluent 在 150ms 内回到 0。默认 Power 为 Middle=3，依据同一提交 ModAnimation，而非沿用卡片高度动画的五次方。
- 固定安装页 XAML 中的 PanForgeInfo/PanFabricInfo 等只包含版本图标与文字，Btn*Clear 是独立兄弟控件。本轮将本工具的摘要图标/文字封装进 PclComponentSummary，清除按钮保留外层；30 DIP 大小、32 DIP 右侧间隔及文本省略范围保持。
- 从当前 Opacity/Y 创建两条并行 WPF 动画；重复刷新只更新文字/颜色，不重新启动相同目标的动画。替换和卸载后只释放自己拥有的时钟；未加载控件直接设置最终状态。隐藏摘要禁用自己的命中测试，独立清除按钮不受影响，这是独立 WPF 适配的补充。
- `artifacts/pcl-install-source-0.42.vb` 是固定 Git 内容的原始文件。独立 summary-oracle 摘取原 SetPanelVisibility、AniEaseOutFluent 方法体原样编译，以记录桩替代 AaOpacity/AaTranslateY/AniStart；8 组计划、288 个属性采样与实际 Release App 一致，包含中途反向起点及冻结副本。固定界面参考仍为 44 份。
- 新增 28 项本地检查，纳入 Card Checks 和完整 UI 检查：信息层结构、线性/三次方参数、实际时钟、清除按钮位置、刷新不重播、反向/卸载、名称/依赖与已访问行保留。下载 Core 和安装数据格式不变。

0.41 对照表中的摘要显隐差异已在本轮处理。整页转场仍以淡入为主；任务页布局、完整下载排程及 Java 自动下载仍按该表保留独立实现，不据此宣称完整复刻 PCL。

## 0.41 卡片动画与安装页操作对照

- 固定 `Controls/MyCard.vb` 的 `StartHeightAnimation`（179–225 行）及 `IsSwapped`（246 行起）：距离不超过 800 DIP 时为 150ms ExtraStrong 缓动；中程采用匀速和减速两段，超长展开按 300ms 匀速段与 400ms 末段处理，长距离收回为 100ms 与 150ms。保留整数时间/距离的 VB 舍入、初速度不足时的线性回退，没有按注释重新设计数学曲线。
- 从同一 Git 提交读取 `Modules/Base/ModAnimation.vb` 的 `AniEasePower`、`AniEaseOutFluent`、`AniEaseOutFluentWithInitial`：ExtraStrong=5，带初速度曲线为 `(alpha+1)*p/(1+alpha*p)`，不是通用 PowerEase 代替。原样完整内容另存 `artifacts/pcl-animation-source-0.41.vb`、`pcl-card-source-0.41.vb`；44 份界面参考快照保持不变。
- 新增 PclCardMotion，以 WPF 关键帧承载同样的时长和曲线；箭头使用 250ms 五次方缓动。旧高度时钟反向替换或卸载后释放，完成回调只处理自己仍拥有的时钟。固定高度、未播放动画的卡片不受卸载清理影响。这些生命周期适配是本工具实现，未移植完整 PCL 动画排程器。
- 固定 `PageDownloadInstall.xaml.vb` 的 `PrepareSelectPage`（约 48–62 行）显式 `PanBack.ScrollToHome` 并收起组件卡片。本工具在 SelectVersion 中只对新选版本重置 InstallScroll；设置往返、来源刷新和组件选择仍保留当前方案及浏览位置。
- `artifacts/card-oracle-0.41.vb` 将上述原始方法体和两类缓动函数摘取后原样编译，仅给 AaHeight/AaCode/AniStart、Clamp 和窗体状态提供记录适配；与实际 Release App 逐样本比较 17 组计划、332 个高度采样（含冻结副本）、11 个箭头采样，通过。检查的是时长与数值等价，不是原动画引擎的帧排程或跨 DPI 视觉等价证明。

| 核对项目 | 本轮状态与仍有的差异 |
|---|---|
| 短/长列表折叠与箭头曲线 | 时长、分段、舍入和缓动已对照源码；实际帧调度由 WPF 承担 |
| 新版本进入、设置返回、组件选中/清除 | 新版本视口回到顶部；设置返回保留位置，选中后折叠、依赖与自定义名称保持 |
| 卡片摘要显隐 | PCL SetPanelVisibility 使用淡入淡出和 6 DIP 位移；本工具目前仍直接改变摘要可见性 |
| 整页转场 | PCL 安装选择页组合位移与分时淡入淡出；本工具仍以 160ms 淡入为主 |
| 任务统计布局与页面范围 | PCL 独立任务侧栏与右侧列表；本工具保留下载页内横向统计及传输详情，继续保持下载/设置两页范围 |
| 下载排程与 Java | 保留自己的共享连接/速度预算及安装器执行层，没有完整移植 PCL NetFile 排程器和自动 Java 下载；此次 Core 未改 |

## 0.40 主题与任务状态验证

- 重新核对固定 `Modules/ModSecret.vb` 的 `ThemeRefresh` / `ThemeRefreshMain`：替换应用 ColorBrush/ColorObject 资源，并更新标题/背景；本轮没有修改这一主题实现，也没有修改下载 Core。固定参考仍为 44 份。
- 原单独检查在任务页从未加载时读取隐藏动态资源，会看见缓存的蓝色；显示任务页后便是正确绿色。修正的是独立 WPF 检查的加载前提，不把它描述为原版 PCL 缺陷或实际下载进度重置。
- 新增 45 项任务主题状态检查及单独的 Appearance Checks 入口，覆盖显示/隐藏、成功、失败、取消时的颜色、进度、结果、说明和原控件身份。检查结果保留结构化失败现场是本工具调试能力，不来自 PCL 源码。

## 0.39 重复库选择与版本比较

- 重新阅读固定 `5ef81e4` 的 `McLibListGetWithJson`（约 1653–1762 行）：先检查 rules，普通/原生分别归类，再用实际文件父目录名比较版本，同版本使用后来的声明；同条件组件合并也按这个顺序选取，而不是无条件后者覆盖。
- 通过该提交的 Git 内容读取 `PCLCS/Minecraft/MCUtils.cs`，原样证据另存 `artifacts/pcl-version-source-0.39.cs`。版本比较按数字/字母分词、rc/pre/snapshot/experimental 次序、缺项 0、Int32 解析及末尾 ordinal 比较独立实现；这不是改成 SemVer，也没有把单纯字符串排序当数值比较。固定界面参考仍为 44 份。
- 新增 PclLibrarySelection：基础下载计划在过滤规则后选择同类库的较新保存路径，组件合并在相同规则组内做同样判断，保留原槽位；不同条件的元数据继续保留，由当前平台规划时选取。不适用的其他平台元数据不解析其文件。
- 将固定提交的原始 MCUtils.cs 原样交给 VS Roslyn 编译，仅为它的 Lower/RegexSearch 外部依赖提供适配；对 16 个版本的 256 组配对，与本轮 Release Core 的比较结果逐对核对，通过。输入使用小写版本，包含中文快照/预览版、未知版本及溢出数字；没有据此宣称所有语言区域和输入均已穷举。证据为 `artifacts/pcl-version-oracle-0.39.json`。
- 保留本工具原有的 classifier/extension 维度保护；本轮对齐常规 Maven 重复版本，不声称完整照搬 PCL 对混合扩展名条目的所有去重边界。对同一规范化目标，继续交由 DownloadPlan 合并大小/SHA-1/MD5、拒绝矛盾声明，避免选择版本时丢掉更强校验。这是本工具已有完整性保护的延续。
- 显式 null 的 rules 按未声明处理；原始元数据中的 null natives 也可进入普通 Maven 回退，遵循 PCL 清除顶层 null 的读取含义，不修改源对象。
- 新增 32 项检查，覆盖顺序、数字与预发布标签、溢出分词、保存目录与坐标版本不同、规则、普通/原生独立选择、同目标完整校验及实际模拟 Fabric 安装。VS 2026 新增 Library Selection 配置，原仓库保持。

## 0.38 支持库的路径与仓库回退

- 重新阅读固定 `5ef81e4` 的 `ModMinecraft.McLibListGetWithJson`（约 1653–1727 行）：普通 artifact 缺少 path 时用 McLibGet，Windows native 缺少 path 时从坐标生成包含 NativeName 的文件名；下载 URL 缺少时使用库声明生成的 RootUrl。原实现直接把缺失 path 拼为 `libraries/`，且未补下载 URL，导致计划冲突或不可用。
- PlainCraftDownloader 独立增加 `LibraryFile`，供普通 artifact、Windows classifier 及原有 Maven 回退共用。缺失/null 的 path 由既有 MavenPath 生成；明确的 path、URL 和 SHA-1 保留。明确空白 path 属于错误声明，在生成计划时拒绝，不静默猜测。
- URL 缺失/null 时，将库的 url 视为仓库根地址，未声明则使用本工具既有的 libraries.minecraft.net 默认值。仓库内的地址按坐标生成，与显式本地保存路径分别处理。对于原生库，本工具补上 classifier；原 PCL 代码的 RootUrl 在分支前由基础坐标生成，这里是独立完善，不声称两者对缺失原生 URL 的每个细节完全相同。
- 普通转换函数对显式 null 的大小使用 -1 表示未知，保留 SHA-1 后沿原完整内容校验流程处理。常规坐标的配置保持；计划转换不修改输入 JSON。
- 最后核对 `McLibGet`（约 1904–1913 行）发现它只拼接四段坐标及 `.jar`，不解析本工具既有的 `@extension` 格式。单纯下载到正确路径仍不足以让 PCL 定位。对这些坐标已有的 artifact/classifier 声明，独立增加 PreparePclLibraryPaths：在克隆的最终配置中补齐 path、url 和缺失的 size，Windows classifier 同时展开架构键；PCL 可走已有的显式 path 分支。它不改源 JSON，原版补全比较也使用同样规范化，因此不会把本工具生成的路径判为用户修改。
- 新增 38 项检查，覆盖路径/URL 缺失与 null、显式值优先、classifier/扩展名、未知大小、空路径，以及实际原版/模拟 Fabric 安装、两类库并存、第二实例复用、扩展名配置、补全和旧 JSON/PCL 设置保护。VS 2026 新增 Library Metadata 配置；44 份参考与原仓库保持。

## 0.37 本地复制回退与取消边界

- 重新阅读固定 `5ef81e4` 的 `ModNet.LoaderDownload.CheckExistingFiles`（约 1485–1506 行）：找到候选后复制，复制失败跳到下载路径。PlainCraftDownloader 原本只忽略候选检查的 IO 错误，实际 `File.Copy` 或副本校验异常会终止整个任务；本轮补齐这一回退。
- 独立暂存实现只捕获可选复制及副本校验的 IOException/UnauthorizedAccessException，继续候选循环；没有可用副本时沿既有网络选源下载。目标 `SafeFiles.CommitFile` 位于该捕获块之外，因此真实目标替换错误不会被当作可选源故障。
- 在源文件检查返回后、复制返回后，以及临时副本校验返回后检查 token；OperationCanceledException 不进入 IO 回退。沿用最终发布前检查的协作式边界，仍使用系统同步 File.Copy，复制调用本身不支持中途打断，不声称即时停止大文件系统复制。
- 私有 `.reuse-<GUID>` 文件在 Finally 尽力删除，清理时的 IO/访问异常不再覆盖取消或原始错误；受阻残片可能保留，不强制解除占用。源目录保持只读，未调整其文件属性或辅助数据流。
- 27 项新增检查运行实际 DownloadEngine。NTFS 辅助流占用复现真实 File.Copy 失败；受控、测试进程内的 CryptoConfig SHA-1 提供程序在真实哈希结束时取消，确定覆盖源检查和副本检查两个边界，Finally 恢复原提供程序。它不进入应用或运行包，也没有给生产引擎增加测试钩子。VS 2026 新增 Copy Fallback 配置；44 份固定参考保持。

## 0.36 PCL 文件复用资格与客户端查找

- 重新阅读固定 `5ef81e4` 的 `ModNet.LoaderDownload.CheckExistingFiles` / `CheckExistingFile`（约 1474–1564 行）：跨目录查找前要求大小或哈希声明；客户端按版本目录中的匹配扩展名枚举，不限定为“版本目录名.jar”。原版还在复制失败后回退下载，本轮改动聚焦前述资格和候选查找。
- PlainCraftDownloader 在现有 `ReuseCandidates` 入口补上声明要求，缺少大小且 SHA-1/MD5 均空白时不查找其他目录。保留已有目标文件的检查及网络下载路径；不把没有上游声明的数据推定为可按文件名复用的内容。
- 查找客户端时先通过 `SafeFiles.UnderRoot` 校验版本目录，再只枚举该目录顶层的 `.jar`，按实际扩展名过滤。候选与临时副本沿用现有 `SafeFiles.Matches`，因此同大小的错误客户端不能通过已声明的哈希；JSON、嵌套文件不作为客户端候选。其他游戏根目录仍只读。
- 保留原有仅大小、仅 SHA-1、仅 MD5 及合法空文件复用规则；仅有哈希时仍按本工具既有校验能力处理，不声称原版的长度预筛完全相同。实际安装器检查不同名称的旧客户端可零次客户端 HTTP 请求安装新实例，新 JSON 身份独立，旧 JSON/PCL 设置字节不变。
- 新增 27 项回环 HTTP/模拟内容检查，以及 VS 2026 的 PCL File Reuse 配置。固定参考仍为 44 份，没有修改 PCL 等原仓库。

## 0.35 已提交下载的缓存清理

- 重新阅读固定 `5ef81e4` 的 `ModNet.NetFile.Merge`（约 1162–1245 行）：先合并、检查完整文件，再清理线程临时文件并调用 Finish；其外层捕获会重新合并或切源。PlainCraftDownloader 的原实现也把清理放在传输重试块内，实际文件占用可使已校验、已提交的目标被误报失败。
- 本轮是独立适配改进：只在完整内容校验及目标原子提交成功后调用 `TransferCleanup.DeleteCommitted`，逐个删除当前内容键下的已知分片、区间记录与旧分片名。文件占用或访问异常单独记录相对路径，继续其他文件；不把清理失败视为选源或内容传输失败。
- 日志失败不覆盖结果的处理方式继续参考 `ModDownloadLib.vb` 的安装清理 Try/Catch（约 1271–1277、2146–2161 行）。不声称 PCL 的 Merge 已采用同样的下载清理策略。损坏缓存恢复仍用原严格清理路径，校验失败和目标提交异常不在此处吞掉。
- 真实回环 HTTP、FileStream 共享占用覆盖单连接、8 MiB 分段、不可写日志；检查其余已知分片仍清理、未知文件和其他传输目录保持。另验证同大小损坏内容及目录占用目标的失败和重试。VS 2026 增加 Transfer Completion 调试配置，固定源码仍为 44 份。

## 0.34 最终写入前的取消检查

- 重新阅读固定 `5ef81e4` 的 `ModDownloadLib.McInstallState` / `LoaderStateChangedHintOnly`，原版对 Finished、Failed 和 Canceled 分别报告。另通过该提交的 Git 内容阅读 `Modules/Base/ModLoader.vb`：取消状态通过 CancellationTokenSource 传递，等待时以 OperationCanceledException 结束；没有将当前上游代码混作固定基线。
- PlainCraftDownloader 沿用独立暂存目录与最后改名的安装流程，本轮补齐进度通知返回后、JSON 暂存写入后，以及最终 Directory.Move 前的 token 检查。校验补全在计算是否需要替换客户端后、实际替换前检查，避免取消仍覆盖旧客户端。
- 安装入口先检查已取消的 token，防止先创建游戏根目录/写锁。下载引擎也在接收批次、清单合并后和发送完成通知前检查，覆盖空列表及清单生成期间取消。
- 文件系统提交调用后沿用真实提交结果，不再补一个可能将已发布实例误报为取消的检查。取消仍为协作式：本轮验证的是在最后一次检查前已观察到的请求，不声称将文件系统提交和取消请求变成同一原子操作。
- 实际安装器及回环 HTTP 检查原版、Fabric 模拟配置、已有原版补全、旧 JSON/PCL 设置字节保护和取消后的零内容下载重试。VS 2026 新增 Final Commit 配置，可直接在安装提交位置断点；固定源码仍为 44 份。

## 0.33 安装清理与任务结果

- 重新阅读固定 `5ef81e4` 的 `ModDownloadLib.vb`：新版 Forge 安装器的 Finally 清理（约 1272–1276 行）和 `McInstallFailedClearFolder`（约 2146–2160 行）均把清理异常捕获后单独记录。清理失败不会覆盖原安装结果。
- PlainCraftDownloader 独立增加 `InstallCleanup.DeleteOwnedDirectory`，用于本次组件工作目录和本次实例暂存载荷。只接受已生成的 GUID 私有目录格式，删除前通过既有 `SafeFiles.UnderRoot` 重新检查，拒绝目标目录链接；文件占用、访问失败单独记入累计 `installer.log`，日志不可写时也保留原结果。
- 组件工作区的初始化也纳入 Try/Finally。清理不遍历已有实例、共享 libraries/assets 或续传缓存；暂存 GUID 父目录及清理受阻的旧目录暂不自动回收。本轮没有实现启动时全局扫描清理，也没有强制解除文件占用。
- 真实内容校验、组件安装与最终目录写入异常仍沿原通路抛出。通过实际 FileStream 占用和安装器验证成功、原始配置错误、取消、真实发布冲突、日志路径被占用，以及解除占用后的重试。
- 只修改安装结束阶段和版本信息，无界面布局改动；原 44 份固定源码与七个原仓库状态保持。

## 0.32 普通支持库与原生库分别合并

- 重新阅读固定 `5ef81e4` 的 `Modules/Minecraft/ModMinecraft.vb`：`McLibListGetWithJson` 在展开支持库时分别设置 `IsNatives=False/True`，去重键包含 `Name` 和 `IsNatives`（约 1656–1757 行）。因此同一 Maven 坐标下的普通 JAR 和 Windows 原生 JAR 不是同一种可互相覆盖的文件。
- 重新核对 `ModDownloadLib.MergeJson` 的多份加载器配置合并和 `inheritsFrom` / `jar` 清理。PlainCraftDownloader 继续使用独立的启动配置合并，但给既有库身份补上普通/原生类别，修复同坐标两种库互相覆盖、普通库升级连带删除原生库的问题。
- 一条元数据同时有 `downloads.artifact` 和 `natives` 时，独立拆成普通库条目与原生库条目：前者保留 artifact，后者保留 natives、classifiers、extract；原生条目不再携带旧 artifact。这样最终 JSON 能进入 PCL 对应的普通/原生读取分支。PCL 原代码也清理显式 null 字段，本项目只在这里规范化 null 的 natives 标记，保留普通 Maven 回退。
- 条件 rules、classifier 和扩展名继续参与既有合并与下载判断，源 JSON 不被修改。版本冲突仍沿用本项目既有的后加载器覆盖行为，没有将这一轮描述为完整照搬 PCL 的最高版本比较策略。
- 通过实际安装器、回环 HTTP 和最终写出的 JSON 检查两种元数据格式、Java 单独升级、原生单独升级、一起升级、Windows/Linux 条件、文件复用，以及原生下载取消后的不发布和重试。没有在测试中启动 Minecraft。
- 本轮无界面布局改动或新增固定快照，原 44 份源码和原启动器仓库保持。

## 0.31 下载清单去重与校验约束

- 重新阅读固定 `5ef81e4` 的 `Modules/Base/ModNet.vb` 中 `LoaderDownload.Start`（约 1394–1420 行）：开始下载前以 `PathUtils.ForCompare(a.LocalPath).Lower()` 去重，随后逐项检测输入。本项目现在也在启动下载工作前，通过既有 `SafeFiles.UnderRoot` 解析真实保存位置并检查整批目标；大小写、斜杠与内部点路径不会生成重复传输或重复完成计数。
- PCL 原方法使用 `DistinctBy`。PlainCraftDownloader 的 `DownloadPlan.Merge` 在此基础上独立合并兼容的已知大小、SHA-1、MD5；相同算法哈希忽略大小写比较。第一条记录仍决定 URL、类别和路径显示，保留普通传输缓存键的原哈希拼写，不引入重复记录 URL 的隐式回退，也不修改调用方的记录。
- 同一目标的已知大小或同算法哈希互相冲突时，报告具体文件路径并停止该批文件下载。SHA-1 与 MD5 并存时均参与已有文件和下载完成校验；跨算法是否匹配同一内容仍由实际文件验证。下载错误文案改为“大小或哈希校验失败”，覆盖 MD5。
- 原版支持库、资源清单和完整安装分支拆分前均使用同一合并逻辑，避免旧的 `GroupBy(...).First()` 提前丢失校验信息。完整安装中的冲突可能需要先读取版本 JSON / 资源索引，但会阻止后续内容文件传输；并行连接预算、限速、分段、取消及选源机制继续复用。
- 本轮没有界面布局改动或新增固定快照，原 44 份源码保持。原启动器仓库不改动。

## 0.30 键盘选择与返回

- 重新核对固定 `5ef81e4` 的 `MyRadioButton`、`MyListItem`、`MyCard` 事件代码，以及既有 `MyVirtualizingElement` / `LazyLoadBehavior`。原版布局和鼠标按下/离开/释放行为继续保留；本轮的键盘激活、焦点转移及 Esc 层级是 PlainCraftDownloader 的独立补全，不宣称原版已有相同键位。
- 顶部页签、可折叠标题与交互版本行可参与焦点导航，Enter / 空格调用现有点击/展开/选择路径，重复按键不重复触发。关于页的只读信息行不进入焦点导航。普通按钮沿用既有键盘处理，并为按钮、图标、标题和版本行复用原主题/悬停资源提示键盘焦点；四个固定控件 XAML 均未改动。
- 长列表保持全量固定高度与视口按需生成；键盘焦点进入尚未创建的行时，生成并替换该占位、带入视野并将焦点交给新行。占位和实际行不同时留在布局中，不为键盘导航提前创建完整版本控件。
- 原版行由键盘选定后请求名称输入焦点，组件行选定后请求回到组件标题；返回原版列表时尽量保留原行，刷新替换行后回退到刷新按钮。Esc 先交给原 WPF 下拉菜单与当前展开卡片，再执行页面返回，不关闭浏览根页、不取消任务。
- 本轮未新增固定快照，44 份原文件保持。下载、安装、偏好格式及原启动器仓库未改动。

## 0.29 下载结束后的结果与操作

- 重新阅读固定 `5ef81e4` 的 `PageSpeedLeft.xaml.vb.TaskRefresh`：`LoadState.Failed` 使用失败路径图形并提供复制错误详情；`Finished` / `Canceled` 与失败分开处理，移除任务卡片。继续使用已固定的失败图形、PCL 按钮及主题资源。
- PlainCraftDownloader 为方便返回 PCL 使用，独立保留最后一次任务结果卡片，而不是像原版销毁卡片。成功提供实际返回的实例目录；成功、失败、取消均可复制原任务的版本、组件、名称、根目录与结果原因。取消阶段显示灰色「已取消 / 未完成」，真实失败仍使用原 PCL 的叉号。
- 任务开始时固定目录与安装选项，日志入口使用该任务的游戏根目录，后续选择不改写它；新任务会清空旧结果和操作提示。日志仍是 Core 在游戏根目录内追加的 `.vanilla-downloader/installer.log`，不是按次隔离的文件；界面工具提示和文档明确其为累计日志，没有改动原日志写入机制。
- 目录/日志不存在或无法访问时就地提示；外部程序或剪贴板操作失败不覆盖安装结果。预览与自检通过受控回调核对目标，不启动资源管理器、日志关联程序或写入用户剪贴板。
- 本轮无新增固定快照，44 份源码与下载 Core 保持不变。

## 0.28 设置操作的定位与反馈

- 本轮重新阅读固定 `5ef81e4` 的 `PageSetupLeft.xaml.vb`（按目标切换设置分区）与 `PageSetupSystem.xaml.vb`（重置设置后的成功提示、异常提示及重新加载），并复核现有 `Settings.vb` 的逐项读写机制。继续使用固定原版 `MyButton`、输入框及调色板，不扩展主页面数量。
- PlainCraftDownloader 将不同设置合并在一个滚动页，与 PCL 的独立设置子页结构不同。因此新增本项目的 `NavigateToSetting`：先显示设置页并更新布局，再将对应控件带入视野；目录入口定位游戏目录卡片，限速错误定位并聚焦输入框。没有将单纯滚动到页面顶部等同于定位目标。
- 保存结果使用原调色板的主题色/错误红色，在保存按钮旁显示可换行文字，编辑相关参数后清空过期提示。PCL 原版使用全局 Hint / 日志提示，本项目独立适配为就地反馈，保留现有显式保存与关闭保存机制，不声称实现原版逐项即时保存。
- 本轮没有新增源码快照，原 44 份及固定提交保持不变。未改动设置文件格式、原子写入、偏好目录、下载 Core 或原启动器仓库。

## 0.27 关于页与 BetterPCL 名称



- 直接参照固定 `5ef81e4` 的 `Pages/PageOther/PageOtherAbout.xaml`：25 DIP 页边距、`21,40,21,16` 卡片内容边距、45 DIP 信息行、40 / 自适应 / 150 DIP 三列、圆形图标、只读 `MyListItem` 的 `-5,0,15,0` 边距与 35 DIP 操作按钮。两份关于页原文件现已纳入 `reference/PCL`，共 44 份固定快照。

- 参照 `PageOtherAbout.xaml.vb` 的重新进入滚动归顶及运行版本替换；BetterPCL 直接读取本程序集版本，显示构建版本并可复制 CLR 与固定参照提交信息，不使用静态占位版本号。

- 内容独立适配为项目说明、版本、与 PCL 的协作方式及来源致谢，提供 PCL / BMCLAPI 实际项目链接和随附文档入口。BP 圆标与缩写图标由本项目制作；原版作者行不作为 BetterPCL 作者信息。关于页作为设置子页，保留下载、设置两个主标签和返回前的设置滚动位置；下载任务及选择不受导航影响。

- 窗口标题、关于页与文件产品信息使用暂定名称 BetterPCL。便携包生成 `BetterPCL.exe` / `BetterPCL.exe.config`；内部程序集名、VB 命名空间和现有偏好/缓存目录保留原值，以兼容嵌入资源、既有设置和安装缓存。下载/安装 Core 本轮无改动。



| 本项目 | PCL 源文件、方法 | 采用的规则及改造 |

|---|---|---|

| `PclFolders.vb` | `Modules/Minecraft/ModMinecraft.vb`：`SetMcFolder`、`McFolderListLoadSub` | PCL 旁的游戏目录、`versions` 检测、官启目录、自定义列表的 `名称>路径\|...` 格式、路径去重、`$` 替换为 PCL EXE 所在目录 |

| `PclFolders.vb` | `Pages/PageSetup/Settings.vb`：`LaunchFolderSelect`、`LaunchFolders`、`Get`；`Modules/Base/ModBase.vb`：`IniGetContent` | 当前目录从 `PCL/Setup.ini` 读取，自定义目录从注册表读取；INI 冒号分隔、重复键以最后一项为准。只读取相关值，不扫描或保存账号数据，不修改 PCL 的配置 |

| `MinecraftInstaller.vb` | `Pages/PageDownload/ModDownloadLib.vb`：`McDownloadClientLoader` | 版本 JSON / 索引预检查后，并行下载客户端、支持库与资源；实例 JSON/JAR 同名；保留官方版本信息并写入 PCL 的 `clientVersion` 字段 |

| `MinecraftInstaller.vb` | `ModMinecraft.vb`：`McLibListGetWithJson`、`McLibGet`、`McJsonRuleCheck` | Maven 坐标到目录、artifact/classifier、Windows natives、`${arch}`、有序 allow/disallow 规则。独立处理必要的架构匹配，原生库与普通 artifact 都保留 |

| `MinecraftInstaller.vb` | `ModMinecraft.vb`：`McAssetsListGet`；`ModDownload.vb`：`DlClientJarGet`、`DlClientAssetIndexGet` | assets 的 hash 前两位子目录、索引文件、大小/SHA-1 校验、virtual/map_to_resources 旧版资源路径；同时保留 objects 供后续复用 |

| `SourcePolicy.vb` | `ModDownload.vb`：`DlSourceAssetsGet`、`DlSourceLibraryGet`、`DlSourceLauncherOrMetaGet` | 对应官方主机的 BMCLAPI 路径映射、来源优先级与失败回退。使用本应用 User-Agent，不冒用 PCL 身份或搬运 CDN 签名/账号凭据 |

| `DownloadEngine.vb` | `Modules/Base/ModNet.vb`：`NetFile`、`TryBeginThread`、下载线程、`LoaderDownload.CheckExistingFiles` | 校验本地文件、其他目录复用、多文件并发、大文件分段、BMCLAPI 不分段、Range 异常回退单连接、大小和哈希通过后提交 |

| `DownloadEngine.WaitForBandwidthAsync` | `ModNet.vb`：`NetTaskSpeedLimitHigh` / `NetTaskSpeedLimitLeft`、下载线程限速循环（995–1000 行）、100ms 刷新循环（1778–1784 行） | 沿用所有连接共享额度的思路，独立改为异步排程；空闲信用最多 100ms，避免无限积累；批次重新开始时清除取消产生的等待负债，等待可取消且不占网络无数据超时 |

| `Controls/Pcl*.xaml` / `MainWindow.xaml` | `Controls/MyButton.xaml`、`MyRadioButton.xaml`、`MyListItem.xaml`、`MyCard.vb`、`FormMain.xaml`、下载页 | 四个控件（含 MyIconButton）的 XAML 原样复制，仅替换类名；原版调色板、顶部图形、字体和方块图标。卡片构造参数、48 DIP 标题栏、42 DIP 版本行、40 DIP 折叠标题、25 DIP 页边距、独立安装页按源码移植 |

| `Controls/PclSelectionStyles.xaml` / `PclComboBox.vb` / `PclCheckBox.vb` / `PclTextBox.vb` | `Application.xaml` 的 `MyComboBox`、`MyComboBoxItem`、`ToggleButton`、`MyTextBox` 样式；`Controls/MyComboBox*.vb`、`MyCheckBox.xaml(.vb)`、`MyTextBox.vb` | 3 DIP 圆角、原箭头路径及 200ms 转动、8.5/21.5 DIP 选中项边距、320 DIP 菜单上限、6/4 DIP 项内边距、18/12 DIP 勾选框及原勾选路径、输入提示和错误区。保留 WPF ComboBox/CheckBox/TextBox 基类、选择事件、键盘及 UI Automation；动画适配本项目调度器。Popup 直接绑定 ActualWidth，不改写控件 Width；额外阻止关闭时滚轮误改值 |

| `PclTransferStatistics.vb` / `PclSegmentScheduler.vb` | `ModNet.RefreshStat`、`NetFile.TryBeginThread` | 100ms 采样，最近 30 次加权均速，最近一秒 85% 单调速度基线；优先未完成区间，最大剩余片段 60/40 切分、256 KiB 下限、连接准备数控制 |

| `SourcePolicy.RaceJsonAsync` | `ModDownload.DlSourceLoader` | 首选先启动，备选延迟启动，首选失败立即唤醒备选；只返回经过 SHA-1 和 JSON 解析验证的结果，取消并等待输掉的请求 |

| `Controls/PclTheme.vb` | `ModBase.MyColor.FromHSL2`、`ModSecret.ThemeRefresh`、`ThemeRefreshMain` | 亮度修正表、蓝色主题与标题/正文渐变公式；0.7 补齐 `ColorBg0 = Color4 * 0.4 + Color5 * 0.4 + ColorGray4 * 0.2`、按下背景 Alpha 190 的主题资源更新 |

| `Components.vb` | `ModDownload.vb`：`DlForgeVersionListBmclapiMain`、`DlNeoForgeListOfficialMain`、`DlFabricList*`、`DlLiteLoaderList*`、`DlOptiFineList*`、`DlFabricApiLoader`、`DlOptiFabricLoader` | 七类版本列表、OptiFine 的 Forge 版本要求、NeoForge 新旧 Maven 坐标、Fabric API 的 Modrinth 项目、OptiFabric 的 CurseForge 项目 322385。没有复用启动器 API 密钥 |

| `ComponentSelection` / `MainWindow.Components.vb` | `PageDownloadInstall.xaml.vb`：`ReloadSelected`、`Load*GetError`、`IsOptiFineSuitForForge` | Forge/NeoForge/Fabric 互斥、指定 Forge 版本兼容检查、Fabric 1.20.5+ 的 OptiFine 限制、Fabric API/OptiFabric 依赖、LiteLoader 等按游戏版本显示 |

| `ComponentInstaller.vb` | `ModDownloadLib.vb`：`McDownloadForgelikeLoader`、`McDownloadOptiFineInstall`、`McDownloadLiteLoaderLoader`、`McDownloadFabricLoader`、`McInstallLoadersGet`、`MergeJson` | 安装器与支持库预下载、Mojang mappings 校验、客户端处理器、旧 Forge versionInfo、OptiFine 独立安装/作为 Mod、启动参数合并、移除继承关系、实例内模组隔离。独立处理器执行层替代 PCL ForgeInstaller/JavaWrapper 二进制 |

| `MainWindow.xaml` / `MainWindow.Tasks.vb` | `PageDownloadInstall.xaml`、`MyExtraTextButton.xaml`、`PageSpeedRight.xaml`、`PageSpeedLeft.xaml` | 62 DIP 名称卡、40 DIP 组件标题、132 DIP 信息起点、原组件 PNG、居中蓝色下载按钮、26 DIP 分任务行、原完成/等待图形、总进度/速度/剩余文件；新增连接、重试、分段和日志 |

| `InstallPreflight.vb` | `PageSelectLeft.xaml.vb` 的 PCL 文件夹校验；`McDownloadOptiFineInstall`、`ForgelikeInjector` 的 Java 选择 | 提前检查 PCL/Java 不兼容路径以及必要 Java；避免在下载完整游戏后才发现运行环境不足 |

| `MainWindow.xaml` / `Controls/PclSlider.vb` | `PageSetupSystem.xaml` 的下载卡片；`MySlider.xaml(.vb)` | 共享标签列、标签右边距 25、常规行 28/间隔 7、卡片内容 25/37/25/15、动作按钮高 35；滑条 10 DIP 圆点、1.2 DIP 描边、2/1 DIP 前后轨道及 40/100/200ms 配色。保留 WPF Slider 的拖动、命令及 RangeValue 接口；连接档位依下载内核限制为 8/16/32/64，旧 connectionsIndex 仍可读取 |

| `PclTypography.xaml(.vb)` / `PclTextBox.vb` | 原 `Resources/Font.ttf`、`Application.xaml` 的字体回退、`MyTextBox.HasBackground`、安装页的 `TextSelectName` | 统一从明确的程序集基准 URI 加载 PCL English，保留 Microsoft YaHei UI 回退；名称框透明背景、30 DIP 高度、15 DIP 字号、70 字符上限。通过 GlyphTypeface 确认实际字体文件，避免只有字体名称相同而实际回退 |



本地核对时，PCL 工作区 HEAD 是 `6b9a50df911c9d68b69b24c979efba477fb14b67`，该仓库保持未修改。上述目录读取、支持库和下载模块在限制前基线中也存在；对受限制影响的方法使用 `git show 5ef81e4:路径` 核对。



## 0.26 个性化基础

- 直接阅读固定 `5ef81e4` 的 `PageSetupUI.xaml`、`Settings.vb`、`MyRadioBox.xaml(.vb)`，新增四份快照，共 42 份。基础卡片、主题单选布局、不透明度上限 600，以及 `UiLauncherTransparent / 1000 + 0.4` 的窗口不透明度换算都有公开来源。单选控件采用原 18 DIP / 1.1 DIP 外圈、9 DIP 选中点与 26 DIP 文字起点，保留 WPF RadioButton 的分组、键盘和自动化语义；未移植整套 MyRadioBox 动画。
- `ModSecret.vb/ThemeRefresh/ThemeRefreshMain` 公开了 HSL2 调色板、亮度修正系数、三段标题渐变、彩色背景及关闭后 RGB(245,245,245) 背景公式。本项目将既有固定蓝色主题参数化，沿用这些公式；默认蓝色仍使用公开的 Hue=210、Sat=85、亮度偏移 0。
- 原版公开 `ModSecret.vb` 没有完整的主题 ID 到配色参数映射，`ThemeCheckAll/ThemeCheckOne/ThemeUnlock` 也为省略实现。因而本项目的青、绿、黄、棕、灰黑及彩色渐变预设参数为独立适配，彩色项命名「彩色渐变」，不冒充原版隐藏主题「滑稽彩」的完整实现。自定义直接使用用户指定的色调、饱和度和亮度偏移；没有迁移 PCL 的解锁记录。
- 原 `Images/Themes/12.png` 确实存在，标题图层参照 `FormMain.xaml/ImgTitle` 的 UniformToFill、低质量缩放及不接收点击设置，直接使用原文件。图片本身最大 alpha 只有 26/255，所以图层保持 Opacity=1，避免重复降低透明度而使纹理消失。素材字节另与固定 Git 对象核对。
- 外观入口并入现有设置页，不增加主导航。通过应用资源和已加载控件的主题刷新更新按钮、文本框、滑条、卡片、列表高亮及任务阶段颜色；监听在 Loaded 时接入、Unloaded 时移除。仅改变颜色，保留列表实例、组件选择、自定义名称、任务进度与下载源设置；下载期间传输设置继续锁定，外观仍可调整。
- 外观独立保存在本应用 JSON 的 appearance 对象中。即时保存只合并该对象，保留已持久化的下载参数及未知字段，不读取或改写 PCL 主题注册表。正常保存其他设置时也带上当前外观；恢复默认只恢复外观。损坏/未知字段回退或限幅，非有限数不进入配色运算。
- 隔离的 `PreviewSurface` 为临时根连接应用资源字典，以接收与 Window 相同的动态资源失效通知；不在用户窗口中注入或操作其他应用。整窗透明度通过 Window.Opacity 检查，离屏截图提取的是内容，本身不能证明桌面透视的合成效果。

## 0.25 七类组件长列表与选择保留

- 直接对照固定 `5ef81e4` 的 `ModDownloadLib.vb`：`ForgeDownloadListItem`、`NeoForgeDownloadListItem`、`FabricDownloadListItem`、`FabricApiDownloadListItem`、`OptiFineDownloadListItem`、`OptiFabricDownloadListItem` 和 `LiteLoaderDownloadListItem` 都返回 `MyVirtualizingElement(Of MyListItem)`，高度为 42 DIP。安装页的 Fabric 使用 `SetContentBuilder`；Forge/NeoForge 等添加延迟行，并不按每 100 条要求一次“显示更多”。相关来源均已在现有 38 份固定快照中。
- 组件列表统一复用 0.24 的 `PclLazyRow`，保留完整可滚动高度，移除 100 条分页与追加按钮。实际行创建时保留现有组件字体、图标、版本/兼容说明，并将原始 `ComponentEntry` 放在 Tag 中；点击仍走同一个 `SelectComponent`、依赖选择、兼容性检查及命名入口。
- 对照 PCL `Fabric_Selected/Fabric_Clear` 在更新选择后折叠卡片与刷新摘要的流程，选中项变化不再单独触发列表重建：若条目、过滤、加载和错误状态不变，仅更新已创建行的 Selected。尚未创建的行首次可见时读取当前选择，避免捕获已经失效的选中状态。列表内容、兼容过滤或来源元数据变化仍按原有判断替换 body。
- 保留选择本身的独立适配：自定义名称、手动取消可选依赖、切源后同版本重新绑定、缺少版本时待确认与已有互斥规则不变。没有把整套 PCL 组件列表文案、推荐分区或动画标记为全面一致。

## 0.24 原版长列表按视口加载

- 直接阅读固定 `5ef81e4` 的 `MyVirtualizingElement.vb`、`Controls/Behaviors/LazyLoadBehavior.vb`，以及 `ModDownloadLib.vb/McDownloadListItem`。PCL 返回固定高度的占位元素，利用 `LayoutUpdated`、`IsVisible`、祖先 `ScrollViewer` 视口与变换后的行矩形判断首次进入；在原 Panel 中按相同索引替换成实际版本行。补充两份固定快照，总计 38 份。
- `Controls/PclLazyRow.vb` 将该机制用于最新版本和四类原版列表：占位行保持 42 DIP 高度，实际行继续使用现有 PCL XAML、字体、图标和原始下载条目。直接滚动到中间或底部不会生成跳过的行，列表不增加分页按钮，不缩小滚动范围。
- 已创建的行继续保留，这是首次可见时生成的单向替换，不是滚出视口后回收控件。独立适配增加初始化幂等保护、成功后移除布局监听，以及 Unloaded/Loaded 时暂停/恢复等待；重新获取列表后，旧分类不在视口祖先链中，不会因后续布局继续生成旧条目。
- `PreviewSurface.vb` 仅用于隔离自检和 `--snapshot`：创建没有 WS_VISIBLE 的隐藏 WPF presentation source，使真实的 `IsVisible`/Loaded/Unloaded 及滚动视口逻辑在离屏渲染中生效；结束后解除 RootVisual 并释放。不以“生成所有行”绕过生产视口判定，不操作用户窗口或写入游戏数据。
- 分类/别名检查需要遍历全部测试条目时显式初始化占位行；另用未经该遍历的 1,200 条生产列表检查实际按视口加载。原版长列表机制本轮已补齐；0.24 时组件仍每次追加 100 条，该差异随后在 0.25 处理。完整卡片/页面动画仍按 0.21 的差异记录。

## 0.23 版本分类、推荐与特殊版本呈现

- 直接对照固定 `5ef81e4` 的 `PageDownloadInstall.xaml.vb/ShowMinecraftVersionList`：将不含 combat、rc、experimental、pre 的 `1.` 开头 snapshot 归入正式版，再以指定愚人节 ID 或 UTC+2 的 4 月 1 日日期覆盖；原始 release、special 和历史类型各自保持对应类别。四类互斥、按发布时间降序；最新推荐先放正式版，仅在预览版更晚时追加预览版。
- `VersionPresentation.vb` 是独立的展示适配。PCL 在深拷贝的 JSON 上修正 type、改写 `20w14infinite` 的显示 ID、添加 lore；本工具保留 `VersionEntry` 原始实例和 Id/Kind/Url/Sha1/Released，另行计算显示分类、图标、简介与 `20w14∞` 别名。点击行继续向安装器传原条目，元数据 ID 校验与组件目录查询不会收到显示别名。默认实例名称可使用别名，手动名称规则保持不变。
- 对照 `ModDownloadLib.vb/McDownloadListItem`，普通版本副标题改为日期，最新项继续标注「最新正式版/预览版，发布于」。十段愚人节简介逐字取自 `ModMinecraft.vb/GetMcFoolName`；新增该文件的固定快照，总计 36 份，并由来源检查核对十段文字。完整源文件只用于参考，不编入应用。
- 对照安装页 `Init` 对 `ImgMinecraft.Source = VanillaIcon` 的处理，Minecraft 摘要不再固定显示草方块；它使用原版图标，名称卡在添加 Fabric 等加载器后仍按既有优先级显示组件图标。
- 对有效但稀疏的列表做独立容错：只有预览版或只有正式版时仍提供推荐，只有特殊/历史版本时不显示空的推荐卡。PCL 此处直接索引两类首项，本工具不会为缺失类别构造虚假版本。

0.21 中记录的特殊版本分类差异已在本轮处理。完整卡片动画、两页导航及下载内核既有差异仍按各节记录；本轮没有进行真实 PCL 运行画面的逐像素验收。

## 0.22 名称即时校验与回车安装

- 固定来源为 `PageDownloadInstall.xaml.vb` 的 `Init`、`SelectNameUpdate`、`TextSelectName_TextChanged/ValidateChanged/KeyDown`，以及 `ModValidate.vb/ValidateFolderName`。新补充 `ModValidate.vb`、`MyHint.xaml(.vb)` 三份固定快照，总数由 32 增为 35；原快照内容未改。
- 名称由独立的手动编辑标记管理，自动写入名称时不把自身更新当作手动编辑。用户把名称改回默认文字也继续保持手动状态；换一个 Minecraft 版本才重新进入自动命名。切换设置和目录保留上下文的产品行为继续存在。
- UI、点击前和实际安装内核共用 `InstallPreflight` 的名称/目标检查；不再只在开始时弹窗。沿用既有 Windows 路径约束，补齐固定 PCL 的 CLOCK$/COM0/LPT0 与 NTFS 短名称格式检查。名称检查使用原始文本，首尾空格会报告错误，不再悄悄 Trim 后安装；新增安装的目录撞名、同名文件占位均提前拒绝，实际提交时的竞态保护仍保留。
- 输入框按源码设置 `ShowValidateResult=False`，错误只在单独的红色 `PclHint` 中显示；其他文本框默认继续显示行内提示。提示条保留 MyHint 的 3 DIP 左边框、2 DIP 圆角、12/9 DIP 内边距、16 DIP 行高、-1 DIP 底边距及三项红色值。它是仅供名称错误使用的不可关闭适配，没有移植其他主题、自定义点击或“不再提示”存储功能。
- 回车只在可见的安装选项页、当前输入有效且没有安装任务时提交，使用与开始按钮相同的处理器。重复回车、隐藏页面和安装中均不提交；开始入口再次读取目录状态，避免名称未改但目标已被其他程序创建时沿用旧判断。
- 本工具保留「校验补全已存在原版」：明确勾选后允许存在版本 JSON 的目标，加载器组合仍须使用新实例。这个即时检查只确认目标结构，实际内核仍需按官方元数据检查同版本和原版声明，不把 JSON 存在等同于已经验证可修复。

0.21 中名称即时校验、回车与手动编辑标记的差异已在本轮处理；特殊版本分类随后在 0.23 处理，长列表/页面动画及两页导航等其他保留差异仍适用。未进行真实 PCL 运行画面的逐像素验收。

## 0.21 视觉与操作流程再次对照

本轮再次直接阅读固定 `5ef81e4` 的 `MyListItem`、`MyRadioButton`、`MyButton`、`MyCard`，以及 `PageDownloadInstall` 的选择、名称和列表处理。继续保留下载补全工具的两页范围，未恢复其他启动器入口。

| 对照项 | 固定 PCL 依据 | 本轮结果 |
|---|---|---|
| 版本行与顶部页签点击 | `MyListItem.Button_MouseDown/MouseUp/MouseLeave`、`MyRadioButton.Radiobox_Mouse*` | 补上按下归属、移出及禁用后取消、单次松开；已选页签不重复操作。顶部按下事件在本窗口中阻止冒泡到标题栏拖动 |
| 普通按钮与卡片 | `MyButton.Button_Mouse*`、`MyCard.IsSwapped` 和鼠标事件 | 按钮移出后取消，不再持有鼠标捕获；按下缩放 0.955，正常释放 300ms、移出恢复 800ms。卡片只接受完整点击，展开时使用源码的 34 DIP 点击区；设置相同折叠值不发重复事件，初次展开仍能懒加载 |
| 版本行与页签颜色 | `MyListItem.RefreshColor/Loaded`、白色主题 `MyRadioButton.RefreshColor` | 全高选中行采用 ColorBrush2；选中后的闲置行不保留悬停底色。按下底色 ColorBrush6、行缩放 0.98、背景初始比例 0.75；顶部按下 alpha 120/60ms、悬停 alpha 50/90ms、选中 120ms、移出 150ms |
| 组件摘要 | `ReloadSelected` 与 `Pan*Info/Lab*` 的 XAML | 选中摘要改用 ColorGray1，添加 CharacterEllipsis；完整版本保留在 ToolTip，文字与清除按钮的实际布局边界另有检查 |
| 展开列表保留 | `MyCard.SetContentBuilder/BuildContent`、`Fabric_Loaded` 等按组件更新 | 修复本工具每次刷新都重建所有展开卡片的行为。按组件条目、当前选择及加载/错误状态失效内容；无关来源晚到时保留已显示的 150 项，当前列表更新仍替换旧行 |
| 默认名称与名称图标 | `GetSelectName`、`GetSelectLogo` | 对齐 Fabric 构建后缀、省略 LiteLoader 重复版本、Forge/NeoForge/OptiFine 下划线与顺序；名称卡按 Fabric → Forge → NeoForge → LiteLoader → OptiFine 选择图标，自定义名称不妨碍图标更新 |

以下为 0.21 当时的明确差异，其中名称行为随后在 0.22、版本分类在 0.23 处理：

- `MyCard` 超过 800 DIP 的匀速/减速分段动画、PCL 动画曲线和完整页面切换动画仍未移植；当前卡片高度动画仍为 WPF 150ms。普通按钮长按后的第二段 700ms 微缩也未照搬。
- PCL 名称输入有 `ValidateFolderName` 即时反馈及 Enter 开始操作；本工具名称合法性/撞名检查仍主要在开始安装时执行。自动名称采用上次自动值比较，自定义编辑标记不等同于 PCL 的完整 `IsSelectNameEdited` 事件语义。
- PCL 版本分类会修正部分 Mojang snapshot 标记、处理指定愚人节 ID；本工具仍主要使用上游 type 和 4 月 1 日判定。这会影响少数历史/特殊版本的分类与图标。
- 按用户已确定的补全工具流程，返回或切换设置保留安装上下文与任务详情；PCL 的 `ExitSelectPage` 清空选择，以及完成/取消后移除任务卡片的行为继续有差异。两页导航、刷新入口和额外下载详情也保留。
- 本轮不改下载内核。来源、连接/超时与全部组件并行范围仍以 0.16/0.17 的对照及已知差异为准，不能由这轮视觉检查推断已与 PCL 全面等价。

鼠标检查使用 WPF 路由事件和实际页面处理器，图片是本工具 96 DPI 离屏示例；没有真实 PCL 运行截图的逐像素对比，也没有跨 DPI 或真实鼠标拖动的人工验收。

## 0.20 取消、结束与重试的进度归属

- 再次核对固定 PCL 的 `PageSpeedLeft.xaml.vb/TaskRefresh`：以实际 Loader 的 `State` 区分 Loading/Waiting、Failed、Finished/Canceled，只有运行状态刷新阶段百分比；`ModDownloadLib.vb/McInstallState` 按最终状态显示结果。当前工具此前仅靠窗口 `_busy` 判断是否接收回调，无法区分错误提示框仍打开的已失败任务，以及已经开始的下一轮重试。
- 现在每次 `ResetTaskView` 产生新批次，实际 `Progress(Of TransferProgress)` 捕获自己的批次；只允许当前窗口、当前批次、尚在运行的任务更新页面。`FinishTaskView` 在写入最终 UI 前关闭进度接收，因此嵌套消息循环中的旧回调也不能重新打开已结束阶段或降低成功百分比。
- 取消进入独立的等待状态，立即请求所属取消源、禁用重复取消，等待说明和标题保持稳定；等待期间速度、连接数和剩余文件显示未知，最终结束才显示零速度/零连接。关闭活动任务复用同一取消入口，仍等待实际安装退出后再释放设置及允许关闭。
- 这是 WPF 事件队列和单任务界面的独立适配。本工具保留已结束任务详情，未照搬 PCL 完成/取消后移除任务卡片的行为。下载内核的取消、目录锁、校验缓存与发布规则未修改；通过额外回环安装检查验证取消完成后释放写锁、清除私有 payload，以及同名重试成功。

## 0.19 当前组件组合的即时校验

- 再次核对固定 PCL `PageDownloadInstall.xaml.vb` 的 `LoadOptiFineGetError`、`LoadForgeGetError`、`IsOptiFineSuitForForge`、`OptiFine_Selected` 和 `Forge_Selected`：按 OptiFine 声明过滤具体 Forge 版本，空声明、不可用声明及旧 build 编号分别处理；当前选择变化后重新显示可用条目和兼容状态。本轮不改变 `ComponentSelection` 的既有规则，界面直接复用其 `Validate`，与最终安装前校验保持一致。
- 0.18 对同版本条目重新绑定新元数据后，如果上游的 Forge 要求发生变化，页面此前仍可能显示通常的安装说明、保持开始按钮可用，直到点击后最终校验才报错。现在在列表完成、组件选择/清除及解除任务锁定时即时检查；当前组件不兼容则标记标题、提供原因和禁用开始按钮，兼容替代条目仍可展开选择。
- 缺少必需的 OptiFabric 与仅推荐的 Fabric API 分别处理：前者等待依赖就绪或用户调整组合，后者不阻止安装。禁用按钮启用 WPF 的 `ToolTipService.ShowOnDisabled`，在小窗口下无需先滚到提示区域也可悬停查看阻塞原因，布局和原有控件样式保持不变。
- 继续遵守 0.18 的刷新保留策略：元数据刷新引发冲突时保留选择和自定义名称，等待用户选择或清除；不照搬 PCL 手动选中事件自动清掉另一组件的所有行为。本工具的两页范围、WPF 事件调度与原加载器框架仍有差异。

## 0.18 切源、刷新与已选组件保留

- 再次核对固定 PCL 的 `PageDownloadInstall.xaml.vb`：`ReloadComponentLists` 在列表完成时更新各组件显示，`ReloadSelected` 独立刷新选择与兼容状态，`EnterSelectPage` 启动各类组件获取。本工具沿用列表状态和当前安装选择分别更新的思路；保留两页范围，在安装页顶部复用现有「刷新列表」，没有加入启动器功能。
- 组件列表以请求批次、所选 Minecraft 对象和各组件自己的取消源共同判断结果归属。全量刷新与切换列表源使旧批次失效；单项重试只替换该组件请求，不取消其余列表。旧任务即使忽略取消并成功或失败，也不能改写新结果或移除新请求的状态；各任务结束后释放自己拥有的取消源。
- 同一个组件版本从新列表取得新对象，以更新 URL、校验和其他上游声明；不只按版本文字保留旧对象。自定义名称、已选版本、手动取消依赖继续保留。新列表没有该版本时保留选择并提示「待确认」，暂停开始安装，等待刷新、重新选择或取消。自动依赖清除后重选不会遗留旧的确认状态；开始安装后冻结选择。
- 修改版本列表来源时先取消并使旧结果失效，返回下载页再按新偏好刷新当前列表；多次修改设置不逐次后台联网。修改文件来源不清空独立的版本列表缓存。原版列表采用同样的批次与取消所有权，刷新失败保留已有列表；安装中或关闭后的晚到结果不能覆盖安装状态。
- 这些请求归属、保留策略及 WPF 异步调度是本工具的独立适配，不声称逐行移植 PCL 的加载器框架。沿用原卡片、按钮、字体和图标；仅增加加载中、待确认文字与上下文提示。受控异步检查不等于公网响应、Minecraft 启动或完整 PCL 像素一致性验证。

## 0.17 独立选择官方与 BMCLAPI

- 直接核对固定 PCL 的 `PageSetupSystem.xaml`「下载」卡：`ComboDownloadSource` 绑定 `ToolDownloadSource`，`ComboDownloadVersion` 绑定 `ToolDownloadVersion`，每项按镜像 / 自动 / 官方排序，默认中间自动。界面保留原三档文案，仅将“镜像源”明确为 BMCLAPI；行号 0/2、28 DIP 高度与 7 DIP 间隔继续对齐。
- `SourcePolicy.Preference` 控制文件；新增可空 `VersionPreference` 控制列表。App 明确设置两项；旧 API 调用者未设置列表偏好时，继续沿用文件偏好。元数据按用途区分：版本目录走版本偏好，选定版本 JSON、资源索引与 Fabric 启动配置走文件偏好，遵循 PCL `DlSourceLauncherOrMetaGet` 关于不能用于版本列表的说明。
- Minecraft 和组件版本列表按版本设置竞争：自动/镜像优先/官方优先分别在 5/30/60 秒后启动备用源，首选失败立即启动备选，等待输掉的请求结束。文件自动判定仍共享本次会话的官方探测状态，手动文件选择独立覆盖它。实际请求的 45 秒超时与 PCL 全部动态超时仍有差异。
- 修正此前固定先访问 BMCLAPI 的 Forge、OptiFine 列表；Forge 官方采用本工具原有的 Maven XML 解析，镜像采用 BMCLAPI JSON；OptiFine 官方 HTML 和镜像 JSON 分别解析后参与竞争。Forge 官方 Maven 列表的校验信息不等同于 PCL 官方 HTML 所列 MD5；仍保留 BMCLAPI 返回的 SHA-1/MD5。NeoForge 新增固定 PCL 的 `/neoforge/meta/api/maven/details/releases/net/neoforged/{package}` 镜像路径，并按 `GetNeoForgeEntries` 提取两种格式中的版本名、排除 47.1.82。
- OptiFine 文件下载也按文件设置选择官方解析或 BMCLAPI，失败后先清除本次私有目录内无效 payload，再换源并重新验证 ZIP 中的安装器标识，避免把坏的非空文件当作已完成文件复用。有效官方/镜像下载继续使用同一个内容连接和限速预算。
- 显示索引改为 PCL 的 0=镜像 / 1=自动 / 2=官方；持久化继续使用旧版语义值 0=自动 / 1=官方 / 2=镜像，避免旧配置被错读。新增 `versionSource` 字段，缺失时沿用旧 `source`；未知/不合法值返回默认，两个选择器随安装设置卡一起冻结。
- 用户给出的 [OpenBMCLAPI](https://github.com/bangbang93/openbmclapi) README（2026-10-21 核对）说明其用途为协助 BMCLAPI 分发文件。客户端继续使用 `bmclapi2.bangbang93.com` 公开入口，入口负责实际分发节点选择，无需用户填写节点集群 ID/密钥。Modrinth、CurseForge/OptiFabric 等第三方服务依旧使用其实际接口，不能把任意节点域名替换为同路径的通用 Minecraft 镜像。

以下 0.16、0.15 表格保留各轮当时状态；“两项偏好共用设置”的旧差异已在本轮拆分。

## 0.16 自动选源、跨阶段并行与图标按钮

再次直接核对固定 `5ef81e4` 的 `ModDownload.vb`（198–245、1215–1238 行）、`ModDownloadLib.vb`（客户端加载器与 `McInstallLoadersGet`）和 `ModNet.vb` 的全局预算。下方 0.15 表格保留的是上轮状态，本节记录本轮更新。

| 项目 | 实际实现与依据 |
|---|---|
| 官方探测与文件自动排序 | `SourceSelectionState` 对应 PCL 的 `DlPreferMojang`：完整有效的官方版本列表在 **小于 4000ms** 内返回后，会话内自动文件排序转为官方优先，已确认的状态不因之后慢请求降级。未探测成功时仍镜像优先；手动官方/镜像优先覆盖自动判定。MainWindow 创建的所有策略共用状态，不写入 PCL 配置 |
| 版本列表竞争 | 自动模式官方立即启动，5000ms 后启动镜像；镜像优先 30000ms、官方优先 60000ms 后启动备选。首选失败立即唤醒备选，取消并等待输掉的请求；采用 PCL 客户端列表启动预算。保留 v2 manifest 与各版本 JSON 的 SHA-1 声明，所有来源需至少 200 个有效版本条目；无效/取消响应不能学习来源偏好 |
| 原版分支 | `MinecraftInstaller` 按 `McDownloadClientLoader` 非阻塞组合拆出客户端、支持库/日志、资源索引/对象分支。前置 metadata、Java、路径和资源索引规划验证完成后并行下载；资源与支持分支进度按 PCL 的 18:14 权重合成 |
| 组件与资源并行 | 客户端及支持库校验完毕，允许 `ComponentInstaller` 在资源分支仍运行时下载组件依赖、执行处理器；全部分支成功后才提交实例。保持原有七类组件及互斥规则；没有宣称完整复制 PCL 的所有组件预取组合 |
| 一个内容预算 | 新 `DownloadBudget` 在整次安装内共享全局连接信号量、每主机信号量、100ms 镜像启动节流、速度额度和传输缓存所有权；新批次只有在所有活动批次结束后才重置限速排程。组件的后续下载同样借用此预算，避免并行后各分支分别获得全额速度或连接额度 |
| 并行进度与失败 | `InstallationProgress` 保留每分支快照，累计字节/复用/分段/失败数只相加一次；阶段完成状态独立保存。UI 加权求和、保持单调，资源仍在运行时不会因为处理器完成而打勾；当前阶段文件数量与全局剩余数量分别传递，Java 日志的未知进度仍保留省略号。失败取消其余分支并等其退出，保留原始错误、根目录锁和最终原子发布机制 |
| 图标按钮 | 新增两份固定 `MyIconButton` 源码快照；原 XAML 仅改类名。使用原黑色主题 alpha 160/230、120/150ms 状态变色，按下缩至 0.8；返回按钮 26 DIP / LogoScale 0.87 / Opacity 0.6，清除按钮 30 DIP / LogoScale 0.5 / 右侧 32 DIP。事件及回弹曲线为 WPF 适配，额外保留键盘操作 |

仍有差异：版本列表与文件目前共用一个设置；普通组件/版本 JSON 的竞争延迟仍为自动 1.5 秒、手动 5 秒，未逐项套用 PCL 全部预算。请求/空闲超时仍主要 45 秒，因此手动列表等待预算也可能被提前失败触发；低速阈值、每源重试及按主机的连接限制没有照搬整个 PCL 来源统计模型。前置资源索引检查仍在内容下载前完成，以维持非法路径/声明在下载前拒绝的约束；组件准备需等待原版客户端与支持库，不等价于 PCL 每一种独立组件下载的全部并行调度。

视觉上仍保留两页布局，长列表超过 800 DIP 的卡片动画仍为 150ms，箭头和图标回弹使用 WPF 缓动，尚未逐段移植原动画系统。图片为本工具 96 DPI 离屏示例，未与实际 PCL 跨 DPI 逐像素验收，也未做同条件冷缓存速度比较。

## 0.15 视觉与下载主流程对照

本表是对固定 `5ef81e4` 的核心路径核对，不是仓库所有行为的等价证明。0.15 直接修正了可明确复现的视觉与来源路径差异；继续保留下载、设置两页的产品范围。

| 项目 | PCL 基线依据 | 本工具当前状态 |
|---|---|---|
| 字体、标准按钮、顶部选项、版本行 | `Resources/Font.ttf`、`MyButton`、`MyRadioButton`、`MyListItem` | 原字体字节及三份 XAML 比对通过；事件、动画调度为适配层。没有原版运行画面的逐像素验收 |
| 安装页尺寸 | `PageDownloadInstall.xaml` 名称卡 62、组件头 40、信息起点 132 DIP | 保持相应尺寸；目录选择提示和两页导航为本工具布局 |
| 卡片阴影 | `MyCard.vb`、`MyDropShadow.vb` | 0.15 移植九块渐变绘制，替换通用 DropShadowEffect；只改类名、命名空间及四处显式转换，核验完整绘制源码。圆角 5、半径 3、悬停透明度 0.4/静止 0.07、颜色变化 90ms；下沿按实际 DPI 补一个物理像素 |
| 组件折叠方向 | `PageDownloadInstall.xaml` 七类卡片的 `SwapLogoRight=True`；`MyCard.IsSwapped` | 0.15 收起角度 270°、展开 180°、箭头转动 250ms；分类卡保持默认收起 0°。悬停时箭头与标题一起变色 |
| 输入框状态 | `MyTextBox.RefreshColor` | 0.15 补齐错误淡红底色，焦点 10ms、普通悬停/恢复 100ms、错误/禁用 200ms；透明名称框仍保持无底色 |
| 任务图形与数字 | `PageSpeedLeft.xaml.vb` 动态创建任务行 | 0.15 使用原等待/完成/失败路径，行高 26、状态列 50 DIP、完成 15×16、失败 15×15，子阶段百分比向下取整；已知处理器阶段进度不再因没有文件数量而显示省略号 |
| 任务页结构 | PCL 左侧统计与动态任务卡 | 保留本工具下载页内的四项统计、五类阶段和传输详情；整体布局及总进度权重没有照搬 PCL 的多加载器结构 |
| 返回/清除按钮及页面动画 | 安装页的 `MyIconButton` 黑色主题、原动画系统 | 仍复用本工具 CaptionButton；悬停、按下和缩放尚未完整移植。长列表超过 800 DIP 的展开仍统一 150ms，未实现 PCL 的分段匀速/缓动；箭头缓动曲线仍为 WPF PowerEase |
| 文件类别来源映射 | `DlSourceAssetsGet`、`DlSourceLibraryGet`、`DlSourceLauncherOrMetaGet` | 0.15 修正 piston-data/piston-meta 的类别路由：asset→assets，library→maven/libraries，meta/client→根路径；官方顺序与查询参数保留。映射与回退由真实回环 HTTP 覆盖，未声称每个 CDN 路径均做公网可用性鉴定 |
| 自动选择来源 | `DlClientListMojangMain`：官方列表成功耗时小于 4 秒时设置 `DlPreferMojang` | **仍有差异**：Automatic 目前固定镜像优先，没有这项会话内自动切换；应作为后续主流程对齐重点 |
| 版本列表与文件偏好 | `ToolDownloadVersion`、`ToolDownloadSource` 两项设置 | **仍有差异**：本工具共用一个偏好；元数据备选延迟为自动 1.5 秒/明确优先 5 秒，没有逐项复现 PCL 各加载器的等待预算 |
| 原版任务并行 | `McDownloadClientLoader` 的支持库/资源子组合 `.Block=False`，`McInstallLoadersGet` 继续组合组件任务 | **仍有差异**：本工具先下载共享文件，再暂存客户端，再安装组件；目录写入锁、连接与速度预算按当前串行阶段设计。文件批次内部并发，但不等价于 PCL 的跨阶段并行 |
| 连接与动态分段 | `NetTaskThreadLimit`、`NetFile.TryBeginThread` | 采样与 60/40 分段已参考 PCL；本工具总连接上限 64、每主机 16、单文件 16，PCL 使用全局线程与来源状态调度，约束不同；不能据此推断同等速度 |
| 超时、低速和失败重试 | PCL 下载线程按平均连接时间/来源失败次数计算 15–30 秒等待，并检查收包间隔/速度 | **仍有差异**：本工具请求/空闲等待以 45 秒为主，每源两次、400/800ms 退避，低速切源采用 12 秒宽限及平均速度阈值，未移植整个来源统计模型 |
| 文件校验、续传与提交 | PCL 既有文件检查、Range 恢复及合并 | 本工具保留整文件大小/哈希校验，额外提供分段日志与单连接持久续传、私有安装缓存、最终实例原子发布；这些额外机制继续保留 |
| Java 与安装执行层 | PCL 的 `SelectOrDownloadJava`、ForgeInstaller/JavaWrapper | 本工具发现/选择本机 Java，自行解析并执行客户端处理器；没有自动下载 Java，也没有打包 PCL 安装器二进制，不能称为所有历史组合等价 |

上轮待办中的自动选源、共享预算下的阶段并行和图标按钮已在 0.16 更新，具体边界见上节。长列表动画仍待继续核对。本轮图片是本工具的离屏示例数据渲染，不是 PCL 原程序截图或跨 DPI 人工验收。

## 首版与完整 PCL 引擎的区别

- 这是依据 PCL 代码重组出的独立 VB 实现，没有直接嵌入完整 PCL、MeloongCore、登录模块或更新器。
- 下载使用异步有界工作队列；并发上限 8/16/32/64，每主机最多 16。0.2 已替换固定四段：从一段起步，按速度基线与准备/传输连接数决定追加，切分最大剩余区间。单文件最多 16 活动连接、128 个持久区间，均校验 Content-Range。
- 大文件分段记录可跨进程保留，最终文件始终重新校验哈希。0.14 补齐普通单连接续传，要求已知大小和有效的声明哈希；无完整校验依据的文件仍从头下载。未声称已经达到 PCL 的实测速度。
- 0.8 修正独立实现中的三处缺陷：同一传输缓存由一个任务持有到提交完成，解决不同目标路径争用缓存；按明确大小或哈希接受合法零字节文件；验证续传记录结构后再恢复，异常结构只清理已知分片并重新下载。其复现、取消/重试及修复前后证据见 `VALIDATION.md`。
- 0.14 重新核对固定 `ModNet.vb` 下载线程（约 917–977 行）：PCL 按 `DownloadStart` 发起后续 Range、检查返回长度，不支持时走回退。本工具按这一流程独立增加单连接恢复：保留现有传输缓存的 `complete.tmp`，仅在大小和 SHA-1/MD5 声明完整时追加，严格核对 Content-Range 单位、起点、终点、总长及已声明的响应长度，最后校验整文件。HTTP 200 直接使用完整响应覆盖；错误区间、通配范围和 416 不追加，重新请求完整文件。短连接按 IO 中断保留残片；完整文件哈希错误则清除残片重下。跨引擎/安装暂存目录的持久恢复、完整临时文件复用和取消后等待所有工作结束是本工具的适配实现，不宣称直接移植 PCL 的全部缓存生命周期。BMCLAPI 仍不同时分段；允许续传时也只使用一个连接。
- 0.15 的来源分类映射对应 `ModDownload.vb` 的 1250–1301 行；卡片阴影新增固定快照 `Controls/MyDropShadow.vb`，任务行补充运行时快照 `Pages/PageSpeedLeft.xaml.vb`。`import-pcl-ui.ps1` 可重新生成适配阴影类，`verify-pcl-ui.ps1` 逐字核对经过限定替换后的完整源文件及三类任务路径；不是仅按截图调整阴影。
- 0.9 以共享排程替代累计字节除以引擎存活时间的限速方式。PCL 原代码每 100ms 增加额度，本工具额外限制空闲额度为 100ms；不直接移植阻塞线程的 16ms 轮询。限速控制内容读取/落盘后的节奏，单连接和分段使用同一入口；不是逐毫秒的网卡流量整形，64 KiB 应用块和系统网络缓冲仍可造成短时峰值。不同安装阶段顺序启动批次，当前 UI 不允许下载中改限速参数。元数据请求不计入内容速度上限。
- 0.10 继续核对 PCL `McDownloadForgelikeLoader` 的支持库清单与已有文件复用、`McInstallLoadersGet` 的临时目录合并，以及 `McDownloadClientLoader` 写入 `clientVersion` 的行为。独立实现额外收紧发布范围：仅复制最终配置引用且已检查的运行支持库，处理器依赖留在私有工作目录；正确的共享文件不再替换，失败清理自己的临时副本。补全前核对已有资源索引、日志文件声明和可选的 `clientVersion`；新实例直接以同卷目录重命名提交，失败仍由原暂存清理流程回收。这些保护属于本工具补充，不声称 PCL 原代码使用相同的提交方式。
- 0.11 对照固定 PCL `ModDownloadLib.MergeJson` 默认追加数组的行为，取消本工具误用的 `MergeArrayHandling.Union`，保留重复 JVM 选项及游戏参数值。另以 `git show 5ef81e4:Plain Craft Launcher 2/Modules/Minecraft/ModLaunch.vb` 核对 `McLaunchArgumentsGame` 同时读取旧式字符串和结构化数组、`McLaunchArgumentsJVM` 对无新式 JVM 参数的四项回退；混合格式不再压平条件对象或删除 JVM 段，新 JVM 段出现时把该回退保留下来。库覆盖键补齐扩展名，统一隐式/显式 jar。HMCL 当前本地 `Arguments.merge` 和 Prism `LaunchProfile.applyAddnJvmArguments` 仅辅助确认顺序追加逻辑，没有移植其代码或许可相关实现。
- 0.12 继续参考 `ModNet.LoaderDownload.CheckExistingFiles` 在复用前检查文件、复制失败回退下载的思路；新增 `VerifiedComponentCache` 是本工具独立的私有内容缓存，不是复制 PCL 的完整缓存层。仅对上游声明 SHA-1/MD5 的安装下载和暂存客户端启用，内容键与 URL/文件名分离，读取及临时副本均校验，原子提交；其他所选游戏目录只读。普通资源对象沿用原复用机制，不重复缓存。成功发布客户端后释放额外副本，取消或失败时保留；可选缓存的 IO 故障不阻断已校验下载，缓存复制支持取消。
- 0.13 再次核对固定 PCL `ModDownloadLib.ForgelikeInjector`（930 行起）及 `ForgelikeInjectorLine`（1028 行起）：原版通过 Java 安装器执行处理器，以校验/处理日志更新进度；这份 VB 源码本身不包含 Java 安装器内部的完整缓存实现。辅助阅读本地 HMCL `HMCLCore/src/main/java/org/jackhuang/hmcl/download/forge/ForgeNewInstallTask.java` 的 `ProcessorTask.execute`（75–116、156 行起），确认非空输出列表、全部摘要匹配才可跳过，执行后仍逐项校验。本工具用 VB 独立实现这一判断并复用自己的已校验复制服务，以独立 `processor-cache` 保存声明的结果；没有移植 HMCL Java 代码，也不放宽哈希校验。额外限制输出在私有工作目录内，按 Java 工作目录解析相对路径，复制结果与下载依赖分别存放。实测 NeoForge 21.1.77 没有声明处理器 outputs，不适用这一跳过优化；不从命令行参数或文件名推测输出校验值。
- 元数据使用官方 v2 manifest（包含版本 JSON 的 SHA-1），文件源映射遵循 PCL；0.2 实现延迟竞争（自动模式 1.5 秒、明确优先源 5 秒）。内容文件逐源重试，持续低速且有备选源时提前切换；最后来源保留低速可用性。用户主动限速时不触发低速淘汰。
- 新实例完成前保留在本应用暂存目录；共享文件使用临时文件及原子提交。补全只接受与所选官方版本一致的原版元数据，不改 PCL 版本偏好。
- 固定为当前 Windows 的 x64/x86 文件规划。0.3 已加入加载器，但没有宣称支持所有历史版本或 ARM64；已执行的真实安装组合见 `VALIDATION.md`。

PCL-CE、Prism 的代码用于比较原版数据及实例组织；HMCL 另辅助核对上述处理器规则和 0.11 参数顺序。本项目主要参照仍为上表的固定 PCL 代码。

## 可复查的上游快照

`reference/PCL/manifest.json` 记录固定基线与导入源码 SHA-256。`tools/import-pcl-ui.ps1` 从本地 Git 对象重新生成这些快照、原版 XAML、调色板、图形资源和字体。`tools/verify-pcl-ui.ps1` 验证三份控件 XAML 只改类名、十份方块图标字节相同。

0.6 新增下拉框、勾选框和文本框的固定上游源码快照；比对脚本另核验下拉箭头、勾选框边框及勾选路径的原始几何参数。新选择控件是按源码重组的 WPF 适配实现，没有声称新模板与整个 PCL XAML 逐字相同；设置的即时校验及原子保存为本工具补充机制。

0.7 新增下载设置页、滑条和主题模块的固定快照；核验滑条几何、设置行高/间距/卡片内容边距及字体文件字节。卡片悬停阴影使用原 `Color4`，折叠箭头转动 150ms；任务统计以原 `PageSpeedLeft` 的 14/20 DIP 字号和 2 DIP 渐变分隔线为依据，在下载页内横向呈现四项统计，没有恢复独立任务侧栏。

事件处理与动画调度的 MeloongCore 依赖由独立 WPF 适配层替代，因此不声称把整个 PCL 控件库原封不动嵌入。卡片折叠用 150ms 缓动，按钮颜色 100/200ms、卡片悬停 90ms，参数来自原实现。

0.5 按用户明确的下载补全定位，将主导航缩减为「下载」「设置」；删除启动/联机/更多入口、对应占位处理代码和辅助侧栏。目录选择并入设置，任务进度成为下载流程中的状态。0.4 曾使用的侧栏描边图标 `Controls/NavIcons.xaml` 随侧栏一并移除。原控件 XAML、方块图标和字体仍由原比对脚本核验；标题栏只导入实际使用的下载、设置图标。新提供的两个恢复工具只做机制参照，范围和固定 SHA 见 `PATCH_MECHANISMS.md`，未移入其补丁实现。

## 跟随 PCL 当前主题

本轮仍以 `Meloong-Git/PCL` 的 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` Git 对象为准，读取并核对：

- `Plain Craft Launcher 2/Pages/PageSetup/Settings.vb`：`UiLauncherTheme` 为未加密的普通设置，默认 0；`Set` 先保存再触发主题刷新。解锁记录为另一组加密注册表设置，不属于本功能。
- `Plain Craft Launcher 2/Modules/Base/ModBase.vb`：`IniGetContent` 使用扁平 `key:value` 格式，重复键保留最后一条；`WriteIni` 定位程序旁的 `PCL/Setup.ini`。实现只打开读句柄，不调用上游可能回写默认值的设置 API。
- `Plain Craft Launcher 2/Pages/PageSetup/PageSetupUI.xaml`：主题 ID 与中文名称对应关系；`Modules/ModSecret.vb`：公开快照缺少各非默认主题的选色分支及解锁实现，因此不声称能从公开源码完整复原所有配色。

PCL-DB 对 ID 0 使用已有默认蓝色、ID 1–5 使用已有对应近似预设；实验性外观限制继续生效。ID 6–14 及未知 ID 保留可识别名称/编号，但回退本机外观。500 ms 后台采样、两次一致确认及绑定代次保护属于本程序的跨进程只读适配，不是 PCL 暴露的 API。规范化源码及摘要在 `artifacts/follow-pcl-theme/source/`、`source.json`，实现范围见 `docs/reviews/follow-pcl-theme.md`。

## 安装与任务页之间的切换动画

本轮继续读取固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8`：`FormMain.xaml.vb` 的 TaskManager 路由和 `PageChangeAnim`，以及 `Controls/MyPageRight.vb` 的 `TriggerEnterAnimation` / `TriggerExitAnimation`。任务页使用通用页面切换：退出后 110 ms 换页，再等 30 ms 入场；卡片淡出 70 ms、上移 6 DIP、间隔 15 ms，入场从 -16 DIP 开始，并行执行 250 ms Fluent 与 350 ms Back、间隔 25 ms。

PCL-DB 已有对应的 `PclSurfaceMotion`、曲线和冻结退出层，本轮仅将原版安装/浏览与任务页的两个遗漏方向接入该所有者，保留水平选择页与按钮动效的独立职责，不新建重复时钟实现。四份固定 Git 对象副本及摘要位于 `artifacts/task-progress-motion/source/`、`source.json`，说明见 `docs/reviews/task-page-motion.md`。

## 任务返回的焦点与视口

固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Controls/MyScrollViewer.vb` 在 `PreviewGotKeyboardFocus` 中明确记录 #3854：阻止 MySlider 聚焦引发自动滚动。本轮参考这一原则，并核对 `MyPageRight` 的页面交接和 `PageDownloadInstall.EnterSelectPage/ExitSelectPage` 显式滚到顶部的边界：新选版本与恢复已有安装表单需要区分。

PCL-DB 自有的任务返回保留表单流程，改为把“保留安装视口”作为当前待执行焦点请求的闭包参数，在交接结束后聚焦滚动容器；不全局屏蔽 BringIntoView，也不阻止用户主动选择名称框。该适配不是从上游复制的一套任务返回 API。四份规范化 Git 对象及摘要在 `artifacts/task-return-focus/source/`、`source.json`，验证边界见 `docs/reviews/task-return-focus.md`。

## 浮动下载按钮与页面交接

继续读取固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `FormMain.xaml.vb`、`Controls/MyPageRight.vb` 和 `Controls/MyExtraTextButton.xaml.vb`。`FormMain.PageChangeAnim` 在退出 110 ms 后换页、再等 30 ms 触发入场；`MyPageRight.TriggerEnterAnimation` 此时才设置附加按钮 `Show=True`，按钮再执行自身延迟 50 ms 的淡入和并行缩放。

本程序保留已有按钮曲线和输入门控，由现有页面控制器发出一次正文入场事件，把待显示按钮交给该页面所有者；正常入场时运行按钮曲线，中途结束页面动画时直接呈现终态。无需第二套页面计时器。三份 Git 对象副本及摘要位于 `artifacts/install-action-handoff/source/`、`source.json`，详见 `docs/reviews/install-action-handoff.md`。

## 浮动下载按钮内部按压反馈

读取固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Controls/MyExtraTextButton.xaml.vb`、`Modules/Base/ModAnimation.vb` 和 `Controls/MyPageRight.vb`。附加按钮内部 PanScale 按下时并行执行 800 ms Strong Fluent（到 0.85）与 60 ms 默认 Fluent（减少 0.05），长按终点为 0.8；释放以默认 Back 300 ms 恢复，移出以默认 Fluent 500 ms 恢复。Strong=4、默认 Fluent/Back 使用 Middle=3，60 ms 微缩不是线性。

本程序改用继承标准 Button 的 PclInstallButton，沿用原模板及原生点击/自动化入口，仅为 PART_Press 接入独立缩放；外部 Show 仍由既有控制器管理。手势取消、键盘适配和自动化门控属于本程序适配层，未声称上游有相同的 WPF Button 子类。三份规范化 Git 对象及摘要在 `artifacts/install-button-press/source/`、`source.json`，详见 `docs/reviews/install-button-press.md`。

## 筛选栏与外层页面的滚轮归属

继续读取固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Controls/MyScrollViewer.vb`、`Pages/PageDownload/Resource/PageDownloadCompDetail.xaml(.vb)`。保留 PanFilter 的单行 Horizontal/Left/Top 布局；参考 MyScrollViewer 的预览滚轮处理和对嵌套控件让出输入的原则，修正本程序已有横向溢出视口与外层竖向滚动之间的冲突。

仅在来源链中遇到实际横向溢出的 PclFilterScrollViewer 时，外层不在预览阶段提前接管，让子视口的冒泡处理器完成滚动。未溢出时继续使用原有竖向滚动曲线。横向兜底仍属于本程序适配，未声称 PCL 原有 PanFilter 带同样的视口。规范化源码和摘要在 `artifacts/filter-wheel-routing/source/`、`source.json`，详见 `docs/reviews/filter-wheel-routing.md`。

## 整合包详情的复制与网页失败恢复

读取固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Pages/PageDownload/Resource/PageDownloadCompDetail.xaml(.vb)` 和 `Modules/Base/ModBase.vb`：详情按钮调用 ClipboardSet / OpenWebsite；前者四次重试、间隔 200 ms，后者浏览器失败后复制网址并提供手动访问提示。本程序据此为原直接系统调用增加异步限次重试、结果反馈与失败边界。

项目身份/代次失效、页面门控、卡片内反馈和异步延迟属于本程序适配；既有页面动画和安装任务继续各自管理。规范化 Git 对象与 SHA-256 在 `artifacts/project-action-recovery/source/`、`source.json`；详见 [项目操作验证](docs/reviews/project-action-recovery.md)。

## UVMC 来源标签与 2.0 说明

后续文案收口以固定 PCL `5ef81e4` 的 PageSetupSystem / PageSetupLaunch、PageDownloadInstall、PageOtherAbout 为主要参照，保留必要警告、来源选择和愚人节原文。CE `915ac29` 的中文资源与来源标签作补充参照。详见 [精简范围](docs/reviews/concise-ui-copy.md)，本轮未修改安装或动画实现。

UVMC 来源标签另参见 [版本行对照](docs/reviews/uvmc-source-tags.md)：读取 CE `915ac29` 的 `Controls/MyListItem.xaml.cs`（PanTags / Tags）、`Pages/PageDownload/ModDownloadLib.cs`（来源标签）、`PCL.Core/Minecraft/McVersionClassifier.cs` 及 zh-CN 资源；结合固定 PCL 原版本行与 GetMcFoolName，保留本项目三色显示名和独立版本身份。源码副本与 SHA-256 存于 artifacts/uvmc-source-tag/source.json。

## 外部启动器目录接入第一批

后续原生隔离适配见 [第二批报告](docs/reviews/external-launcher-native-isolation.md)：固定 PCL `5ef81e4` 的 PathIndie / ModModpack 为主线；核对 HMCL `0d20910` 的 computeRunDirectory、shouldIsolateNewInstance、LegacyGameSettingsMigrator、实例布局和 game patch；核对 CE `915ac29` 的 McInstance.PathIndie、Config 的 Local / Instance 来源和 YamlFileProvider 回退。本程序自行实现只读、有界的配置解析与暂存配置合并，不调用上游迁移写入服务。源码及摘要另存 artifacts/native-isolation/source.json。

PCL 继续参考固定 `5ef81e4` 的目录扫描和 Setup.ini 规则。另读取本地 HMCL `0d20910ea302a907d6a01fec9244ab10d2993d5e` 的 SettingsManager、GameDirectoryManager、LegacyConfigMigrator、PortablePath、JsonSchema 与仓库布局；以及 PCL-CE `915ac29795efbdf6373417389e181e1504117048` 的 Paths、States、ConfigService、文件提供方和 ModFolder。原配置只读，解析逻辑采用本程序的有界实现，不调用会迁移/保存的上游配置服务。

多份绑定、旧偏好迁移、按来源保留共享根目录、冻结任务目标及阶段能力拦截属于适配层。源文件摘要、邻接仓库状态和离线回执见 `artifacts/external-launchers/`；范围及第二批隔离工作见 [目录接入报告](docs/reviews/external-launcher-directories.md)。

## 0.3 数据接口补充

- [Fabric Meta](https://github.com/FabricMC/fabric-meta/blob/master/README.md)：读取版本专属 loader profile，合并 vanilla 参数并补全支持库。
- [Forge Installer](https://github.com/MinecraftForge/Installer)：读取官方安装器的 install_profile/version.json；执行 client 处理器并校验声明的输出，排除 server 处理器。
- [Modrinth 版本接口](https://docs.modrinth.com/api/operations/getprojectversions/)：Fabric API 按 Minecraft/Fabric 过滤、读取文件大小与 SHA-1。
- [OptiFabric 的 CurseForge 项目](https://www.curseforge.com/minecraft/mc-mods/optifabric/files/all)：与 PCL 相同的项目 322385。本地 MCIM 元数据请求返回 503 时，使用 CurseForge 网站的公开文件列表接口；网站按 pageIndex 翻页，MCIM 按 index 偏移。公共网站列表不提供哈希时，检查大小和 JAR 格式，记录这一限制，不伪造 SHA-1。
- `tools/java/OptiFineBridge.java` 是本项目的小型反射调用桥，调用下载的官方 OptiFine Installer.doInstall，安装位置明确指向临时目录。没有打包或修改 OptiFine、Forge、NeoForge 安装器。
