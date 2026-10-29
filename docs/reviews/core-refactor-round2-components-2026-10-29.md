# 第二轮重构第二批：组件安装与 Java 执行职责

基线：`c9ebc450df2e5028bb4133c0b1a1a329ab337f30`。继续 Core 的结构整理，将原约 900 行的组件安装文件拆为安装编排、Forge 执行、下载校验、配置解析、Java 进程和同步进度转发六个文件。公开类型与方法体保持，VB 界面、原 VB 测试和 net48 不变。

## 文件与状态归属

| 文件 | 保留职责 |
| --- | --- |
| `ComponentInstaller.cs` | 全部字段、自动属性、构造器、ApplyAsync、组件 Mods 计划、发布租约，以及共用的进度和处理器日志。 |
| `ComponentInstaller.Forgelike.cs` | Forge／NeoForge 安装、处理器顺序执行和输出声明。 |
| `ComponentInstaller.Downloads.cs` | 共享下载器适配、计数累积、OptiFine 源回退与负载校验。 |
| `ComponentInstaller.Profiles.cs` | 配置合并、依赖库、LiteLoader profile、处理器变量、路径替换、Fabric 元数据和 ZIP 辅助。 |
| `JavaRuntime.cs` | 原 JavaRuntime 完整类型：Java 发现与版本探测、双流读取、进程取消／清理及 Windows 参数转义。 |
| `InlineProgress.cs` | 原 InlineProgress 完整类型：同步调用原委托，保留异常传播。 |

ComponentInstaller 只增加 `partial`，仍是原命名空间中的 public 非 sealed 类；没有引入新的服务对象或接口。所有实例字段与属性留在主文件，`Stopwatch.StartNew()` 仍先于发布列表初始化，不建立跨 partial 文件的字段初始化顺序依赖。ProcessorLine 仍共享同一节流时钟、日志与锁。

JavaRuntime 的 StandardModule 标记、sealed、私有构造器和静态成员保持，支持原 VB 调用及既有二进制消费者。InlineProgress 保持 public 非 sealed 与直接委托调用。安装器资源仍通过原类型的程序集读取，`OptiFineBridge.class` 逻辑名不变。

## 固定源码依据

以 PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 为核心：`Plain Craft Launcher 2/Pages/PageDownload/ModDownloadLib.vb:1733,1813-1846,1896` 分别展示安装编排、各组件 loader 与配置合并；`PCLCS/Java.cs:34,41,99` 将 Java 检测与执行工具分开。

C# 重点参考 PCL-CE `dev` `915ac29795efbdf6373417389e181e1504117048`：`Pages/PageDownload/ModDownloadLib.cs:715,1750-1785,1963,3795,4057-4073,4144` 的 OptiFine／Forge 执行、回调生命周期、总体编排与合并；`PCL.Core/Minecraft/Java/JavaManager.cs:19,109,286` 的 Java 领域边界。借鉴职责，不复制其现代运行时 API、Java 管理器架构或 JSON 实现。精确引用与约束见 `artifacts/core-refactor-round2/component-boundary-review.md`。

## 保真与验证

独立 Roslyn C# 9 探针直接读取固定 Git 原文件，与当前六文件比较：3 个类型、48 个手写成员、26 条同类型私有依赖，零差异。只允许 ComponentInstaller 增加 partial；其他修饰符、完整签名／默认参数／方法 token、字面值、类型归属、初始化顺序均严格比较。JavaRuntime 与 InlineProgress 还需整类声明顺序相同；全部状态、构造器和 ApplyAsync 必须仍在主文件。六个负控制分别修改 Java 取消体、可选默认值、Java 构造可见性、Module 标记、状态字段位置及初始化顺序，全部被拒绝。证明和工具在 `artifacts/component-refactor-review/`，根验证另保存 `artifacts/component-refactor/member-equivalence/`。

原文级独立复核也确认 ComponentInstaller 的 22 个方法及构造函数、字段／属性整段、两个独立类型完整原文保持；新文件的 using 均为原导入集合子集。首次编译发现缺少 Constants／Regex 的 using，补齐原有命名空间后构建通过，没有改写方法体；早期失败日志不计作最终通过。

不新增镜像搬迁实现的业务测试，保留原断言。初步 Java 探测 20、处理器双流 22、输出取消 2、失败通知 19 项通过。完整 VB 7468、原 Beta 8 已编译宿主 7305、公开／友元契约、两份资源和 15 组配置／JSON，以及 VS 2026 Debug／Portable PDB、最终单 EXE 冷启动与搜索，均以 `artifacts/component-refactor/validation.json` 和原始回执为准，不累计重叠套件。

上轮出现过两次不同的动画／弹窗复合断言失败，随后原基线和同字节候选通过，原因仍未确定。本轮保持 UI、断言和等待阈值；所有构建、Core 宿主结束后再串行运行单 EXE 检查。该安排不作为上轮根因已解决的证明；若再出现失败，保留回执并使用私有分项状态诊断。

最终源码清单、精确 DLL／EXE、提交前暂存树与实际 commit tree 绑定。受控进程、本地 HTTP、临时实例和隐藏 WPF 不代表真实平台完整安装、游戏启动、人工 F5 或帧率验收。未打包、推送或发版。
