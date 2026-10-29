# 组件缓存校验的取消与重试

基线：`8cf52a0c1ce65df88fe5a05e6f58a94dca3e728e`。本批继续第二轮收尾，保留 VB 界面／测试、net48 和全部程序集、资源与配置身份。

## 实际问题与修复范围

VerifiedComponentCache 的恢复候选、已有缓存和复制副本三处校验已有 token 参数，却仍调用两参数 Matches。副本复校验同时服务 Save 与 TryRestore，形成四条真实路径。旧 Core 对照中，首块取消后普通 SHA-1、处理器 SHA-512 和 MD5 路径仍读满 524307 字节；处理器缓存 S3 单分片 ETag 路径还继续计算 16 字节原始摘要，共 524323 字节。已有有效缓存的 Save 分支甚至正常返回。此处证明的是内部缓存入口忽略取消，不据此宣称外围安装错误发布了实例。

产品只在这三处传入已有 token，复用既有可取消 Matches。没有新增 API、状态或锁，不改缓存地址、摘要优先级、未知摘要的 noop、读根顺序、复制实现、提交和统计顺序。三个替换逆还原后与基线 Git 原始字节完全相同，文件维持 LF 且无 EOF 换行。

TryRestore 的可选读取／复制回退只捕获 IO、权限和参数错误，取消继续上抛；真正的目标提交错误仍在该过滤之外。Save 的同类可选缓存异常过滤保持，取消不被吞掉。两种私有副本仍由原 finally 清理，保存被中断时保留原缓存，恢复被中断时保留原目标与所有供体。单文件已经落盘后的 Save 被取消时，也不撤销那个文件；外围事务继续负责其既有发布边界。

## 发布完成后的清理

ReleasePublishedClient 保留原两参数校验，不接 token。MinecraftInstaller 先完成 publication／modsPublication，再调用这项可选清理，并按实际发布结果返回成功。迟到取消不能将已经完成的实例发布反向报告为失败。这与仍处于文件事务中的 Save／TryRestore 是不同阶段；本批不统一替换所有旧 Matches。

## 固定源码依据

- PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `ModNet.vb:1474-1506,1531-1537`：复用时关注中断，先大小后摘要、复制失败可回退；不声称其 FileChecker 已支持块间取消。
- C# 重点参考 PCL-CE `915ac29795efbdf6373417389e181e1504117048` 的 `PCL.Core/Utils/Hash/{SHA1Provider,SHA512Provider,MD5Provider,IHashProvider}.cs`：流哈希传递取消令牌。本项目继续使用已有 net48 兼容实现，保留 S3 内层／外层 MD5 复位语义，不引入现代 HashDataAsync。
- 本地基线的缓存、Models、下载器、Forge 处理器与安装器发布顺序一并固定。11 份 Git 原始字节与摘要见 `artifacts/component-cache-hash-cancellation/source/manifest.json`。

## 验证与证据边界

新增 VB 专项通过原 Save／TryRestore 入口和真实标准哈希包装器，在首个 HashCore 后取消并计量实际字节。缓存全部通过真实 Save 建立；save-copy 刻意损坏已有同长度缓存，确保取消时旧缓存及时间戳不被覆盖；恢复路径保留原目标和两份真实供体。四条候选路径均在首个 65536 字节块停止、抛出原 token，无正常成功返回。

37 项检查覆盖两种缓存命名空间、SHA-1／SHA-512／MD5／S3 ETag、取消早停、文件和时间戳保留、两种临时副本清理、句柄释放、fresh-token 重试以及六个算法别名与共享回调恢复。冻结同一 EXE 仅替换旧 Core，四条路径实际失败；不靠新入口缺失、时间门槛或大物理文件复现。

最终 Core 再跑专项 37、完整 VB 7687（原 7650 + 新 37）、原 Beta 8 已编译宿主 7305、严格 API 零差异（979 public／1245 友元）、两资源与 15 个 JSON／配置样本、VS 2026 Debug，以及串行单 EXE 冷启动 25／搜索 3240。实际 DLL、测试 EXE、两份内嵌程序集、七份文档、源码清单和提交树统一绑定于 `artifacts/component-cache-hash-cancellation/validation.json`。各套件重叠，不累加。

单次同步 Read 不能抢占；本批也不改成功发布后的清理语义。Forge 处理器、共享运行库和其他发布阶段的旧校验仍需分别审查，第三轮尚未启动，见[收尾清单](core-refactor-round2-closeout-2026-10-29.md)。未进行完整在线安装、游戏启动、人工 F5 或桌面帧率验收，未打包、推送或发版。
