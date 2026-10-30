# C# Core 重构的界面与生命周期基线（2026-10-30）

## 范围与当前阶段

被测产品提交为 `8fb72e20580b062857c8f20816e9dabf69634f73`，包含最近安装预检、共享 Mods／旧资源及客户端最终复验的取消修复。Core 已迁入 C#，第二轮模块拆分和第三轮四批解析／配置解释器提炼已完成；VB WPF、VB 测试、net48、程序集／配置／资源身份继续保留。

本轮执行上次计划的第一步：在当前源码上新建完整 UI、动画和搜索基线。没有借基线验证修改运行时、验证入口、计数或时限。报告在验证结束后单独加入仓库；文档提交与被测产品提交分开记录，不声称候选 EXE 已嵌入这份新增报告。

## 本轮新验证

使用未修改的 `tools/verify-single-exe.ps1`。在同一中文／空格冷目录中，仅放同一个重命名后的 EXE；依次执行 standalone、motion、完整 UI 和 pack-search，所有套件均使用该 EXE 的实际 SHA-256。

| 检查 | 最终结果 |
| --- | --- |
| Release 构建 | 零警告、零错误 |
| 单 EXE 冷启动 | 25 项 |
| 完整动画 motion | 6011 项 |
| 完整 UI all | 7166 项 |
| 完整搜索 pack-search | 3444 项 |
| 嵌入内容 | 两个依赖、七份根文档与本轮构建／源码摘要一致 |
| 冷目录与源文件 | 运行结束仍仅有 EXE；原产品源码保持 |

策略继续为 `PCL-DB-checks/2026-10-30.21`；UI／motion 各 3600 秒，搜索 1800 秒，没有改变断言、计数或时限。套件有重叠，以上数量不可相加为独立场景总数。

最终结果路径、EXE／Core 摘要及文档提交绑定见本地 `artifacts/refactor-lifecycle-baseline/validation.json`。原始套件 JSON、stdout／stderr 和构建日志保留在其 Operation 目录。执行工具会检查各子进程退出码，但没有把各 scope 的 PID／时长对象写进最终 result；本轮只记录真实可得的总工具退出结果，不补造 scope 进程元数据。

本轮 EXE SHA-256：`A9FCFC2FD6F07CC931E7437EC2F1E37AD4F5502D0BF590200A03E6DA22343BB4`。Core SHA-256：`4B64F19153D8A2EED8BA7EF8ADD3BA10A9B5EBCDCA0AA0B5DA15976C3D6B3E0C`。被测原源码清单为 906 个文件，摘要 `5DA5D0B4F9ECA11CC5D57C2EA092788E0FA73EB80E13103B403523145AE1D97A`；新增报告不在此原清单中。

## 观察与源码依据

完整回归覆盖管理目标、本地 Mod 操作、社区资源另存为／安装交接、整合包启动、详情与前置返回、分页取消重试，以及页面／卡片／任务页的输入门控、时钟清理、布局重入和迟到操作退役。标准 pack-search 使用受控夹具；存在于源码中的实时 CurseForge 数据包探针不是标准入口的一部分，本轮不声称执行实时平台查询。

抽看新生成的 `vanilla-cards-settled.png` 和深色 `selection-motion-return.png`，未见明显裁切或卡片布局异常；两张静态截图不能证明动画自然流畅度或完整界面视觉一致性。

实际对照固定 PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`：ModAnimation.vb:753–783 同名动画先停止再替换，MyPageRight.vb:236–248 先发布 Empty 状态再停止／隐藏，ModLoader.vb:275–284 通过执行身份及取消状态拒绝旧执行。现有本地 owner／generation 与时钟断言用于保护这些交接语义，不声称上游具有同名本地修复。

本轮未发现需要修改运行时代码的回归；该结论仅覆盖已执行的套件。没有在报告中把源码职责耦合、未覆盖路径或静态截图推断为已复现 Bug。

## 既有证据与限制

`installer-final-hash-cancellation/validation.json` 是此前 `8fb72e2` 的 Core 验证：专项 21、VB 全量 8284、原 Beta 8 编译 VB 宿主 7305、当前 C# API 零差异、单 EXE／Debug。原 Beta 8 严格元数据总比较仍为失败，仅历史 Friend +4；本轮没有重跑或放宽这些比较。

本次重新构建的 Core DLL 与此前验证 DLL 的实际字节不同，旧宿主／API／VB 数量仅作为被测源码已有证据，不冒称它们在本轮新 DLL 上重新执行。报告提交只增加本文件，所有既有源文件及七份嵌入根文档逐字节保持；实际新增文档范围、提交父节点与原源码清单由最终独立审查和 validation 绑定。

验证使用隐藏 WPF、临时文件／配置和受控 HTTP 夹具，不证明真人桌面帧率、手工 F5、真实平台完整下载安装或游戏运行。没有修改用户实例、外部启动器配置、dist 或已发布工件；未打包、推送、发版。

## 下一批边界与验收

接下来可沿既定路线提炼 `MinecraftInstaller.InstallCoreAsync` 的私有并行下载执行边界（当前约 230–295 行），先保留完整安装交易。PCL2 ModDownload.vb:54–65 与 C# 参考 PCL-CE `915ac29795efbdf6373417389e181e1504117048` 的 ModDownload.cs:77–101 均提供非阻塞支持库／资源任务的阶段依据；本工具已有的配置保护和原子发布继续保留。

1. 私有执行边界只借用既有冻结输入，负责共享预算、linked token、五个引擎、分支汇合和统计；交回最终 metadata、同一个 ComponentInstaller 及原统计。无组件时保留原 metadata 引用。
2. 客户端和支持库完成后才安装组件；资源和整合包按原顺序并行。StagedFiles 仍在 assets 完成后调用，pack.Files 和组件 FileCount 在原位置读取，不提前枚举或重算。
3. 引擎、linked token、预算在 helper 内按原逆序释放；ComponentInstaller 本身不是 IDisposable，其冻结运行库描述继续交给外层最终租约。根锁、两个资源发布对象、隔离／JSON／INI guard、overrides、提交、回滚及成功清理留在原交易。
4. 保留调用方取消的原 token、所有已启动分支汇合，以及现有按分支数组／Flatten 顺序选取非取消异常的规则；不要误改成按墙钟时间抢占的“首个失败”。进度观察器异常不替换原故障。
5. 安装失败即取消同伴，搜索则允许平台部分失败；首批不统一两者，也不迁移 VB UI 或改变动画。

实施前固定旧方法与外层不变区，提取后逐项映射；固定新旧探针比较阶段事件、异常／token、统计、文件树／配置、取消清理和重试。重点保留 AdaptiveParallel、InstallFailureNotification、运行库最终租约、共享 Mods／旧资源及最终客户端取消检查，再完成 VB 全量、旧编译宿主、API／资源／JSON、Debug 和单 EXE 门槛。结构提炼与新行为修复分别提交。
