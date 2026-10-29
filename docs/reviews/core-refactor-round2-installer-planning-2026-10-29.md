# 第二轮重构第四批：安装规划器提炼

基线：`6f18c9a5865a5e35dd59412ebcf3d2a764748c1a`。这批从文件分区进一步提炼职责：MinecraftInstaller 的安装交易保留完整，将不持有安装实例状态的规划实现收进私有静态 PlanBuilder；六个原公开／内部入口保留原类型、签名、参数名、默认值和直接转发。

## 最终边界

| 位置 | 职责 |
| --- | --- |
| `MinecraftInstaller.cs` | 五个状态字段、构造器、版本查询、安装入口、完整 InstallCoreAsync、并行取消、失败首因通知与进度报告。 |
| `MinecraftInstaller.Planning.cs` | 六个兼容入口和 private static PlanBuilder；后者收纳八个原规划方法，解析客户端／库／资源下载声明、规则、Maven 路径与 PCL artifact 兼容。 |
| `MinecraftInstaller.Metadata.cs` | 原 HMCL 原版 patch 适配、补充资源索引命名，以及原 ToFile 静态描述工厂。 |

四个 public 入口为 BuildBasePlan、BuildAssetsPlan、RulesAllow、MavenPath；两个 internal 入口为 ValidateResourceName、PreparePclLibraryPaths。非 void 入口直接返回对应 PlanBuilder 调用，void 入口仅调用一次，参数按原序传入，不加 clone、默认值替换、缓存、异步派发或额外操作。

八个实现保留完整声明和方法体：上述六个，加 LibraryFile、CompletePclArtifactPath。PlanBuilder 没有字段、构造器、初始化逻辑或实例对象；仍读取运行时 OS context，调用 PclLibrarySelection 和原外层 ToFile 等静态协作，不能视为与环境无关的纯函数。

ToFile 原本也被 InstallCoreAsync 两处直接调用，因此保留外层的原 private owner／签名／方法体，不增加新转发或放大可见性。这样主交易的路径锁、已有实例比较、三启动器设置保护、隔离、下载并行、取消、校验、原子发布和回滚均不需要重写。

## 固定源码依据

以 PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 为核心：`ModDownload.vb:19-63` 分别建立客户端、资源索引及支持库下载任务；`ModMinecraft.vb:1554-1594,1653-1730,1904-1912` 负责规则、支持库/natives 与 Maven 兼容，`:1967-1982,2012-2038` 负责资源索引和旧资源目标；`ModDownloadLib.vb:1837-1846` 的隔离与文件迁移属于安装执行边界。

C# 重点参考 PCL-CE `dev` `915ac29795efbdf6373417389e181e1504117048` 的 `ModDownload.cs:23-101`、`ModLibrary.cs:85-141,221-277,633-657`、`ModAssets.cs:70-90,137-170` 和 `ModDownloadLib.cs:4057-4073`。不替换本项目 Maven 语法、Newtonsoft、环境规则或 net48。精确审查见 `artifacts/core-refactor-round2/minecraft-plan-builder-review.md`。

## 证明与差分

独立 Roslyn C# 9 探针从固定 Git 原文件逐项核对全部 24 个原成员：八个实现只改变 owner，完整 token 声明／方法体哈希相同；六个 facade 保留原头部并严格只做同名、同序、无转换的单次转发；其余 16 个成员保持原 owner 与原体，ToFile 的交易／规划调用关系单独检查。候选是原外层加一个 private static 类型、合计 30 成员；这个预期增量不作为漏移或额外可见 API 接受。十个隔离负控制均检出，包括参数顺序、额外转发操作、状态／factory 遮蔽、可见性和主交易体变化。证据在 `artifacts/minecraft-planbuilder-refactor-review/`。

另外用同一冻结 net48 探针 host、配置与样本清单，在独立进程分别加载旧／新 Core，真实调用六个入口的 87 个边界样本（45 正常、42 异常），比较返回值、异常类型／消息／ParamName／HResult、JSON 入参前后及返回引用共享。初步全部一致，入参改变为零；最终 DLL 再单独执行同样差分并绑定摘要。该样本证明不代表穷尽所有元数据、架构或 OS context。

原有 VB 测试／断言不变。初步库元数据 38、库选择 32、安装输入 71、失败通知 19 项通过。完整 VB 7468、旧 Beta 8 已编译宿主 7305、公开／友元接口、资源／配置、VS 2026 Debug／Portable PDB 和最终单 EXE／搜索，及源码／二进制／提交树绑定，统一见 `artifacts/installer-planning-refactor/validation.json`。各套件重叠不累加。

公开／友元接口、程序集／资源／配置名称和 net48 保持。私有实现的 DeclaringType、源码文件、栈帧位置与生成元数据可能改变，不要求原始 DLL/PDB/MVID 或任意错误栈文本逐字相同；主交易的原始故障保留和取消身份规则不变。

WPF 检查在所有构建与 Core 宿主结束后串行运行，不调整断言或时间阈值，也不将该顺序当作历史动画复合断言原因已解决的证明。受控进程、临时实例和隐藏 WPF 不代表完整在线安装、游戏启动、人工 F5 或帧率验收。未打包、推送或发版。
