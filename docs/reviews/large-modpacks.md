# 大整合包固定容量上限修复

基线 `8af637b2bbdfb88ea40f9d89be5710ae14e94a53`（Beta 4.0），参考固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 ModModpack。修复纳入 Beta 4.1，不替换已发布的 Beta 4.0 工件。

## 实际问题

用户提供的「乌托邦探险之旅3.5.2」ZIP 与 MRPACK 均包含 `resourcepacks/乌托邦自定义音乐版本3.5.zip`，该文件为 **218261798 字节**。两包展开总量分别为 834491796 / 833762737 字节，真正触发的是解析器的 **128 MiB 单文件硬上限**，并非总量超过 2 GiB。

实际调用两个格式读取器，修复前均在相同条件抛出「整合包解压内容超过大小限制」。原始报告位于 `artifacts/large-modpacks/before/samples.json`。

## 修改

- 移除 CurseForge、Modrinth 本地归档的 128 MiB 单文件及 2 GiB 展开总量限制。
- 同时移除在线版本清单和归档获取的 2 GiB 大小筛除，以及 Modrinth 文件预计大小的 16 GiB 上限；大小继续使用 Int64，不能本地接受而在线静默丢失。
- 原有 CopyOverrides 以 64 KiB 缓冲区提取并计算摘要，不需要将整个文件载入内存；保留声明长度、摘要、路径/重名冲突、链接条目、实例设置合并、取消和原子发布检查。小型 JSON/配置的格式与长度检查保持。

PCL ModModpack 按清单识别两种格式，并通过 ExtractModpackFiles / CopyOverrideDirectory 安装覆盖文件，在这一层没有上述 128 MiB / 2 GiB 筛除。本轮保留本工具已有的流式提取与发布事务，不将 PCL 的依赖库内部实现视为已逐行验证。固定文件的精确 Git blob 与 SHA-256 记录于 `source/manifest.json`。

## 验证

新增 `large-modpacks` Core 回归，共 12 项：真实压缩的超过 128 MiB 文件在两种格式下解析、流式提取并核对 SHA-256；17 个各为 128 MiB 的真实 ZIP 条目验证展开总量超过 2 GiB；两平台超过 2/16 GiB 的版本大小保留、获取取消，以及 Modrinth 大文件声明。夹具流式生成，不依赖用户归档。

对实际两包分别解析 Minecraft **1.20.1**、Fabric **0.18.4** 和 413/415 条下载声明；在私有目录中提取覆盖内容、在音乐包提取途中取消，再以新目录重试并逐文件核对长度和摘要。CurseForge 校验 **2693** 个文件 / **834426944** 字节，Modrinth 校验 **2691** 个文件 / **833577277** 字节。最终样本报告为 `artifacts/large-modpacks/after2/samples.json`。

该样本验证不下载全部外部依赖，也不启动游戏；完整安装事务及取消发布仍由 Core 的隔离集成回归验证。用户原包和真实游戏目录不改写。Beta 4.1 的完整回归、明确单 EXE 候选、打包及公开下载证据以 `artifacts/beta-4.1-release/` 与发行附件中的 verification.json 为准。
