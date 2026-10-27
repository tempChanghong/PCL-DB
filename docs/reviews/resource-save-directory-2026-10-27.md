# 旧版纹理包：另存为与实例安装使用同一目录规则

基线：`c627f5780e8d61698cccb28f6aac9eda467b565d`。本轮检查资源下载和管理上下文之间的默认路径，发现 `ResourceSaveDirectory` 将资源包目录固定为 `resourcepacks`，而安装内核已按旧版游戏选择 `texturepacks`。

## 源码依据与影响

重读固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `PageDownloadCompDetail` 默认保存位置和 `PageInstanceExport` 目录收集逻辑。上游根据资源类型和实例实际路径选择默认目录，导出同时收集 `resourcepacks`、`texturepacks`；但其另存为入口本身固定使用 `resourcepacks`。两份原始 Git blob 和 SHA-256 保存于 `artifacts/resource-save-directory/source/manifest.json`。

PCL-DB 已有旧版纹理包安装兼容规则。以已管理的 1.5.2 实例为例：指定实例安装目标是 `texturepacks/resource.zip`，另存为却优先进入 `resourcepacks`；该目录不存在时退回运行根目录。用户需要重新找目录，共享与隔离实例均受影响。

## 修复

把安装内核的既有版本判断命名为公开只读函数 `UsesLegacyTexturePacks`，用于安装兼容性、安装目录和另存为默认目录。判断内容不变：本轮不扩展未知快照或历史版本的兼容边界。

有有效实例上下文时，旧版纹理包使用 `texturepacks`，现代资源包使用 `resourcepacks`。无匹配实例时继续使用所选游戏根目录的现代资源目录；子目录不存在时退回实际运行目录，不创建文件夹。Mod、光影包和数据包的默认位置不变。

不改写启动器或实例配置，不移动已有文件，不改变安装发布事务、取消重试与页面动画。默认位置只是保存对话框的起始位置，用户仍可另选路径。

## 验证

新增 48 项界面上下文检查：PCL／HMCL／PCL-CE × 共享／隔离 × 1.5.2／1.6.4，每组检查缺少目录时的只读回退、安装与保存路径一致、本地 Mod 上下文及其他资源类型、切换启动器后忽略旧实例并保留配置字节。使用临时原生配置与真实实例解析器，不启动下载任务。相同最终夹具下旧实现 12 项失败，修复后专项全部通过。

Core 的 resource-installation 回归增加旧版判断边界断言，覆盖正式版、Alpha／Beta、现代版及未知版本回退；同时复验既有资源发布、共享／隔离、文件冲突、存档变化和取消检查。

新增界面检查注册到 all 和 pack-search；没有新增视觉动画逻辑，因此不重复注册到 motion。策略 `PCL-DB-checks/2026-10-27.16` 的注册数为 all 3,342、motion 2,764、pack-search 1,817，navigation 保留 1,852。**本轮实际执行专项、完整搜索组和 Core 的 resource-installation 组，不执行完整 all／motion 或 Core 全套；各组有重叠，不累加。** 搜索组继续覆盖现有切页、返回、图片、弹窗、输入与取消重试动画。

另验构建、固定 PCL 来源／资源、验证契约、限时策略及验证进程。最终结果和具体执行数见 `artifacts/resource-save-directory/validation.json`；界面检查是离屏和临时配置证据，不代表桌面帧率或远端服务可用性。本轮不打包、推送或发版。
