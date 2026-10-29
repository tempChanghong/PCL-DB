# 下载器校验取消收尾

基线：`f8be3c315fcbf18f72d3c51471430c9ff87f613e`。本批继续第二轮收尾，尚未发行。

## 问题与改动

上一批为归档获取和整合包终验接入了可取消 Matches，但下载器单文件事务内六处仍使用原两参数入口。真实基线测试确认：首块取消后 target、donor、复制副本和最终内容仍读满 524307 字节；完整缓存及 HEAD 长度误报路径还会继续重复校验，共读 1048614 字节。目标复用路径在最终取消前还将 Reused 增为 1。

产品补丁仅将 `DownloadEngine.Files.cs` 六个原调用改为 `Matches(..., token)`：已有目标、donor、私有复制副本、完整续传缓存、Modrinth HEAD 长度误报后的内容确认、最终下载内容校验。使用已有重载，不增加接口、状态、锁或哈希实现。

单文件传入 worker 的 linked token；批次仍在全部 worker 汇合后，以调用方原 token 重抛取消。donor／copy 的 IO 过滤不吞取消，来源重试的原条件会排除主动取消；取消不被误当成坏摘要清理缓存或网络重试。原复制副本 finally、transfer gate、批次预算收尾、校验后发布和统计顺序保持。

## 固定源码依据

- PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Plain Craft Launcher 2/Modules/Base/ModNet.vb:1474-1506,1531-1537`：在复用前检查中断状态，先大小后摘要，复制失败可回退；不能把它误称为 FileChecker 已有块间取消。
- C# 参考 PCL-CE `dev` `915ac29795efbdf6373417389e181e1504117048` 的 `PCL.Core/Utils/Hash/{SHA1Provider,SHA512Provider,MD5Provider,IHashProvider}.cs`：流哈希接受原取消令牌。本项目继续使用已验证的 net48 分块实现，不移入现代 HashDataAsync。

固定 Git 原始字节和 SHA-256 见 `artifacts/download-hash-cancellation/source/manifest.json`。

## 验证

新增 VB 专项只调用原 `DownloadAsync`，以进程内标准 CryptoConfig 包装真实 SHA-1／SHA-512／MD5，在首个 HashCore 后取消并计量实际字节。六条候选路径均首 65536 字节停止，目标取消时 Reused 为 0。续传缓存通过真实首轮下载、终验时取消来准备，不以直接伪造目录替代；cache-seed 是刻意读完数据后取消的准备步骤，不计为早停样本。HEAD 用真实测试服务器错误的 Content-Length 触发，验证不会因取消再次无长度下载。

53 项专项检查原调用方 token、早停、完成统计、目标／供体保留、复制副本清理、缓存保留、无额外 HTTP／重试、释放后 fresh-token 重试、取消后保留缓存重新校验再发布，以及六个算法别名与共享回调恢复。冻结同一 EXE 仅替换 Core 的旧／新对照，保留旧实现六处预期失败，不使用计时门槛或新 API 缺失作为复现。

最终 Core 另跑 53 项专项、完整 VB 7650 项（原 7597 + 新 53）、旧 Beta 8 已编译宿主 7305 项；API 本批严格零差异（979 public／1245 友元）、两资源／15 个 JSON 样本、VS 2026 Debug、串行单 EXE 冷启动／完整搜索，以及源码、实际 DLL／EXE 与提交树绑定，以 `artifacts/download-hash-cancellation/validation.json` 为统一回执。各套件重叠不累加。原有测试和界面代码不变。

## 限度与下一步

不能中断已经进入的单次同步 Read，`File.Copy` 本身仍同步；组件缓存和其他发布流程的旧入口尚未全部接入，不宣称所有文件 I/O 可取消。未进行完整在线安装、游戏启动、人工 F5 或桌面帧率验收，未打包、推送或发版。

第三轮尚未启动。当前收尾清单及进入条件见[第二轮收尾与第三轮门槛](core-refactor-round2-closeout-2026-10-29.md)。
