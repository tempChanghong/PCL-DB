# RC1／Infdev 补充版本接入

本批依据早期版本研究第二至第六阶段报告继续实施；此前七版本的启动配方门控已在 `d7d3940` 完成。新增 `1.0.0-rc1` 与 `inf-20100630-1`，其余候选不批量开放。暂不打包发版。

## 参照的 PCL 代码

固定提交：`5ef81e490ae46b1100d61e33d0183094c7a80ca8`。

- `ModMinecraft.vb` 的 `McLibListGetWithJson`：Maven、rules、Windows natives、extract 共同决定实际支持库。本工具继续要求摘要和大小，不退回无摘要的 Maven 下载。
- 同文件 `McAssetsGetIndex`：启用 legacy 回退时的固定声明。本批沿用该 SHA-1、大小、totalSize 和 URL，但仅准许两个已核对版本在两个索引字段都缺失时补全。
- `McAssetsGetIndexName`、`McAssetsListGet` 及 `ModLaunch.vb` 的变量替换：私有索引名不能改变 `assets/virtual/legacy`，`game_directory` 仍由 PCL 的实际隔离路径决定。

源码：[固定 ModMinecraft](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb)、[固定 ModLaunch](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModLaunch.vb)。本地读取对应固定 Git 对象导出的源码。

## 最终行为

|范围|行为|
|---|---|
|身份、分类|RC1 保留 `old_beta`，Infdev 保留 `old_alpha`；元数据地址逐项限制，普通官方同名条目优先|
|页面与动画|追加至已有「远古版本补充」，使用已有行图标、展开／切页动画及仅原版安装流程，不重建普通行或进入最新推荐|
|库参考|固定官方 `inf-20100618`，SHA-1 `51a5c512af384d3d2a79a3efb93f7d4b9a1c6ec2`；去掉 downloads 后全部字段必须匹配，不按年代猜参考|
|启动配方|LaunchWrapper 1.6，显式 AlphaVanillaTweaker；继续执行已有主类、参数、引号和 wrapper 摘要／大小门控|
|缺失资源索引|只在这两个版本的 `assetIndex`、`assets` 都不存在时补入 PCL legacy 声明；不复制库参考自己的 pre-1.6 索引|
|损坏声明|null、错误类型、索引缺摘要、参考摘要失败或索引下载失败均拒绝，不用缺失字段回退掩盖|
|共享资源|索引按内容命名为 `pcldb-<sha1>`，virtual 文件仍在根目录 `assets/virtual/legacy`，沿用校验、复用及并行逻辑|
|已有实例|完整实例可补全；保留 JSON 原字节、参数及 PCL/Setup.ini。原始不完整外部档案得到专门提示，不隐式迁移|

补全只操作深克隆；源客户端、启动参数与源 JObject 保留。所有补充版本的显式 `downloads: null` 或非对象类型均改为损坏声明，只有属性真正不存在才能申请固定参考补全。普通官方版本不套用此规则。

legacy 索引 SHA-1 为 `c0fd82e8ce9fbc93119e40d96d5a4e62cfa3f729`，大小 134284，totalSize 111220701。13 项原始库声明经平台筛选得到带摘要的 Windows 计划；不引入新的组件安装器或运行补丁。

## 验证

自动化新增检查覆盖：真实原始两份元数据与固定参考、分类与同名冲突、错误索引／null 声明、完整库规则匹配、参考摘要失败与取消、传输中取消、发布前取消、同名重试、UTF-16 自定义实例、损坏客户端补全、已有隔离保留、外部不完整档案拒绝、共享普通索引保留与暂存／锁清理。

```powershell
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-early
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-recipes
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-versions
./build.ps1
```

本轮在线检查通过九个补充版本的清单、元数据和索引摘要；确认官方 rd-132211 仍是普通条目。该在线检查没有下载游戏载荷。

另以两项真实客户端、库及 legacy 资源进行生产入口集成，`IsSupplemental=True`，未绕过允许表或元数据补全。原始元数据、固定参考与索引经本机 HTTP 传输；真实载荷从既有研究缓存按摘要和大小校验复用。每项发布计划 1729 文件，确认 1120 个 virtual 逻辑文件、中文空格实例名称、发布取消后同名重试、损坏客户端修复和 UTF-16 JSON／隔离设置保留。安装与补全均无载荷网络下载；这不是首次联网下载性能测试。

本地日志、进程回执、在线结果、真实载荷结果及源码绑定保存在 `artifacts/early-supplemental-fix/`，此目录不进入 Git。

## 保留的验收与下一步

没有启动 PCL 界面或 Minecraft；实际 PCL 列表刷新、适合的 Java、游戏启动、默认目录落点和存档仍需人工验收。安装成功与配方检查不代表运行兼容性已验收。

Classic c0.30-1 与 Indev in-20100223 的工作目录问题继续暂缓。下一步优先完成上述两项的真实 PCL／Java 启动和存档验收，再独立推进 Classic／Indev 的目录适配；不能只换 tweaker 后扩大允许表，也不自动迁移外部原始不完整实例。
