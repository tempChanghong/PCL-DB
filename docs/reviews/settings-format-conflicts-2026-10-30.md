# 保存时的绑定格式冲突保护

基线 ba56e0598f1b111cb9d5c9daca63074290cf763b。本批完成上一轮报告列出的跨版本写入边界，保留 VB WPF、C# Core、.NET Framework 4.8、程序集、配置和资源名称。

## 问题与修复

窗口载入已知格式 1 后，另一版本把磁盘 externalLaunchers 升级为未知格式。旧窗口仍按自己的格式状态生成差量或完整设置，SaveChanges 原本直接合并写入，导致未知子树被插入旧字段，显式保存甚至会把格式号改回 1。

新增回归先在原实现的真实 SavePreferenceValues 路径失败：automatic target save rejects a newer opaque format without publishing or advancing its baseline: 0，见 artifacts/settings-format-conflicts/baseline-compatibility.json。这是既有 App 保存逻辑的问题，不归因于 C# Core 迁移。

SaveChanges 现在在取得每路径保存锁、读取最新配置之后，先构造候选合并值。若最新绑定子树不是本工具认识的格式，而候选会改变该子树，则抛出可由既有保存反馈捕获的 InvalidDataException，提示重新打开 PCL-DB；不创建候选发布文件、不写入其他字段，也不推进窗口基线或迁移待办状态。

已知格式的识别与 Core Restore 对齐：对象、整数 format=1、对象 bindings。不存在 externalLaunchers 的旧配置仍可迁移。未知子树原样保留的独立修改可以保存；已读入未知格式的窗口仍可保存手动 gameRoot。显式修复损坏 JSON 的既有功能不变，但显式保存不能降级仍可正常解析的未知绑定格式。

原有两秒保存锁、临时文件、原子发布、目标成组差量和错误反馈继续复用。本批不改布局、动画、焦点、下载或安装机制。

## 源码依据

PCL2 5ef81e490ae46b1100d61e33d0183094c7a80ca8 的 Settings.vb:278–294 先保存后调用 OnChanged，ModMinecraft.vb:17–21 将目录作为一个逻辑值提交。C# 重点参考 CE 915ac29795efbdf6373417389e181e1504117048 的 JsonFileProvider.Set:85–88 按键序列化，以及 FileConfigStorage 对不兼容类型的边界处理。

绑定 JSON 和跨进程保护是 PCL-DB 自身协议，不声称上游具有同名检查，也不移入 CE 的错误项删除、异步队列或格式迁移。九份固定 Git 原字节与 SHA-256 见 artifacts/settings-format-conflicts/source/manifest.json。

## 验证与限制

新增 54 项：十种未知值形态分别验证自动拒绝、显式拒绝、独立偏好保存、已知未知格式窗口的手动目录保存、兼容恢复后重试；另两项覆盖待迁移旧配置，两项覆盖等待保存锁时磁盘升级及锁释放。拒绝时核对字节、时间戳、基线与临时文件；既有兼容测试正文保留。初次编译发现新夹具的局部 path 名称遮蔽 Path 类，已显式使用 IO.Path，未据此修改产品或放宽断言。

兼容性由 77 增至 131，完整 UI 由 6694 增至 6748。策略 PCL-DB-checks/2026-10-30.15 仅更新这两项计数，保持原有断言和超时。

提交要求：最终 Release／VS2026 Debug、VB 7984 项、当前 Core 原始／规范 API 零差异（979／1245，资源 2／JSON 15）；同一冷目录单 EXE 的启动 25／兼容性 131／完整 UI 6748，以及内嵌两依赖／七根文档摘要、验证契约。实际完成状态、二进制与提交树绑定见 artifacts/settings-format-conflicts/validation.json。

本轮不另跑原编译 VB 宿主、完整 motion 或搜索专项，不把历史结果列为新验证。全为临时配置和受控窗口，没有真实用户配置改写、手工 F5 或游戏验收。主代理自查，无独立代理审查。未打包、推送或发版。

保护范围是遵循本工具保存锁的写入；任意不协作的外部编辑器在检查后直接覆盖文件不在锁保证内。格式仍为 1 的同版本并发保留既有逐字段合并与目标成组行为。后续可继续检查其他配置子树的并发版本边界，避免扩大本批协议范围。
