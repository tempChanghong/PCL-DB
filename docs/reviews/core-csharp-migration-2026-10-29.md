# Core 迁移到 C#：Beta 8 兼容基线

本轮将 Core 的 60 份 VB 源文件迁为 C#，以 `v8.0.0-beta.1` / `872220b0f7a8303530c5cddaa74af5aafe04c499` 为基线。WPF 界面、动画、现有 VB 测试实现和 .NET Framework 4.8 保持。程序集仍为 `PclDownloadBridge.Core`，命名空间和版本保持；当前源码尚未发行，不修改 Beta 8 已发布工件。

## 源码依据与范围

- 界面和行为以 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 为核心，重新核对 `ModLoader.vb` 的状态赋值、取消通知与执行身份检查。UI 本轮不迁移、不重做。
- C# 逻辑重点参考 PCL-CE `dev` `915ac29795efbdf6373417389e181e1504117048`：`ModLoader.cs` 的输入／重启顺序，`LoaderDownload.cs` 的许可所有权，`FileDownloader.cs` 的取消和响应清理，`CommonFileProvider.cs` 的同目录原子发布。固定引用见根目录 `SOURCE_REFERENCES.md`。
- 以 ICSharpCode.CodeConverter 9.2.7.792 生成初稿，再人工处理编译和跨语言语义。工具只安装在忽略的本地验证目录，不是构建依赖。
- C# 语法固定为 9.0，目标仍是 net48；不引入 CE 的现代运行时 API，不替换 JSON 库，不改变下载源、安装算法、配置结构或文件路径规则。

## 需要保留的行为

1. 保留 Newtonsoft.Json 13.0.3、JToken 显式转换和严格字段验证。保留必要的 Microsoft.VisualBasic 转换调用，避免将注册表值读取、空字符串比较、浮点转整数或缺失 JSON 字段改成另一种含义。
2. 项目显式开启整数溢出检查；保留数组实际长度、浮点除法、舍入、循环上界及多 worker 的原始异常选择。取消仍带调用方 token，局部超时与用户取消分开，许可只由持有者释放。
3. 原 VB Module 以 `StandardModuleAttribute`、sealed 类、私有构造器及静态成员保留，支持原 VB 调用方式；`InternalsVisibleTo("PclDownloadBridge.Tests")` 不变。
4. Java 进程回调使用实际 `EventHandler` / `DataReceivedEventHandler`，订阅与退订使用同一委托；搜索取消清理循环显式使用大括号，保留故障观察器。
5. 保留原生隔离配置、原始文件快照、共享 Mods 租约、逆序回滚和校验后原子发布。没有读取或修改用户实际启动器配置来完成本轮测试。
6. 资源继续名为 `OptiFineBridge.class`、`PclWikiEntries.txt`。App 继续按原名称嵌入 Core 和 Newtonsoft.Json，解析器不变，不增加旁置语言转换依赖。

## 接口兼容检查的准确含义

新增 `tools/verify-core-migration.ps1`：通过独立 net48 进程读取两份 DLL，保存原始快照，再比较可调用契约、程序集身份、两份资源摘要和 15 组配置／JSON 样本。基线从 Git archive 单独构建，不能用已引用新 Core 的当前测试输出冒充旧基线。

公开声明 979 条、排除编译器生成辅助类型后的友元声明 1243 条。比较类型和成员名称、参数名称／类型／默认值、访问权限、继承及接口、泛型约束和资源字节；不声称原始 CLR 元数据逐字相同。以下编译器差异有明确记录和定向检查：

