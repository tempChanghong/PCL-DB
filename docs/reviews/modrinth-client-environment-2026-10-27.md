# Modrinth 导入：兼容 HMCL 的客户端环境声明

基线：`24de5dda28d4d64888f9bc16650379bd6e60049e`。本轮沿本地整合包读取、选择、安装和取消重试检查，确认一处读取兼容问题。

## 依据与复现

固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Modules/Minecraft/ModModpack.vb`，`InstallPackModrinth` 依据 `env.client` 弹出可选文件选择，跳过 `unsupported` 文件。客户端导入不读取 `env.server`。

HMCL `0d20910ea302a907d6a01fec9244ab10d2993d5e` 的 `ModrinthModpackExportTask` 在导出禁用 Mod 时创建 `env`，只写入 `client: optional`。PCL-DB 却要求环境对象同时具有有效的客户端与服务端字段，因此会在显示可选文件之前拒绝整个包，报“Modrinth server 文本字段无效”。两份原始 Git blob、固定提交和 SHA-256 见 `artifacts/modrinth-client-env/source/manifest.json`。

[Modrinth 官方格式说明](https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack) 描述了可选环境对象、两端枚举及用户可选文件，并给出了包含两端的示例。本轮将缺少 `server` 视为对已有 HMCL 导出行为的兼容，不声称规范明确保证该简写。

使用同一缺字段包、保持原生产实现构建，`--regression modrinth-environment` 在第一个客户端环境包上退出 1；不是编译失败或模拟异常。原始错误保留在 `artifacts/modrinth-client-env/baseline-environment.log`。

## 修改

只放宽环境对象中缺少 `server` 的情况。`client` 仍必需且必须为 `required`、`optional` 或 `unsupported`；提供了 `server` 时仍校验其类型和枚举。空对象、缺少客户端、空值和非法声明继续拒绝，不猜测应该下载哪些文件。

没有改动可选文件 UI、返回／切换及动画、下载策略或安装事务。原路径约束、双摘要校验、逐文件选择、隔离、已有目标保护和原子发布继续生效。

## 验证

新增 `ModrinthEnvironmentChecks` 25 项，接入默认 Core 全套及 `modrinth-all`，也可通过 `modrinth-environment` 单独执行：

- 客户端单字段同时覆盖必选、可选、不支持客户端；明确的三种服务端值不改变客户端选择。
- 缺少／非法／空客户端、空环境对象、非法／空／非文本服务端仍拒绝。
- 分别选择、不选择可选文件，走真实回环下载及安装；在发布前取消并重新读取包后重试，检查无半成品实例、已校验文件零网络复用和下载请求选择。
- 检查文件字节、共享 Mods／全局配置保持、实例隔离与包内其他设置保持；已有目标在网络请求前拒绝。

本轮执行完整 Modrinth 组 **153 项（含新增 25 项）**，涵盖格式／路径、双摘要、URL 回退、分段下载、取消续传、分层覆盖、可选路径冲突、配置变化、已有实例和原子发布。另行执行已有原生隔离 **148 项**，覆盖 HMCL／PCL-CE 原生设置、整合包隔离及外部配置保持。新增安装夹具使用 PCL 隔离配置；原生组为独立既有检查，不将其描述为新增缺字段包在每种启动器中的桌面实测。

Release 重建零警告、零错误。验证契约 97 项、限时策略 44 项、验证进程 19 项通过；固定 44 份 PCL 源码快照和既有控件／资源核验通过。最终源文件清单、二进制摘要与实际执行范围见 `artifacts/modrinth-client-env/validation.json`。

全部包与游戏文件均为临时夹具，下载仅访问回环服务器；没有读写用户的实际游戏实例或外部启动器配置。未执行完整 UI、搜索、Core 全套、桌面帧率或真实外部启动器运行验证；不打包、推送或发版。
