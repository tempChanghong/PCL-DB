# 第三轮：本地 Mod 纯文本解析边界

基线 `3d017f5413d9caff5b2d586cfc4a607b810dc727`。第三轮从一个私有、无状态的解析边界开始，继续保留 VB WPF、全部 VB 测试、net48、程序集／公开接口、配置及资源名称。这是 C# Core 的职责拆分，不是把界面继续改语言。

LocalModCatalog 原来同时负责路径／实例快照、ZIP／流读取和多种文本格式解析。本批将 392 行原有解析代码移入 `LocalModCatalog.MetadataParser.cs` 中的 **private static MetadataParser**；外部仍只调用原来的 Read／Scan。LocalModCatalog 加上 partial 用于保持原类型身份，但结构变化是实际的私有解析器提炼，不只是把同一类拆成几份文件。

| 进入私有解析器 | 保留在原目录读取边界 |
| --- | --- |
| Fabric／Quilt／LiteLoader JSON、旧 Forge mcmod.info、严格 TOML | Read／Scan、原 LocalModEntry／Identity、实例不变性与共享目录冲突 |
| JSON 对象与字符串类型检查、图标路径文本选择、版本占位符解释 | ZIP 重复条目检查、大小限制、UTF-8／JAR 清单字节展开、ReadBytes／取消令牌 |
| JAR 清单主属性的纯字符串解释 | 图标字节、尺寸／预算、散列／文件锁、显示文本裁剪及结果发布 |

七个直接入口只是取得有效私有访问性；ReadToml 是对原 LocalModToml.Read 的单行转发。所有原解析成员正文按对应映射保留，仅改变外层限定名、缩进与入口访问声明，没有换 JSON 库或重写 TOML 规则。原工作文件为 CRLF，Git 固定对象为 LF：外层保留其 CRLF，迁入新文件用 LF；映射同时验证规范 Git 字节与工作文件原始字节的逆向还原，不将换行差异藏成算法修改。

PCL2 固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 LocalResourceFile／LocalResourceLoaders 用于核对启用状态、目录和加载生命周期；C# 重点参考 CE `915ac29795efbdf6373417389e181e1504117048` 的 ModLocalComp Load／LoadCore 与分格式区域。CE 的具体 JSON／TOML 实现与本工具并不相同：只借鉴职责边界，保留这里的重复字段、类型、多行文本、UTF-8、图标路径和多 Mod 严格规则。固定原字节共 10 份见 `artifacts/local-mod-parser-refactor/source/manifest.json`。

进入条件来自上一批已闭环的高优先缺陷、`3d017f5` 的完整 UI／动画／搜索结果，以及当前 Core 与 `3c143de6` 已验证 DLL／源码一致。复核发布边界后保留明确后续清单：RuntimePublication 四处旧 Matches、Forge 处理器输出校验、SharedMods／LegacyResource 的部分散列仍可能延迟取消；提交后清理不能因迟到取消反向失败。这些路径本批逐字节不变，不把它们宣称为已完成，也不在提炼解析器时混入发布／回滚改造。见[第二轮收尾与进入条件](core-refactor-round2-closeout-2026-10-29.md)。

验证使用相同私有反射探针、相同 203 个文本样本，在独立 net48 进程中覆盖 en-US／zh-CN／tr-TR／fr-FR：812 条结果及异常类型／消息差分为零，输入对象不变。覆盖重复 JSON 字段、缺失／非字符串标识、多 Mod、图标数字键、大小写、主属性／命名清单段、TOML 多行／转义／伪表／未知字段及固定种子样本；堆栈私有类型名不是兼容性要求。

最终 DLL 新验：现有 VB 全量 7814、原 Beta 8 编译宿主 7305；相对当前 C# Core 的规范／原始 API 均零差异，公开／Friend 声明 979／1245、资源 2、JSON 15 保持。相对原 Beta 8 的精确元数据总比较仍失败：历史提交 `72326b8` 的 NotifyCancellation 与 `f8be3c3` 的带 token Matches 已让 Friend 从 1243 增至 1245；前序 DLL 与新 DLL 的规范／原始差异完全一致，公开 API 无变化。本轮没有新增差异，原始失败回执保留，单独的原 VB 编译宿主 7305 项通过；未放宽比较工具或规则。新正式单 EXE 冷目录运行 25／完整搜索 3444，内嵌新 Core 与 JSON、七根文档匹配；VS2026 Debug 三项目及 Portable PDB 通过。VB 界面／测试、项目、运行库依赖和资源源文件未改，策略仍为 `PCL-DB-checks/2026-10-29.12`。完整 UI 6662／motion 6011 是 `3d017f5` 的前序结果，本轮未重新跑，不能计为新 DLL 的验证。

源码、方法映射、最终 DLL／旧宿主／回执和提交树见 `artifacts/local-mod-parser-refactor/validation.json`。主代理自查，无独立代理复核；使用临时文件与受控进程，未访问用户 Mods 或修改外部启动器配置，不代表所有真实 Mod 的兼容性证明。未打包、推送、发版。

后续适合先补齐真实格式样本的解析差分，再独立拆分外部配置快照／实例解释；发布编排的取消与回滚继续按上述阶段清单单独处理。现有 VB 界面和测试没有列入本轮语言迁移。
