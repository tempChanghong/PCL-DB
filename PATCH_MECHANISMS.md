# 两个 PCL 恢复项目的机制核对

日期：2026-10-21。根据本地源码与差分文件静态核对，未执行补丁、未修改或启动 PCL。

## 固定对象

| 项目 | 本地 Git HEAD | 方式 |
|---|---|---|
| `../pcl-download-unlock` | `2dd4729f03e287d33e854189bde988cef8dabe09` | dnlib 定点修改托管 IL，附带 WinForms 选择、校验及备份恢复流程 |
| `../PCL-Re.Download_PATCH` | `f4e845d7c37a84523481dc7d1d798b0221fc7515` | Mono.Cecil 从旧发行版移植方法，再生成 gzip 压缩的二进制差分 |

两者均限定官方 2.99.0.0 的某个具体构建，输入 SHA-256 相同：

`6B6E1260CB5A1D6A303855F1C60CAB3D0BC1AEBBA7E40A964180188B271E7386`

本轮直接从本地 `../PCL/最新正式版.zip` 读取 `Plain Craft Launcher 2.exe` 并计算哈希，长度 5,980,160 字节，SHA-256 确实与上述输入相同。仅做读取与校验，没有给该 EXE 应用补丁。

这不是同版本号通用补丁。当前工作区第二个项目的 `Patch-PCL.ps1` 已处于删除状态；有关应用脚本的核对使用 `git show HEAD:Patch-PCL.ps1`，保留了这项已有删除。

## pcl-download-unlock

主要依据 `src/PatchEngine.cs` 和 `src/PclService.cs`：

1. 核对目标 SHA-256、程序集身份、版本及预期指令结构。
2. 将 `PCL.PageDownloadInstall.LoaderInit` 中构造“关闭下载”假加载器的 IL，替换为读取 `ModDownload.DlClientListLoader`。
3. 将 `PageDownloadModify.LoadInstanceFinished` 中 Minecraft 卡片的 Visibility 从 Collapsed 改为 Visible。
4. 同时把字面量字段 `ModSecret.RestrictMcDownload` 的元数据改为 False。
5. 重新写出后比对其他方法 IL、字段常量和嵌入资源；服务层负责临时副本、原始备份、替换前再次校验及恢复。

预期产物 SHA-256：

`D8F095ACBD7ADDE4E1E26C2A0FEE0FB58E359E9630A842DAC16D43DF73CD62B8`

重要细节：RestrictMcDownload 是编译期常量，相关判断已被编译进各调用者，单独修改字段值不会把那些方法恢复成旧实现。因此这个项目的实际恢复范围取决于它明确修改的两个方法；它没有移植下述整合包安装入口。

## PCL-Re.Download_PATCH

主要依据 `source/RestoreDownload.cs`、`BuildPatch.ps1`、`VerifyPatch.ps1`、`MakeDelta.py` 及现有 `download.patch.gz`：

1. 以限制前 `5ef81e4` 的官方 EXE 为 donor，以 2.99.0.0 的指定官方 EXE 为目标。
2. 移植 `ModModpack.ModpackInstall` 的两个重载、`PageDownloadInstall.LoaderInit`、`PageDownloadModify.LoadInstanceFinished`、`PageDownloadResourceDetail.InitializeStackResourceVersions`。合计五个原有方法，另需处理相关闭包和委托。
3. 移植层重映射类型、字段、方法、泛型参数、分支目标、局部变量和异常处理结构，避免旧程序集引用残留。
4. 验证脚本检查五个原方法 IL 变化、嵌入资源一致，并对指定方法做 JIT 准备。此检查不能替代界面与真实下载验收。
5. MakeDelta.py 将修补后字节表示为“从原文件复制区间”与“新增字节”的操作列表，附输入/输出长度和 SHA-256，再 gzip 压缩。应用阶段核对差分、原文件和输出哈希。

本轮实际解压读取现有差分清单，得到：

- format：1；version：2.99.0.0；操作数：2,908。
- 输入长度：5,980,160；输出长度：5,975,552。
- 输出 SHA-256：`D4FE05B8C713C573FCD46E0EFA1DC9B5463F5EC4FD03C3D9279D523E48F8D0BD`。
- 差分 SHA-256：`6BABD1946A33E433E187683EBBEF6E0910E15144E18E8D97F274E15623BC06A7`，与该项目 HEAD 中应用脚本的固定值相同。

两个项目输出哈希不同，恢复范围及重写工具也不同，不能互用产物哈希或备份判定。

## 对独立下载器的意义

本地 PCL 当前源码的限制主要在加载/安装入口，原有文件规划和高速下载模块仍保留；两个项目均通过恢复这些入口调用既有引擎，没有另造高速下载器。

另一个实际区别是“下载整合包文件”与“创建/安装整合包实例”。当前 PCL 的 ModpackInstall 两个入口都有编译期限制；第二个项目明确恢复了它们，第一个项目的两个定点修改没有覆盖它们。

PCD 继续使用独立 VB.NET/WPF 安装流程，以限制前 PCL 代码为主参考。本轮没有复制这两个项目的补丁实现或内嵌补丁，原 PCL EXE 保持原样。可借鉴的是精确界定恢复范围、安装前校验和成功后再提交的做法。

0.4 据此继续加强了既有实现：Java 和 PCL 路径兼容检查提前到内容下载之前；旧请求不会覆盖新版本选择；异步依赖尊重用户取消；切换目录后保留当前安装配置；传输计数跨安装阶段保留。它仍负责原版及七类组件安装，没有新增整合包安装器。
