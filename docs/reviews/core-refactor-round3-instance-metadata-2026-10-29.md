# 第三轮第二批：已安装实例的元数据解释

基线 `e05f7a25792034ea0a328411ba343e287d782b75`，承接上一批本地 Mod 私有文本解析器。本批提炼 InstalledInstanceCatalog 的私有、无状态 MetadataInterpreter，保留 VB WPF、现有 VB 测试、.NET Framework 4.8、程序集与外部入口、配置及资源名称。

## 改动与边界

原有 GameVersion、IsVersionId、DetectLoaders 共 165 行移入 `InstalledInstanceCatalog.MetadataInterpreter.cs`，外层类型加 partial，四个版本号调用和一个加载器调用加私有限定名。两个内部入口只在 private 嵌套类型内取得可访问性；不增加外部或 Friend 可调用接口。Scalar 继续使用外层原实现。

| 私有解释器 | 原读取器继续负责 |
| --- | --- |
| clientVersion、HMCL game patch、jar／id 的现有优先级 | 配置快照与 JSON 文件读取、继承图遍历及合并 |
| 历史／快照／愚人节版本 ID 的现有判定 | 版本分类、用户命名及可补全资格 |
| 库坐标、原始 patch、mainClass／arguments 的加载器识别 | PCL／PCL-CE／HMCL 原生隔离、运行目录和 fingerprint |
| 原异常类型／消息、去重、输出顺序 | 路径保护、取消检查、Scan／Read、警告及结果发布 |

成员正文只改缩进与两个入口的访问声明。工作文件原 CRLF 和 Git LF 分别保留、核对；提取规格记录原块，逆向还原同时要求完整 Git 原字节与原工作文件原字节一致。不得借重构引入新的版本别名、加载器兜底、分类或配置迁移。

## 源码对照

- PCL2 固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`：ModMinecraft.vb:427 起的 HMCL patches 处理，以及 PCLCS/Minecraft/McVersion.cs:140–169 的加载器、clientVersion／game patch 解释。上游还包含发布时间、参数或 JAR 等其他推断途径；本批不改变本工具已有取舍。
- C# 重点参考 PCL-CE `915ac29795efbdf6373417389e181e1504117048`：McInstance.cs:185–201 的来源优先级及 755 起的 Fabric／Quilt／Forge／NeoForge 识别。保留本工具的 Newtonsoft.Json、严格类型与多加载器顺序，不复制上游状态缓存或写配置逻辑。
- HMCL 固定 `63207affc6a73127cea19e64272a0401fde4b7a4`：DefaultGameRepository 对持久化表示的说明明确保留继承与 pending patches。这里只读解释已有表示，不调用其保存／迁移流程。

共 11 份固定原始 Git 文件与摘要见 `artifacts/instance-metadata-refactor/source/manifest.json`；包括当前本工具的配置、隔离和外部启动器边界。现有 PCL-CE／HMCL 配置读写策略不在拆分范围内。

## 差分与提交门槛

同一探针分别加载基线与候选 net48 DLL，用同一份 211 个输入和四种文化设置，比较 844 条返回值、精确列表顺序、异常类型／消息及输入不变性。覆盖版本别名／优先级、缺失与异常 JSON 类型、重复加载器、patch／库优先级、别名对象、LabyMod、参数回退与固定种子组合。初步候选差分为零；最终 DLL 必须重新通过同一差分。私有栈帧名变化不属于外部兼容合同。

最终提交要求以下检查全部通过，实际路径、摘要与完成状态集中在 `artifacts/instance-metadata-refactor/validation.json`：

| 检查 | 提交门槛 |
| --- | --- |
| 原有 VB 全量测试 | 7814 项，在本批最终 DLL 上新运行 |
| 原 Beta 8 编译 VB 宿主 | EXE 字节不变，替换最终 Core 后 7305 项 |
| 当前 C# 基线的接口与数据 | 原始／规范 API 零差异；公开 979、Friend 1245、资源 2、JSON 15 |
| 私有解释器差分与源码逆向 | 844 条零差异、输入不改写；原块／外层精确还原 |
| VS2026 Debug | 三项目 net48、未优化、三 Portable PDB |
| 同一正式单 EXE 冷目录 | standalone 25、搜索 3444；两依赖／七根文档字节匹配 |

当前元数据基线为上一批已验证 C# Core（SHA-256 `B90CD4BD77F5B8D8A1082D90D77AE824C853E1B22850FF2E15A3429176D5DC4F`）；原 VB 编译宿主独立用于二进制兼容。原 Beta 8 元数据的两项历史内部新增已在上一批说明，本批不声称与原 Beta 8 元数据完全相等，也不修改比较工具或编译器豁免规则。

VB 界面、VB 测试、项目、工具、资源及其他 Core 文件按上一批原始清单核对不变。完整 UI 6662／motion 6011 不在本批重跑范围，前序 `3d017f5` 的结果只提供历史上下文；没有新的手工 F5、真实游戏或逐帧动画验收。

## 后续

配置快照与原生隔离仍是原边界，不把这次解释器提炼当作整个外部配置重构完成。下一批适合单独处理配置解释与文件读取的边界，并继续以原始格式夹具和二进制兼容为门槛。发布编排中残留的散列取消等待、成功后清理与回滚仍依[第二轮收尾清单](core-refactor-round2-closeout-2026-10-29.md)独立处理。

本批使用受控临时夹具；主代理自查，没有独立代理审查或真实用户目录验收。不修改外部启动器配置，不打包、推送或发版。
