# 安装初始预检的散列取消

基线 c14dcd0bed951527a74cd67c993d027fb773c467。继续处理创建私有暂存树之前的旧资源与共享 Mods 预检，保留 VB WPF、VB 测试、net48、程序集与公开接口，不打包发版。

## 问题与接入方式

初始两处校验在私有 staging 和并行下载分支建立前执行，原入口不能接收取消令牌，校验已有大文件时会先完成摘要再继续流程。上批已修复发布内部，本批解决安装开始前的这两个独立调用点。

保留 LegacyResourcePublication.Validate 和 SharedModsPublication.Preflight 的原签名、唯一方法名与 None 行为，新增不同名 internal ValidateForInstallation／PreflightForInstallation。前者转发既有私有 ValidateCore；后者承接原预检正文，在原位置接入 Matches(token)，旧入口仅以 None 委托。MinecraftInstaller 的两个调用传其原 public caller token；不创建额外令牌或使用隐式环境状态。

## 兼容与冲突边界

- 两个类各需接收调用方令牌。新增两个命名内部入口可保留原二进制签名和按名称反射的唯一命中，不把校验逻辑搬进 Installer、重复正文或引入模式／delegate 参数。
- 公开接口保持 979 项；相对当前 C# 基线，Friend 1245→1247，仅两项新增，原项无删除或改动。相对原 Beta 8，既有历史 Friend +2 与本轮 +2 共四项。严格元数据工具仍应报告不相等；不改工具、不把这次增量说成 API 零差异。
- 保持资源 eager 目标解析、目录／祖先冲突、内容校验顺序；Shared Mods 仍先 ValidateFolders、再 Legacy 布局校验，最后只对已有且含 Sha1/MD5 声明的文件检查内容。不在布局前新增取消检查，结构错误仍可能先于取消。
- hashless 不新增早期内容校验；repair 仍跳过已有资源的内容冲突检查，后续补全按原流程执行。三种启动器、隔离、共享目录与自定义目录的判定均在这两处之前完成，本轮不改变或写入其配置。
- 此处可能已读取 metadata／索引，也可能已有根目录与安装锁，不能描述为“零网络／零文件”。在目标散列中取消应阻止后续 payload 下载和私有 staging 创建；文件流释放、根锁释放、原文件及已有设置保留。
- 发布、Complete、Dispose、回滚以及成功后清理保持原样。首个非取消失败和取消的既有归属不重写；并发摘要错误沿既有 Matches(token) 取消语义。

## 源码依据

以 PCL2 固定 5ef81e490ae46b1100d61e33d0183094c7a80ca8 的旧资源映射、PathIndie、组件 Mods 和安装任务取消为核心；C# 重点参考 CE 915ac29795efbdf6373417389e181e1504117048 的资源目录、下载校验与取消传播。具体固定原字节、行号、摘要与独立阶段审查见 artifacts/install-preflight-hash-cancellation/audit。

本工具的初始内容冲突保护、冻结声明和回滚协议为既有实现，上游路径／任务机制仅作依据；Task.Run(checker.Check, token) 不证明已开始的同步摘要会按块取消。本轮沿用既有 net48 Matches(token)。

## 验证与限制

真实 InstallAsync 专项分别取消已有匹配资源和共享 Fabric API 的初始散列，按实际生产调用链排除发布阶段，记录读取量、原令牌、请求范围、暂存布局、锁与已有文件／配置，并以新令牌重试。旧 DLL 不含新入口，直接控制通过显式 old-None-fallback 调用旧方法，避免把 MissingMethod／JIT 失败当作有效基线；实际安装红灯仍只走原 public InstallAsync。

另控制旧 None 完整读取、预取消与布局冲突顺序、Shared 目录优先、hashless 门控和 repair 跳过。最终以当前 VB 全量、原 Beta 8 编译宿主、精确接口增量／资源／JSON、Release／VS2026 Debug、同一冷目录单 EXE 和两依赖／七根文档摘要收尾。旧宿主全量保持默认临时目录，在主 VB 完成后串行运行；上批管道夹具首次超时原因未确定，不据此修改断言或超时。

新增 41 项：直接入口 12、两条实际安装链 16、兼容／冲突／门控控制 10、探针恢复 3；原 8222 项保留，全量增至 8263。同一新宿主配锁定旧 DLL，基线为 33 项通过、4 个场景失败，另外 4 条后续断言因场景首个失败未执行；不称仅有四条断言失败。修复后 41/41，四个取消目标均在 65536 字节／1 块停止。旧 DLL 直接入口为明确的 old-None-fallback，读完 524307 字节后仍成功；实际安装资源／Mods 分别读满 524307／524680 字节才退出，两个旧流程均留下已创建的空 staging 布局。

候选的实际安装取消发生在 staging 创建前，请求仅为夹具 metadata／index，payload、新实例与新设置均未产生；已有内容／时间戳／配置和安装锁释放均通过，同名新令牌重试成功。这里只证明夹具所选资源和 Fabric API 路径，不把直接回退调用说成旧 DLL 拥有新入口，也不称所有启动器组合均新增了完整矩阵。

实际计数、进程退出码、输入／源码摘要、兼容增量和提交树绑定在 artifacts/install-preflight-hash-cancellation/validation.json。验证为受控临时文件与局部 HTTP 夹具，不证明任意真实安装器、外部启动器、游戏运行或网络／取消延迟／吞吐量；不写用户实例或外部配置。本轮不改 UI，不另跑完整 UI／motion／搜索专项。未打包、推送、发版。

## 后续

再审查安装其余前置的索引／客户端复验，以及搜索、管理和下载之间的取消状态归属；只有取得独立复现依据再扩大修复范围，保留必要收尾的非取消边界。
