# 第三轮第三批：原生隔离配置的值解释

基线 `349f9f4121a72be9b63aa464a476b384227c7056`。承接实例元数据解释器，将原生隔离设置的值解释和文件读取进一步分开，保留 VB WPF、现有 VB 测试、.NET Framework 4.8、程序集、外部入口、配置与资源名称。

## 改动与保留边界

NativeInstanceIsolation 的 IniValue、Scalar、ParseBool、BooleanValue、ParseInt、HmclMode、StrategyValue、CheckSchema 共 99 行提炼到 `NativeInstanceIsolation.ConfigurationInterpreter.cs` 的 **private static ConfigurationInterpreter**。外层类加 partial，34 个调用加限定名；方法正文只改变缩进和嵌套类型内的入口访问声明，不扩展外部或 Friend 接口。

| 私有值解释器 | 原隔离计划继续负责 |
| --- | --- |
| 原 INI 标量、重复键处理与大小写规则 | YAML／JSON／INI 的查找优先级、后备来源、严格 JSON 读取 |
| JSON 标量类型、布尔值与整数范围 | 外部配置快照、缺失文件记录、fingerprint 与短期读锁 |
| HMCL 历史目录枚举、配置版本检查 | HMCL 新旧实例和预设继承、CE 已有隔离与数据目录判断 |
| 现有五种默认策略的纯值映射 | 新建／补全区别、最终目标、路径与自定义运行目录保护 |
| 原异常类型、消息、空值和文化行为 | 暂存实例配置写入、字节复验、取消和原子发布关系 |

IniValue 仍采用原来的同名键最后值；不统一替换 PCL、CE YAML 或其他读取器的规则。StrategyValue 的默认分支与调用前的范围检查均保留。ReadJson／CeReader 的文件选择、惰性读取时机、委托和快照记录不迁移，暂存写入只改 CheckSchema 的限定名。

提取规格保存原方法块、原工作文件与摘要；完整逆向同时要求 Git LF 原字节和工作文件 CRLF 原字节一致。不在职责拆分中混入取消签名、隔离策略、JSON 库、迁移或错误降级调整。

## 源码依据

- PCL2 固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`：ModMinecraft.vb:240–277 的已有实例、旧设置和默认策略优先级；Pages/PageSetup/Settings.vb:95、188 的全局／实例默认值。PCL-DB 的只读快照、锁和原子发布属于本工具的冲突保护，不声称上游有同名实现。
- C# 重点参考 PCL-CE `915ac29795efbdf6373417389e181e1504117048`：McInstance.cs:65–114 的隔离判断，以及 CatIniFileProvider／JsonFileProvider／YamlFileProvider 的值与文件格式边界。CE 会初始化文件、转换格式并保存隔离结果；这些行为不移入本工具对外部配置的只读访问。
- HMCL 固定 `63207affc6a73127cea19e64272a0401fde4b7a4`：GameSettings 的 runningDirectory 与 defaultIsolationType，LegacyGameSettingsMigrator 的 ROOT_FOLDER／VERSION_FOLDER／CUSTOM 和旧设置继承。这里保留既有兼容规则，不调用上游迁移或写入流程。

12 份原始 Git 文件与摘要见 `artifacts/native-config-refactor/source/manifest.json`，包括本工具的 NativeInstanceIsolation、InstanceConfiguration、PclIsolationPlan 和 ExternalLaunchers。此次只提炼 NativeInstanceIsolation 的值解释，其余文件逐字节保护。

## 差分与提交门槛

相同反射探针、相同 440 个输入，在分别加载旧／新 net48 DLL 的两个进程中运行 en-US／zh-CN／tr-TR／fr-FR，比较 1760 条返回值、异常类型／消息和输入不变性。包含 INI 空值／大小写／重复键、异常 JToken 类型、布尔写法、整数上下界与反向范围、HMCL 字符／数字枚举、配置版本和策略组合。候选差分为零；最终 DLL 必须通过同一差分。私有栈帧名变化不算外部合同改变。

实际完成状态、最终 DLL 摘要与提交树集中在 `artifacts/native-config-refactor/validation.json`；提交前要求：

| 检查 | 门槛 |
| --- | --- |
| 精确源码逆向／差分 | 原块与外层可完整还原；1760 条零差异、输入不变 |
| 原有 VB 全量 | 最终 DLL 新跑 7814 项 |
| 原 Beta 8 编译 VB 宿主 | EXE 字节不变，替换最终 Core 后 7305 项 |
| 当前 C# 基线合同 | 原始／规范 API 零差异；公开 979、Friend 1245、资源 2、JSON 15 |
| VS2026 Debug | 三项目 net48、未优化、三 Portable PDB |
| 同一单 EXE 冷目录 | standalone 25、搜索 3444；两依赖和七根文档匹配 |

当前元数据基线为上一批 C# Core（SHA-256 `4821538C153CC45D828D849342223074132F4E21C8A442D8A8389070126F7136`）。原 VB 编译宿主用于二进制兼容，不声称与原 Beta 8 精确元数据相同；历史两项内部新增仍保留前序记录。比较工具、编译器规则与计数策略 `PCL-DB-checks/2026-10-29.12` 均不改。

VB 界面／测试和其他 Core、项目、工具及资源源文件按上一批原始清单保护。完整 UI 6662／motion 6011 未在本批重跑，`3d017f5` 的结果只作历史上下文；没有新的手工 F5、真实游戏或逐帧动画验收。

## 后续

此次不是整个配置子系统重构完成：文件发现、快照和原生策略编排仍在原边界。后续适合按独立调用链梳理配置读取与解释；共享资源／处理器发布的散列取消等待、成功后清理与回滚继续依[第二轮收尾清单](core-refactor-round2-closeout-2026-10-29.md)单独处理。

本批使用受控临时夹具并由主代理自查，没有独立代理审查或真实用户目录验收。不修改外部启动器配置，不打包、推送或发版。
