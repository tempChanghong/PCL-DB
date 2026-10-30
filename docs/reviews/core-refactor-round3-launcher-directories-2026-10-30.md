# 第三轮第四批：外部启动器目录配置解释

基线 b59b4ce7ae26ce6d14c955c26767a5c06a819be1。保留 VB WPF、VB 测试、net48、程序集／公开接口、配置和资源名称。

## 范围

从 LauncherDirectoryReader 提炼私有无状态 DirectoryConfigurationInterpreter。七个入口为严格 JSON、单键 YAML、HMCL 当前目录、HMCL 旧 profiles、CE YAML 选择、CE JSON 选择和 CE 共享列表解释。原 ExternalLaunchers.cs 594→510 行，新文件 140 行；拆分目的是职责边界，不是减少总代码行数。

文件发现、路径选择、File.Exists／Directory.Exists、ReadText、INI 的二次路径读取、ExpandPath、Add、警告与回退、最后身份装饰仍由原读取器负责。内部 ReadYamlScalar 保留原签名转发，不增加公开或 Friend 业务入口。

解释器使用回调逐项回传，保留原循环中路径验证与下一项读取交错的时机。没有先收集完整列表：这样晚异常仍保留先前结果，HMCL 无效／不存在路径仍在原时机占用 ID，跨配置重复 ID 去重与大小写敏感选择比较保持区别。CE 先版本后选择、共享错误保留本地目录、非法最终选择可能导致未装饰部分结果等原语义保持。

不修改隔离、配置快照锁、安装目标绑定、下载器、原子发布、取消或回滚；不借重构修复格式规则。提取规格记录八处外层替换、原始工作文件、完整新旧文本，可逆向恢复 Git LF 与原工作文件 CRLF 字节。

## 基线与差分

先用固定旧宿主复验当前基线：原 Beta 8 编译 EXE 的 7305 项通过，原 EXE SHA-256 保持 1BE8D9EE01497F7890DCA4B84AACBE37024874039DEFA350DD459372C5ADC327。对原 Beta 8 的严格元数据总比较仍为失败，仅规范 Friend 多出 NotifyCancellation、Matches(token) 两个历史入口，公开 979、资源 2、JSON 15 不变。原失败回执保留，不把二进制测试通过表述为原始元数据完全一致。见 preflight/result.json。

独立探针固定 EXE 与用例，在分别加载旧／新 net48 DLL 的进程中执行 151 个临时文件场景 × en-US／zh-CN／tr-TR／fr-FR，共 604 条结果。比较完整目录身份、顺序、Origin、选择、显示名、版本数、警告顺序、异常类型与消息、绑定输入不变性；238 份输入文件集合与 SHA-256 保持。覆盖新旧 HMCL、shared、重复键／ID／路径、大小写、计数与版本边界、相对路径、CE YAML／JSON／INI／shared、选择与部分失败。中间候选零差异，最终 DLL 必须使用同一探针重验。

PclFolders.Discover(false) 仍会无条件探测 APPDATA/.minecraft。为隔离真实环境，这组差分仅运行 PCL 前置拒绝路径；未修改的 PCL 正向分支以源码逆向保护、旧宿主和既有回归为证据。不声称 604 条差分覆盖全部三启动器正常发现，也不证明任意真实配置均兼容。私有栈帧名称变化不属于兼容合同。

## 源码与审查

以 PCL2 固定 5ef81e490ae46b1100d61e33d0183094c7a80ca8 的 ModMinecraft／Settings 为目录选择依据；C# 重点参考 CE 915ac29795efbdf6373417389e181e1504117048 的 States／Settings 和三个配置 providers。HMCL 固定 63207affc6a73127cea19e64272a0401fde4b7a4 的 GameDirectoryConfig／ConfigGameDirectory 用于目录格式与身份。上游会初始化、修复或迁移配置，本工具保持只读，不照搬写入流程。

十份固定 Git 原字节及 SHA-256 见 artifacts/launcher-directory-refactor/source/manifest.json。独立代理复核提取规格、惰性回调、ID 占用、INI 二次读、YAML 顺序及原身份装饰；独立差分代理编制夹具并比较，主代理实现并进行最终范围核对。审查凭据在 audit，差分在 differential。

## 最终门槛

提交前在最终 DLL 新跑当前 VB 7984、原 Beta 8 编译宿主 7305、604 条差分；与当前 C# 基线的原始及规范 API 零差异，公开 979／Friend 1245、资源 2、JSON 15。另核对最终 Release、VS2026 Debug 三 Portable PDB、同一冷目录单 EXE 的启动 25／兼容 299／完整 UI 7166、两依赖及七根文档实际字节，以及验证契约 110。

实际完成状态、进程和源码／提交树绑定集中于 artifacts/launcher-directory-refactor/validation.json。既有 VB UI、测试、工具、策略、项目与资源源文件受原始清单保护；没有降低断言或延长超时。完整 motion 6011／搜索 3444 本批不另跑，历史结果不作新验收。

验证使用临时配置与受控进程，不写真实外部配置、不启动游戏、不进行真实网络下载、手工 F5 或帧率／性能验收；未打包、推送或发版。下一批可单独审查发布阶段散列取消、成功清理和回滚，不将这次解析边界提炼当作整个配置或生命周期重构完成。
