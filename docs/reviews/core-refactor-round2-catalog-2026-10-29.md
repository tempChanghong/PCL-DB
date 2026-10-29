# 第二轮重构第一批：平台目录职责拆分

基线：`d3c244397d144eacca571e3bcf7f31229f1853fa`。Core 的 C# 迁移和随后几轮取消／搜索修复已有完整验证，本轮进入结构整理，将平台目录客户端从一个约 700 行文件拆成明确的职责区。VB 界面、原 VB 测试和 .NET Framework 4.8 保持。

## 最终结构

| 文件 | 职责 |
| --- | --- |
| `src/PclDownloadBridge.Core/ModpackModels.cs` | 原 ModpackPlatform 枚举，以及 Query、Project、Release、CatalogPage 四个数据类型；属性顺序、默认值、枚举值和 Copy 的防御复制保持。 |
| `src/PclDownloadBridge.Core/ModpackCatalog.cs` | 主类型与 PageSize；搜索 URL、项目查询、依赖解析和版本分页的编排。主文件缩到 163 行。 |
| `src/PclDownloadBridge.Core/ModpackCatalog.Transport.cs` | 同一客户端的三个 readonly 字段、构造器和 Dispose；HTTP／JSON 请求、鉴权主机、镜像回退、重试、超时和响应取消收尾。 |
| `src/PclDownloadBridge.Core/ModpackCatalog.Parsing.cs` | 项目／文件解析、网页地址规范化、依赖与版本标签，以及严格 JSON 字段、日期、摘要和身份校验。 |
| `src/PclDownloadBridge.Core/ModpackCatalog.LocalMods.cs` | 原本地 Mod 更新逻辑保持原文，继续复用同类型的私有传输与解析成员。 |

客户端仍为同一个 `public sealed partial ModpackCatalogClient : IDisposable`，公开 `ParseRelease` 与内部 `IsExactMinecraftVersion` 的归属和可见性不变。没有新增服务接口、反射桥接、运行库或项目；SDK 自动包含新 C# 文件，项目 GUID、程序集名称／版本、资源 LogicalName 和单 EXE 加载方式保持。

这批仅整理文件与责任归属，原有成员签名、方法体和数据类型整体搬迁。没有同时改下载算法、来源选择、文件筛选、JSON 兼容范围或异常优先级。HTTP 生命周期连同最新取消修复整块保留，构造器赋值顺序和 DTO 初始化顺序保持。

## PCL2 与 PCL-CE 依据

- 以 PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 为核心：`ModDownload.vb:1176-1208,1305-1313` 集中平台请求和镜像映射；`ResourceProject.vb:123,391-422` 分别承担项目解析和查询；`ResourceVersion.vb:115,227-259` 对应文件解析和文件列表获取。`ResourceSearcher` 的游标／结果仍留在现有搜索会话，本轮不合并到传输层。
- C# 重点参考 PCL-CE `dev` `915ac29795efbdf6373417389e181e1504117048`：`ModDownload.cs:2008-2064,2251-2254` 的平台请求策略边界；`ModComp.cs:958,1017,1051,1136,2845` 的项目／文件元数据职责，以及 `PCL.Core/Minecraft/ResourceProject/Curseforge/CurseforgeProject.cs:7-24`、`Modrinth/ModrinthProject.cs:7-42` 的数据契约分区。
- 借鉴职责边界，不宣称上游采用了相同的四文件 partial 结构，也不引入 CE 的 System.Text.Json 或新运行时 API。精确来源和约束复核见 `artifacts/core-refactor-round2/ce-boundary-review.md`。

## 等价性与验证

除原文分块重组外，使用独立 Roslyn C# 9 探针，从固定 Git 基线直接提取原目录及 LocalMods 文件，与候选四文件加 LocalMods 按完整类型／成员身份逐项比较。类型、签名、完整方法 token、字面值、默认值、属性／字段初始化和 DTO 内成员顺序需匹配；partial 头合并后比较。保留不可变输入副本与摘要，不依赖搬迁脚本自己生成的清单作为唯一证明。负控制验证篡改成员会被拒绝。原始元数据 token、MVID、PDB 和 DLL 摘要允许因编译单元变化而改变，不能据此判定行为不同或完全一致。

最终独立比对为 7 个类型、85 个手写成员、14 条 LocalMods 私有依赖，零差异。五个负控制分别修改超时方法体、可选参数默认值、sealed、LocalMods 方法体及同数量成员的归属，全部拒绝；只改注释的正控制通过。证据与工具在 `artifacts/catalog-refactor-review/`，根验证另从当前源码生成 `artifacts/catalog-refactor/member-equivalence/` 回执。

本轮不增加镜像实现的业务测试，保留原 VB 测试与断言。初步目录 444、目录取消 30、本地 Mod 更新 144、搜索状态 44 项通过；这些初步构建与最终候选分别记录。最终完整 VB 7468 项、原 Beta 8 已编译宿主 7305 项、979 条公开／1244 条友元契约、两份资源和 15 组配置／JSON，以及同一最终单 EXE 的冷启动 25 项和搜索 3240 项，均以 `artifacts/catalog-refactor/validation.json` 及原始回执为准，不累计重叠套件。

另用本机 VS 2026 MSBuild 验证 Debug 构建、三项目未优化配置与 Portable PDB；未人工 F5 验证断点。新文件可直接在现有解决方案中导航和调试，文件位置变更后旧物理行断点可能需要重新定位，方法与类型名称保持。参见根目录 `VS2026_DEBUG.md`。

最终构建发生在提交前，核对已测源码清单和待提交树，再将最终 commit tree 绑定回执。受控 HTTP、临时实例和隐藏 WPF 不作为真实平台完整安装、游戏启动或人工帧率验收；本轮没有 UI／动画源码修改，不重复完整 UI／motion 套件。未打包、推送或发版。

## 下一批方向

第二轮已开始，这批只完成目录模块的职责分区。后续可在保持同一兼容门槛的前提下，分别整理平台特定解析或安装规划／执行边界；算法与配置变更仍单独提交，避免和结构移动混在一起。
