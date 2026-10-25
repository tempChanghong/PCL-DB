# 社区资源搜索、版本显示与动画修复

基线 `131fd90b84cd48b8bdccd7919a8650c02eb7ebd4`。处理用户提供的四张截图；保留原下载安装、三启动器隔离、共享目录、文件冲突与原子发布规则。仅本地 Commit，暂不推送、打包发版。

## 复现与修复

1. **缓存分类返回后空白。** 原检查在断言前手动结束所有动画，没有覆盖真实结束顺序。本轮增加实际计时的缓存分类往返，稳定复现结果卡片透明度停留在 0。缓存响应可以在父级切换的内容更新回调内同步完成，此时父级尚未建立入场时钟，子级错误地另开入场，双方保存对方的透明状态。现在显式标记父级内容更新事务，同步结果由父级统一收集；取消分类请求时同步清理旧加载控制器。未改原有缓动、时间与输入门控。
2. **搜索图标贴住文字、Mod 缺空格。** 搜索图标右边到文本/占位内容的距离从 4 DIP 增至 12 DIP，两种内容一起调整；参照 PCL `PageDownloadMod.TypeNameSpaced`，显示“搜索 Mod”“正在获取 Mod 列表”，中文资源名称保持原格式。
3. **CurseForge 项目显示“版本未知”。** 原解析只读取了加载器，完全没有保存项目游戏版本。现在参照 PCL 合并 `latestFiles` 与 `latestFilesIndexes` 的明确游戏版本，去重并排除 Forge、Java 等非版本标签。先确定资源类型，再接受对应 JAR/ZIP 声明；搜索和详情使用相同读取逻辑。该声明只供展示，安装仍校验选中文件自己的版本，不推测最低版本或完整兼容范围。
4. **Fresh Animations 没有可下载版本。** 实际返回的公开文件有 ID、文件名、大小和摘要，但 `downloadUrl` 为空，原程序直接略过。参照 PCL 从文件 ID 和转义文件名构造 ForgeCDN 地址；继续检查项目、公开状态、客户端文件类型、大小、摘要与精确 CDN 路径。接口明确返回错误路径时仍拒绝，不用回退掩盖冲突；没有摘要的文件仍不可下载。

## 源码与接口依据

主参照是本地固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8`：`ResourceProject.vb`、`ResourceVersion.vb`、`ResourceSearcher.vb`、`PageResource.xaml/.vb`、`PageDownloadMod.xaml`、`MyPageRight.vb` 和 `FormMain.xaml.vb`。Git 对象、路径和摘要另存于本轮 artifacts/source/manifest.json，未修改上游仓库。

接口字段按 [CurseForge 官方 REST API 文档](https://docs.curseforge.com/rest-api/) 核对；[官方依赖下载文档](https://docs.curseforge.com/docs/game-integration/unreal/mod-dependencies/) 也说明下载链接可能为空。回退实现以固定 PCL 源码为直接依据。实际样本为 [Fresh Animations 文件 9262896](https://www.curseforge.com/minecraft/texture-packs/fresh-animations/files/9262896)。

## 验证与边界

策略 `PCL-DB-checks/2026-10-25.12`，Release 构建零警告、零错误。

| 检查 | 数量 | 覆盖 |
| --- | ---: | --- |
| 完整 Core 回归 | 6,961 | 原安装/隔离/共享/取消重试，加 32 项资源目录与地址回退断言 |
| 完整界面 | 1,219 | 原版、整合包、管理、设置与共用控件 |
| 动画 | 1,285 | 原有页面和控件动画、生命周期、门控、清理 |
| 搜索/详情 | 607 | 原 590 项及本轮 17 项；含原资源安装联动检查 |
| 导航 | 228 | 完整界面子集 |

17 项专项通过真实搜索按钮与自动搜索入口覆盖五类资源、慢请求、加载中切换、迟到取消、重试、缓存恢复、快速反向、紧凑窗口、明暗模式与图文间距。缓存返回的关键断言使用真实动画结束顺序，不调用手动结束动画来掩盖结果。截图为隐藏 WPF 表面采样，不代表人工桌面体验或帧率验收。

独立实际联网检查 CurseForge/Modrinth 的 Mod、整合包、资源包、光影包与数据包，共 10 组搜索→详情→版本调用。CurseForge 样本包括 GeckoLib、All the Mods 10、Fresh Animations、Complementary Shaders - Reimagined、Supplementaries Compat，均获取到版本。Fresh Animations 返回 28 个可下载版本；使用程序原下载、校验与另存流程保存 `FreshAnimations_v1.11.1_26.1+.zip`（776,684 字节），独立 SHA-1 复核 `ae9175983c5109162c86daae1acff34ccf623faf`，与接口声明一致。下载仅在 artifacts 下进行；没有解压执行或写入用户实例。

本轮证据在 `artifacts/resource-search-repair/`：失败复现、专项与完整回归收据、10 组联网目录读取、Fresh Animations 实际下载回执、明暗截图、固定上游源码，以及测试源码清单与提交核对。最终验证依据 validation.json 与 commit-receipt.json；构建和收据摘要核对后才提交。

此修复尚未进入 Beta 4.1 已发布工件。其他网络、所有资源版本的游戏内兼容性、用户手工体验不在本轮实测范围。
