# 旧资源发布阶段的散列取消

基线 9dd5ff07314fd36194f0f6eaeb781d4d0699b035。本轮聚焦 LegacyResourcePublication 的发布阶段，保留 VB WPF、VB 测试、net48 及程序集／接口／配置／资源身份，不打包发版。

## 问题与最小修复

Publish 在验证入口及逐文件匹配中使用不带令牌的 Matches。取消只能在摘要完成后被观察；特别是新共享 Mods 已移动且保持读租约时，仍会先完成整份目标摘要才退出。

原 internal Validate 四参签名及其 None 行为保留，把原正文收进不同名 private ValidateCore，Publish 单独传入既有 token。采用不同名避免旧反射 GetMethod("Validate", NonPublic) 出现重载歧义。除了入口目标校验，原 Publish 的目标复用、暂存源及新建 held 目标三处 Matches 接入同一 token，没有扩展公开／Friend 接口。

## 逻辑与冲突边界

- 原有 eager 目标集合、目录／祖先路径冲突和内容验证顺序保持，不在布局验证之前新增取消检查。结构冲突仍可能先于取消；并发摘要错误则沿既有 Matches(token) 的取消语义，不声称所有错误优先级都不变。
- 隔离新安装的原早返保持，整个 payload 仍由后续 Directory.Move 发布。已有实例补全走原子 CommitFile，已经写入且校验完成的修复文件保留供重试。
- 新共享安装仍不覆盖已有不同文件。每个成功移动的自建文件记录在原 _created 中，held 目标在短期提交内保持原读保护；取消后 Dispose 先释放租约，再反向检查并回收仍匹配的自建文件。已有文件或被外部改变的内容不会被当作任务自建文件删除。
- 回滚 Matches 必须保持 None：取消不能让必要回收半途退出。Complete、成功后的缓存清理、guard 顺序与所有权记录不变；不把发布中取消等同于撤回已完成的实例。
- 此次只修 Publish 内部。初始 MinecraftInstaller.Validate 与 SharedMods.Preflight 仍调用兼容的 None 入口，后续分别审查；没有将所有 Matches 一次替换。

## 源码依据

以 PCL2 固定 5ef81e490ae46b1100d61e33d0183094c7a80ca8 的旧资源映射、PathIndie 与任务取消归属为核心，C# 重点参考 CE 915ac29795efbdf6373417389e181e1504117048 的资源目录、临时校验／晋升和租约释放。固定 Git 原字节、行号、摘要与独立阶段审查见 artifacts/legacy-publication-hash-cancellation/audit。

上游提供布局、阶段和资源归属依据；本工具的冻结声明和所有权回滚是既有实现。CE 的 Task.Run(checker.Check, token) 不证明已经运行的 checker 会按块取消，本轮复用本工具已有 Matches(token)。

## 验证与限制

专项分别定位发布入口 Validate／ValidateCore、repair 目标复用、暂存源及新建 held 目标的实际生产散列；不靠第 N 次摘要猜测阶段。回滚摘要单独记录，验证取消后依然完成全量匹配和自建文件回收。另检查 repair 已完成修复、Complete 后迟到取消、原文件冲突、错误源摘要与实际安装取消／重试。

新增 55 项：四阶段 32、双文件 repair 6、Complete／冲突 6、实际安装 8、探针恢复 3；原 8167 项保留，全量增至 8222。同一新宿主配旧 DLL 为 49/55，六项预期早停失败，目标均读满 524307 字节／129 块；修复后 55/55，六目标均在 65536 字节／1 块停止。新建 held 目标取消后，回滚仍独立完整校验 524307 字节并释放租约、回收自建文件；不能把该必要回滚读取量当作前台取消失败。

实际安装还核对了一个重要边界：只有 resources/* 映射进入本发布器，assets/virtual/* 与 hash 对象由 assetFiles 直接下载至共享根目录。取消保留这些已完成的内容，资源映射未发布，实例和新设置不出现，原用户资源与配置保持；同名重试使用完整缓存，新增网络字节为 0。专项没有新增 guard 调用顺序、原 None 包装和隔离早返的独立运行矩阵，这些由源码逆向与既有全量回归核对。

最终验收包括当前 VB 全量、原 Beta 8 编译宿主、当前 C# 原始／规范 API 和资源／配置比较、Release／VS2026 Debug，以及同一冷目录单 EXE 启动／兼容和两依赖／七份根文档摘要。实际计数、进程、输入／源码摘要与提交树绑定在 artifacts/legacy-publication-hash-cancellation/validation.json。原 Beta 8 严格元数据总比较保留两条历史 Friend 新增差异，不把原宿主通过冒充精确元数据相等。

验证使用受控临时文件、测试安装器和局部 HTTP 夹具，不证明任意真实上游安装器、游戏运行或网络／取消延迟／吞吐量。没有用户实例或外部配置写入；本轮不改 UI，不另跑完整 UI／motion／搜索专项。未打包、推送、发版。

## 下一步

再检查初始旧资源验证和共享 Mods 预检如何传入取消而保留兼容入口，同时继续保留布局冲突和首次失败的顺序，以及成功／回滚收尾的非取消边界。
