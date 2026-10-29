# 整合包覆盖文件发布前校验的取消响应

基线：`a0046e327cda06611f6c24984bbe7ca67c863d14`，Beta 8 后续源码，尚未发行。

## 问题与边界

CurseForge ZIP 和 Modrinth MRPACK 共用 `ModpackInstallPlan.ValidatePayload`。复制 overrides 时已经逐块检查取消，但写入安装记录前的 SHA-256 复校验使用整流 `ComputeHash`，只在开始每个文件前检查 token。大文件散列开始后取消，会继续读取整个文件，再由后续检查触发回滚。最终发布前仍有取消检查；问题是取消响应延迟，而不是已证明取消后错误发布。

本批仅替换覆盖文件复校验中的散列读取：每次同步读取前检查原 token，以 64 KiB 分块执行 `TransformBlock`，EOF 后再次检查取消，再执行 `TransformFinalBlock`。保留原句柄和 SHA256 的 using 作用域、原大写十六进制比较与错误文本。空文件仍完成标准 SHA-256，短读继续读取，不将单次短读当作结束。

安装入口、公开及友元接口、VB 界面、配置格式、三种启动器隔离策略、路径锁、共享文件、ledger 格式和实例目录原子移动顺序不变。取消异常保留原令牌，由现有收尾释放句柄、删除本次私有 payload；允许保留空暂存容器。重试仍可复用已校验下载。

## 固定源码依据

- PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`：`Plain Craft Launcher 2/Modules/Minecraft/ModModpack.vb:112-115` 在提取前检查取消并传入 `FileUtils.ExtractToDirectory`，CurseForge 与 Modrinth 任务分别在 `:209`、`:405` 传入任务令牌。
- PCL-CE `dev` `915ac29795efbdf6373417389e181e1504117048`：`PCL.Core/Utils/Hash/SHA256Provider.cs:36-38` 与 `IHashProvider.cs:11` 的流哈希支持原取消令牌。本项目保留 .NET Framework 4.8，不照搬现代 `SHA256.HashDataAsync` API。
- 本项目 `CurseForgePackage.HashArchive` 已使用分块哈希，但它会回绕流并输出小写，故不直接替代此次只读复校验。

固定 Git 对象原始字节和 SHA-256 见 `artifacts/pack-payload-cancellation/source/manifest.json`。

## 验证与证据

复现、专项回归、完整 VB 回归、API/资源/配置兼容、VS 2026 Debug 和单 EXE 检查的最终结论统一保存在 `artifacts/pack-payload-cancellation/validation.json`。该回执绑定实际候选 DLL/EXE、源码清单和提交树，不把不同构建的结果混为一组。

测试使用进程内标准 CryptoConfig SHA256 provider 包装真实算法，在覆盖文件最终复校验开始后注入取消并记录实际散列字节，避免计时门槛或大物理文件。测试必须恢复原 provider 和回调；现有回归及断言保持，不通过放宽等待或取消条件取得通过。

冻结的新测试在独立进程中仅替换 Core DLL：旧实现四处预期失败，修复实现通过 42 项；两侧测试 EXE 和其他运行时字节一致。旧实现首块取消后继续散列 524307 字节／129 块，修复后 CF／MR 完整安装和直接校验均在 65536 字节／1 块后抛出原令牌的取消异常。正常摘要、空文件、最后数据块取消、同长度篡改、ledger 保留、句柄释放、私有 payload 清理、同名重试、隔离及共享配置保留均有覆盖；CryptoConfig 两别名的实际类型和两个回调恢复由断言确认。EOF 后检查由源码审查确认，不把最后数据块取消误称为可控的 EOF Read 内取消。

基线复现与本次修复专项见 `red-green.json`。首次候选专项将整个 staging 分支为空作为清理条件，与原来只删除任务私有 payload 的契约不符；确认实际仅剩空容器后修正新 fixture，并保留首次失败回执。最终 DLL 另跑 42 项专项和完整 VB 7510 项（原 7468 + 新 42），旧 Beta 8 已编译宿主 7305 项、979 个 public／1244 个友元入口、两资源／15 个 JSON 配置样本、Debug 和冷启动结果以总回执为准；套件重叠不累加。

## 限度

本批没有改写非 override 下载文件的 `SafeFiles.Matches` 或原生配置的 `RefreshNativeHashes`；这些路径仍有同步整流散列。本补丁不能中断已经进入的单次同步 Read，只保证块间观察取消，不承诺固定毫秒延迟。

受控 HTTP、临时实例及隐藏 WPF 验证不代表完整在线下载、游戏启动、人工 F5 或桌面帧率验收。未打包、推送或发版。
