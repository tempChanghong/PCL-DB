# 整合包最终校验期间的文件改写窗口

基线 `6bd9f3cc57d76d90e95c86695c364bfb71173853`。本轮检查并行执行之后的配置／发布边界，确认并修复整合包最终校验的一处实际并发缺口；不迁移界面、不改变账本或隔离格式、不打包发版。

## 实际复现

CurseForge ZIP 与 Modrinth MRPACK 的 `ValidatePayload` 原来逐文件校验后立即关闭读流。在第二项 override 散列的回调中，可改写已通过校验的第一项；真实公开 InstallAsync 仍成功，Directory.Move 发布了改写字节，MR 账本却保留原摘要。不是只调用模拟验证函数，也没有生产测试钩子。

正式旧 DLL SHA-256 为 `87F0F5241E90D6D1E5D89F36FD8F0B7DBFB3718D0956A87F4569BFC0729ECC50`。同一冻结探针执行四例：两格式正常控制均发布原字节，两格式并发改写均发布错误字节；两次实际校验摘要仍符合原内容。原始结果、输入／源码 manifest、进程与 CryptoConfig 恢复凭据见本地 `artifacts/installer-publication-review/publication-bug-audit.json`。

## 最小修复与兼容边界

在 MinecraftInstaller 增加 private `StagedPayloadLease`，仅在 pack 非空的新实例最终发布段启用。overrides／原生配置写入完成、共享 Mods 发布之后，先安全枚举当前自有 stage 的既有文件，以 FileShare.Read 持有短期读句柄，再运行 WriteLedger 及原 runtime／configuration／native 最后复验。读者仍可访问，被持有的文件拒绝写入、删除和重命名。

选择整个当前自有 payload，而非新增跨类型的文件清单接口：生成的版本 JSON、client 和隔离配置也在该阶段只读。普通新建、已有实例补全、外部共享目录不进入这次扫描；没有新增公开／Friend 业务入口。它可能更早拒绝私有 payload 中已有的占用或读取失败，不承诺原错误的先后次序不变。

遍历使用显式栈，逐层枚举；每个目录进入前和文件打开前沿原游戏 root＋stage 相对路径验证祖先，并显式拒绝 target reparse point。没有先递归跟随 junction 再校验，也没有新增固定文件数／容量限制。每目录／项及最后取得句柄后检查原 token；构造失败或取消先释放已取得句柄再重抛。Dispose 本身不取消，确保先释放再沿原 finally 清理私有目录。

Windows／net48 的独立矩阵（含与 App 相同 manifest 的复验）确认：持有子文件时，三种共享方式下 Directory.Move 父目录均失败；关闭句柄后同一次 Move 成功。Read 拒绝写／删／文件重命名且允许读取；Read|Delete 会放开删除／重命名，不能用它伪装身份保护。矩阵、运行环境、异常和进程记录在 `boundary/`。

因此 lease 紧邻 Directory.Move 之前结束，原外层 root／runtime／configuration guard、提交、Complete 和成功后的不可取消清理保留。**最后 Close→Move 的非协作外部改写窗口仍存在。** 这不是完整目录快照或跨重命名的文件系统事务：只保护实际打开的既有文件，不冻结新增／替换目录项；WriteLedger 后新生成的账本不在本次句柄集合中。没有把本轮有限修复描述成所有发布竞态已消失。

## 源码依据

实际读取固定 PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 ModModpack.vb:112–142（带取消的解压／overrides 复制与隔离 INI 合并）；C# 重点参考 PCL-CE `915ac29795efbdf6373417389e181e1504117048` 的 ModModpack.cs:308–344、751–755。固定 Git 原字节见 source/manifest.json。本工具的 stage、账本及最终校验属于自身实现，没有把此缺口或此 lease 宣称为上游同名问题／补丁。

## 验证

新增独立 VB 回归 39 项，原测试正文保持，仅向 Program 追加 CLI／默认入口。相同新宿主搭配正式旧 DLL 为 33 通过、6 失败，恰为 CF／MR 各写、删、重命名保护；候选应为 39/39，所有断言均执行。

两格式还检查正常读取与原内容／JSON／账本／隔离发布、外部配置保留、成功后解锁；后项散列取消保留原 token、无实例及 payload／包／根锁释放、同名零 payload 网络重试；已有写句柄导致构造中途失败时，部分租约释放、原清理与同名重试。独立四例复现探针也在候选验证写入被阻而原字节成功发布。CryptoConfig 两别名及回调均恢复。

最终验收门槛：新回归 39、VB 全量 8323、原 Beta 8 编译宿主 7305、当前 C# 原始／规范 API 零差异（公开 979／Friend 1247）、资源 2／JSON 15、Release／VS2026 Debug 三 Portable PDB、同一冷目录单 EXE 启动 25／兼容 299及两依赖／七根文档摘要。原 Beta 8 的严格元数据汇总仍仅历史 Friend +4，不放宽比较；旧宿主在主全量结束后按默认 TEMP／TMP 串行执行。

实际最终状态、输入／输出／源码摘要和提交树绑定见 `artifacts/installer-publication-review/validation.json`。原始失败、复现和候选均保留，不以旧回执代替最终 DLL 的新运行。本轮不改 UI／动画／搜索，不重跑它们的完整套件；前序界面基线不是本轮新验收。夹具不代表任意真实整合包、真人帧率、F5 或实际平台完整下载安装。没有写用户实例或外部配置；未打包、推送、发版。

## 后续

继续按实际复现审查发布关系，优先确认晚取消、外部配置变化和失败后的重试；若要扩大到跨 Close→Move 或新增目录项的保护，需要另立 Windows 发布架构与兼容性方案，不能顺带宣称已覆盖。搜索部分结果与安装失败即取消同伴的策略继续分开。