- C# 与 VB 的 `HideBySig` / `CheckAccessOnOverride` 标记；访问权限和其他虚方法约束继续比较。
- 自动属性 setter 的编译器参数名，以及委托辅助方法的编译器生成参数名；普通方法和委托 `Invoke` 的用户参数名保持严格比较。
- 没有静态初始化器类型的 `BeforeFieldInit` 位。七个真正存在静态初始化的类型，其标记与静态字段一致，不额外添加静态构造器。
- VB `MyTemplate` 自动生成的五个辅助类型。原始快照保留该生成树，手写公开／友元接口继续检查。
- internal sealed 的 `NativeInstanceIsolation`，C# 使用同名内部 getter 加私有显式接口桥接。工具检查接口映射、桥接 IL，以及未初始化对象的 false/true 两值调度，不全局放宽 virtual/final 约束。

工具也有反例自检：单独篡改资源、可选参数默认值或 JSON 默认值会被拒绝。元数据比较不能证明任意方法体等价，因此还须运行原测试和界面套件。

## 验证与复验

本地凭据集中在 `artifacts/core-csharp-migration/`。`validation.json` 汇总最终结果与被测 DLL／EXE 摘要；原始失败转换日志、严格元数据差异和后续修复结果均保留。以下是验证范围和套件约定，实际执行状态以该汇总及各原始回执为准，不将注册数当作执行数。

| 检查 | 范围／约定数量 | 凭据 |
| --- | --- | --- |
| Release 解决方案 | C# Core + 原 VB UI + 原 VB 测试，零警告／零错误 | `solution-release.log` |
| VS 2026 Debug | 三项目 net48、未优化、Portable PDB；未人工按 F5 验证断点 | `vs2026-debug-build.log`、`vs2026-debug-properties.json` |
| 原 VB 测试源码重新编译 | 7305 项；本地 HTTP、模拟安装器、临时原生配置、真实临时文件锁与原子操作 | `new-vb-tests.stdout.log`、`new-vb-tests-process.json` |
| Beta 8 已编译 VB 测试 | 仅替换 Core DLL，保持原 EXE 字节，完整 7305 项 | `contracts/` 下最终 `result.json` |
| 单 EXE 冷启动 | 中文临时目录仅放一个重命名 EXE；25 项加载检查、两份依赖与七份文档逐字节验证 | `single-exe/` 下 `result.json` |
| 现有 UI／动画／搜索 | 同一个单 EXE，6515／5896／3240 项，顺序执行；套件之间有重叠，不累加为独立场景数 | 单 EXE 目录的 `ui.json`、`motion.json`、`pack-search.json` |
| 验证工具 | 契约 110、超时 84、进程 26；超时验证另外覆盖 PS 5.1 和 PS 7 | 对应工具 JSON |
| PCL 固定来源 | 44 份源码与固定资源摘要，现有 UI 对照检查 | `pcl-reference.log` |

复验 Core 契约及旧宿主：

```powershell
./tools/verify-core-migration.ps1 `
  -BaselineCorePath artifacts/core-csharp-migration/baseline-archive-build/bin/PclDownloadBridge.Tests/release/PclDownloadBridge.Core.dll `
  -CandidateCorePath artifacts/core-csharp-migration/candidate-build/bin/PclDownloadBridge.Core/release/PclDownloadBridge.Core.dll `
  -BaselineTestsDirectory artifacts/core-csharp-migration/baseline-archive-build/bin/PclDownloadBridge.Tests/release `
  -RunBaselineTests
```

`./tools/verify-single-exe.ps1` 仅构建并验证候选，不创建分发 ZIP、不改 `dist`、不发版。常规构建和测试仍使用 `./build.ps1`；只有明确传入 `-Package` 才进入打包流程。VS 设置见 `VS2026_DEBUG.md`。

## 边界与后续

本轮不进行真实平台上的完整游戏／整合包下载安装，不启动 Minecraft，不将隐藏 WPF 夹具视为真人桌面、帧率或设备验收。没有发布、推送或迁移用户配置。

后续新增 Core 行为优先用 C#，继续以 PCL2 行为及 UI 为核心、重点参考 PCL-CE 的 C# 逻辑。下一轮适合逐个模块改善可读性和责任划分；与语言迁移分开提交，保留当前兼容性门槛和原 VB 测试，避免同时改变下载算法、JSON 或界面架构。
