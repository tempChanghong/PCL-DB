# 组件支持库发布前的校验取消

基线：361bd248c12ac04e80cac728a5983bef52ef5a0d。范围是第三轮解释器重构后的取消收尾，保留 VB WPF 界面、VB 测试、net48、程序集／接口／配置／资源身份；不打包发版。

## 问题与最小修复

ComponentInstaller.ApplyAsync 在最终运行库计划生成后，先校验私有工作树中的库以决定是否补全，再在下载完成后逐项校验。两处原先调用不带 token 的 Matches，因此散列过程中取消仍会读取整个文件；最终检查的进度通知触发取消时，也会先散列再进入发布阶段的取消检查。

仅给这两个原调用传入 ApplyAsync 已有 token，复用原来的 64 KiB 分块散列。库计划、摘要算法、缺少 URL 的错误、hashless 文件禁用弱缓存复用、文件冲突处理、下载预算及并行分支归属不变。取消与坏摘要同时发生时，遵守既有 Matches(token) 的取消语义，不宣称任意异常优先级都不变。

两处均在 ComponentRuntimePublication.Publish 之前：取消时不发布共享运行库、不接受新的 frozen 运行库清单、不发布实例，仍由原 finally 清理该任务的私有工作树。文件流与算法沿原 using 释放；已存在的库、实例及隔离设置保留。全局实例提交后的 Complete／ReleasePublishedClient 和回滚／Dispose 不接取消。

## 源码依据与范围检查

- PCL2 固定 5ef81e490ae46b1100d61e33d0183094c7a80ca8：ModDownloadLib.vb:1139–1185、1837–1845、1860–1862 分开支持库分析、下载、迁移和最终补全；ModLoader.vb:275–287 区分当前执行与取消。
- C# 重点参考 CE 915ac29795efbdf6373417389e181e1504117048：LoaderDownload.cs:111–124、FileDownloader.cs:124–229 的复用、校验与晋升，以及 DownloadResourceManager.cs:249–269 的资源释放。
- CE 的 Task.Run(checker.Check, token) 不证明已开始的 checker 会按块响应取消。本工具沿用既有 Matches(token)，不声称复制了相同散列实现或上游也有相同私有树／租约协议。

独立审查捕获八份固定 Git 原字节与摘要；两个实参修改逆向后等于原 Git 和原工作文件，EOF 无漂移。Forgelike、Downloads、RuntimePublication、MinecraftInstaller、VerifiedComponentCache 和 Models 保持原字节。证据见 artifacts/component-validation-hash-cancellation/audit。

## 验证与边界

新增回归使用真实 legacy Forge 安装夹具，以生产异步状态机的实际调用栈定位 ApplyAsync 中的校验，并以最终进度阶段区分两处；通过既有 CryptoConfig／FileMatchHashProbe 在首块取消，无生产测试钩子。记录散列字节、取消归属、共享文件和已有设置、私有树清理、文件占用释放及新令牌重试；最终进度预取消另作控制。保留原有测试正文。

新增 31 项：两个块内取消场景各 10、最终进度预取消 8、两个 SHA1 别名与回调恢复 3。相同新测试宿主与锁定旧 DLL 的基线为 28/31，三场景均读取完整 524307 字节／129 块；修复后为 31/31，两个块内场景各读 65536 字节／1 块，进度预取消为 0 字节／0 块。三场景均保留调用方取消令牌、原共享库与既有隔离设置，未发布实例，清理私有树并成功重试；重试复用已校验安装器，安装器请求数保持 1→1。这里只测真实 legacy Forge 声明的 SHA1，不称多算法全交叉；现有 8045 项继续保留，全量增至 8076。

验收包含同一新测试宿主与旧 Core 的失败复现、最终专项及当前 VB 全量；原编译 Beta 8 VB 宿主；当前 C# 原始／规范 API、资源和配置比较；Release／VS2026 Debug；同一冷目录单 EXE 启动与兼容检查，以及嵌入的两依赖、七份根文档摘要。具体计数、进程退出码、输入 DLL／宿主／源码摘要和提交树记录于 artifacts/component-validation-hash-cancellation/validation.json。原 Beta 8 严格元数据比较仍保留两个历史 Friend 新入口差异，不把旧宿主通过冒充精确元数据相等。

本轮不改 UI 和动画，不另跑完整 UI／motion／搜索专项。以上是受控临时文件与局部 HTTP 夹具验证，不证明真实网络下载、Minecraft 运行、任意外部占用或实际取消延迟／吞吐量；不写用户实例或外部启动器配置。未打包、推送、发版。

## 后续

下一批可独立处理 Forge／NeoForge 处理器的嵌入库复用、输出复用判断及执行后输出校验；需分别验证跳过执行、缓存恢复、处理器失败与取消的先后关系。共享 Mods、旧资源发布及成功后清理继续按各自阶段审查，避免把所有 Matches 一次替换。
