# 外部启动器目录配置的错误边界

基线 1a52fd60933699a692e1c520875b4de8def746c2。本批承接配置边界审查，修复目录发现阶段的两个实际问题；保留 VB WPF、VB 测试、.NET Framework 4.8、程序集、公开／Friend 接口、外观与动画。

## 复现与影响

使用基线 DLL 的公开 LauncherDirectoryReader.Read、临时工作目录与显式配置路径，不读取用户 AppData：

| 输入 | 旧结果 | 修复后要求 |
| --- | --- | --- |
| HMCL 可选 launcher-settings.json 的 selectedGameDirectory 为对象／数组 | 整体扫描失败，正常本地目录不可用 | 提示选择信息不可读，继续显示本地目录，避免猜测选中 ID |
| CE 可选共享配置的 FileVersion／LaunchFolders 为对象／数组 | 整体扫描失败，正常本地选择丢失 | 提示共享配置异常，保留有效本地目录与选择 |
| CE／HMCL 必需主配置含重复路径键 | 静默采用最后一项，可能显示错误目的地 | 拒绝该来源并提示，保留界面原选择 |
| 可选共享目录／选择 JSON 含重复键 | 静默接受歧义数据 | 仅忽略该可选来源，保留可靠本地目录 |

baseline.json 保留首批四个公开入口复现结果。发现阶段原来使用默认 JObject.Parse 的后值覆盖行为，而 NativeInstanceIsolation.ReadJson 已拒绝重复键，导致同一配置在发现与安装阶段解释不一致。

## 最小修复

仅修改 ExternalLaunchers.cs：

1. 五个外部 JSON 解析调用转到私有 ParseConfiguration，沿用 Newtonsoft.Json，显式拒绝重复键，包含嵌套对象；其他解析设置和读取顺序不变。
2. HMCL 可选当前选择、HMCL 可选共享目录、CE 可选共享目录的三个既有错误边界补齐 ArgumentException、InvalidCastException、FormatException。不会将所有异常统一吞掉，也不改变主配置失败规则。

主配置异常继续返回失败，界面既有逻辑会保留当前目录；可选配置异常则返回可靠本地结果及原有提示。缺失来源、来源优先级、YAML／INI、PCL 路径展开、大小限制、共享 ID 优先级及新建／补全策略保持。修好配置后使用同一读取器重试，不保留旧警告。

MainWindow.ExternalLaunchers 的读取代次、冻结绑定和结果提交条件逐字节不变。本批不改变快速切换、下载中禁止编辑、导航或动画；恢复目录附带原启动器身份，后续编辑不会重定向已冻结的安装目标。

## 源码对照

- PCL2 固定 5ef81e490ae46b1100d61e33d0183094c7a80ca8：ModMinecraft.vb:58–115 的按来源／条目扫描与失败隔离。上游还会同步配置、创建默认目录；这里保留本工具的只读策略。
- C# 重点参考 CE 915ac29795efbdf6373417389e181e1504117048：ModFolder.cs 的目录加载和逐项异常处理，以及 JsonFileProvider／YamlFileProvider 的格式边界。不引入这些 provider 的初始化和迁移写入。
- HMCL 固定 63207affc6a73127cea19e64272a0401fde4b7a4：GameDirectoryManager:184 起按选中 ID 查找已合并目录，LauncherSettings 的 selectedGameDirectory 类型。未知选择与可用目录不是同一状态。
- 本工具已有 NativeInstanceIsolation.ReadJson 的重复键规则提供发现／安装一致性的直接依据；这是本工具自身的冲突保护，不声称上游存在同名修复。

13 份固定 Git 原字节与摘要见 artifacts/launcher-directory-read-errors/source/manifest.json。

## 验证与提交门槛

新增独立 VB 模块 LauncherDirectoryConfigurationChecks，共 14 组、70 项。仅在 Program 增加回归入口和全量调用，原有测试正文不改。每组验证错误边界、可靠目录与选择、外部文件字节和副文件、修复后重试及安装目标冻结。使用同一新编译测试 EXE，只替换 Core：

- 旧 DLL 的 14 组均在对应行为断言失败，保留日志、退出码和宿主／DLL 摘要，避免把加载失败当成复现；
- 修复 DLL 通过 70 项；最终构建还需重复这一相同宿主对照。

最终提交需通过 VB 全量 **7884**（既有 7814 加新增 70）、未经重编译的原 Beta 8 宿主 **7305**、当前 C# 基线的原始／规范 API 零差异（公开 979、Friend 1245）、两个资源／15 组 JSON、VS2026 Debug／三 Portable PDB、同一冷目录单 EXE 的 standalone 25／搜索 3444，及内嵌两依赖／七根文档匹配。实际完成状态、最终字节和提交树集中在 artifacts/launcher-directory-read-errors/validation.json。

当前元数据基线为上一批 C# Core（SHA-256 1F86DEDCB1923B54A77DC003C551AA0DDBCEEC29B49C8870BCB89857C9FFE193）；原 Beta 8 宿主单独证明二进制兼容，不声称旧 Beta 8 精确元数据相同。UI 计数策略保持 PCL-DB-checks/2026-10-29.12，不调整比较工具或编译器规则。

本批未重新运行完整 UI 6662／motion 6011；前序证据仅作历史上下文。没有真实游戏、用户目录、手工 F5 或逐帧动画验收。主代理自查，没有独立代理审查。未打包、推送或发版。

后续继续分开梳理目录发现、只读配置快照与安装计划；发布阶段的散列取消等待、成功后清理和回滚仍按既有清单独立处理，不在此次读取修复中混入。
