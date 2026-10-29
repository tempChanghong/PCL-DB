# 归档与非覆盖文件最终校验的取消

基线：`1a85a30bcdb08e8177e398a468c7f031b39acf34`。Beta 8 后续源码，尚未发行。

## 复现与修复

上一批修复了 overrides 的最终 SHA-256 复校验。本批检查相邻路径，确认非覆盖文件仍通过无 token 的 `SafeFiles.Matches` 整文件散列；CF／MR 最后一个文件散列期间取消，直接 `WriteLedger` 仍会返回。归档获取入口 `ModpackArchiveInstaller.AcquireAsync` 在最后一次缓存校验前检查取消，校验后却直接返回路径，导致这次散列期间取消也可能返回成功。

受控基线复现中，两个平台归档均读满 524680 字节／129 块后返回成功；CF 文件读满 524307 字节／129 块，MR 的 SHA-512 和 SHA-1 合计读完 1048614 字节／258 块。四条链路都从首块开始请求取消，测试不依赖计时或新 API 缺失异常。

保留原 public `Matches(path, item)` 签名、参数名和行为，以 `CancellationToken.None` 转发到新增 internal 三参数重载。不能取消时仍使用原 `ComputeHash(stream)` 路径，保留原 provider 的读取／复位行为；可取消时按 64 KiB 分块，块前、EOF 后及结果返回前检查原 token。

大小和空文件规则、SHA-512 → SHA-1 → MD5 → S3 单分段 ETag 的顺序、多摘要必须全部通过、大小写不敏感比较均保留。分块完成后先取摘要副本，再 Initialize，匹配 `ComputeHash` 的复位语义；S3 继续使用同一 MD5 对原始内部摘要字节二次散列，不对十六进制文本散列。

只在两个入口接入新重载：非 override 的 `ValidatePayload` 最终校验、`AcquireAsync` 最终缓存校验。路径、共享缓存、锁、隔离、ledger、原子发布、回滚和既有实例配置不改。取消不删除完整缓存，重试可直接复用。

## 固定源码依据

- PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`：`Modules/Minecraft/ModModpack.vb:112-115,209,405` 传递任务取消；`:435-436` 为 MR 建立 SHA-1 FileChecker。`Modules/Base/ModNet.vb:1474-1506,1531-1537` 保留文件状态／中断边界及先大小、后摘要的复用检查。没有将其 FileChecker 误称为已实现块间取消。
- PCL-CE `dev` `915ac29795efbdf6373417389e181e1504117048`：`PCL.Core/Utils/Hash/SHA1Provider.cs`、`SHA512Provider.cs`、`MD5Provider.cs` 及 `IHashProvider.cs` 的可取消流哈希。取其令牌语义，保留本项目 net48 实现，不移植现代 `HashDataAsync`。

固定 Git 原始字节及摘要见 `artifacts/file-match-cancellation/source/manifest.json`。

## 兼容增量与验证

本批是明确的内部接口增量：979 个原公开入口保持，1244 个原友元入口全部保留，新增一个精确的 internal `Matches(path, item, token)`，友元总数 1245。原严格迁移检查会如实报告这一个 Added 并返回失败，未修改该工具；另用专项验收重算快照，仅接受这一完整签名及其原始 flags，其他 Missing／Added、初始化、资源和 JSON 差异必须全部为空。正控制及 12 个负控制验证该验收边界，不将其写成严格零差异。

冻结的同一测试 EXE 分别加载旧／新 Core，仅在原有入口执行 integration 子集：候选通过 31 项，旧 Core 在四个真实取消点失败，未将 MissingMethodException 当作复现。候选四条链路均首 65536 字节／1 块抛出原 token，归档不返回成功路径。全部专项 87 项还覆盖各摘要、S3 外层取消、正常／空文件、篡改、最后块取消、无摘要规则、大小早拒、预取消、旧两参数行为、缓存／ledger 保留、句柄释放和无网络重试，并显式恢复六个算法别名及进程内回调。

最终 DLL 重跑 87 项专项及完整 VB 7597 项（原 7510 + 新 87），旧 Beta 8 已编译宿主 7305、严格差异及精确增量验收、两资源／15 个 JSON 样本、VS 2026 Debug 和串行单 EXE 冷启动／搜索，以 `artifacts/file-match-cancellation/validation.json` 为统一回执。绑定源码、实际 DLL／EXE 和提交树；套件重叠不累加。

## 搜索动画检查的采样修正

首次单 EXE 搜索检查在第 3082 项后，因自动分类搜索的固定 1700ms 采样失败。保留原回执，再用同 Core 的只读诊断副本记录各子条件，完整诊断运行通过 3240 项。随后用同一 private App EXE、仅替换旧／新 Core 执行原 fixture：旧 Core 同断言失败，唯一 false 为卡片不透明；基值为 1、实际值为 0，结果入场时钟仍正常 Active，时间约 0.132 秒，未暂停。新 Core 通过 17 项，其同一采样点也仍有 Active 时钟（约 0.361 秒）。这是实际观测到的固定采样与自然入场未同步，不作为所有历史动画失败的统一根因或环境归因。

只修改 `MainWindow.ResourceSearchRepairChecks.vb` 的等待方式：自动／快速切换／缓存恢复等固定 1200/1700/2000ms 等待改为观察请求结束、loading Idle、所属三个时钟释放及内容应用事务结束，沿用现有 `WaitPackUiAsync` 的 5 秒期限。原会话、可见性、Opacity=1、输入门控等断言逐字保留，且不放进等待条件；不调用 Finish／Seek，不改运行时动画。卡住的请求／时钟会超时，时钟结束后错误的透明度或门控仍会使原断言失败。刻意的 30ms 快速反转和旧回调延迟用例保留。

这是 fixture 正确性修正，不保留原固定时间点作为端到端性能门槛，也不据此宣称帧率或速度提升。首失败、原样诊断、旧 Core 对照和修正后的验收均保留。只重建 App 后核对 Core／测试宿主字节仍与已通过回归相同，复用这些明确绑定的 Core 证据；新的 App 重新执行 Debug、单 EXE 和完整搜索检查。证据见同目录 `ui-review`／诊断回执及统一 `validation.json`。

## 范围与限度

其余 DownloadEngine、组件缓存及配置散列调用仍保留原入口，不宣称所有文件校验已可取消。原生配置只有读取边界，不能据此断言序列化后必定不超过该大小。本批不能抢占已进入的单次同步 Read，不承诺固定毫秒延迟。

受控 HTTP、临时文件及隐藏 WPF 不代表完整在线安装、游戏启动、人工 F5 或桌面帧率验收。未打包、推送或发版。
