# 整合包版本下载统计与时间呈现对齐

日期：2026-10-23。基线：`e0c20ce`。本轮处理对照报告 S-04，验证后本地 Commit，不推送、打包或发版。

## 当前差异与结果

原版本模型没有下载计数，版本行只显示日历日期。修复前搜索回归在“使用 PCL 相对更新时间”断言失败；这是外观／信息还原性差异，不是下载失败。现在 CurseForge 的 downloadCount 和 Modrinth 的 downloads 进入各自发布版本模型，版本行例如显示“Fabric，下载 88715 次，更新于 2 天 13 小时前，测试版”。

沿用 PCL 的正数显示规则：100000 显示“100000 次”，100001 显示“10 万次”；半数按 Math.Round 默认偶数舍入，不另设“亿”分支。零值不显示，避免 CurseForge 返回错误零值时产生“无人下载”的暗示。提示保留完整计数和精确到秒的本地更新时间，统计只代表当前平台，未跨平台合并。

统计是辅助字段。可空 Long 保留未知与已知零的区别，接受非负且在 Int64 范围内的 JSON 整数／整数值浮点数；缺失、null、文本、对象、数组、布尔、负数、小数和溢出降级为未知。此规则只用于新增计数字段，文件大小、地址、平台／项目／文件 ID、SHA-1／SHA-512／MD5、游戏版本与隔离校验不放宽。

时间格式使用与 PCL 相同的详细分支：秒／分钟／小时／天可以显示两个单位，月按 30 天计算；未来时间使用“后”，零或不足一秒按“1 秒”处理。每张版本卡片共用一个显式当前时间，按构造时刻生成文字；保留卡片缓存，不新增计时器、自动重建行或每帧布局。

## 固定源码与许可

本轮实际读取并保存：

1. [PCL ResourceVersion.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Resource/ResourceVersion.vb)：FromPlatformJson 的计数，以及 ToListItem 的格式、顺序与相对时间调用。
2. 同一 PCL 提交的 [.gitmodules](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/.gitmodules) 和 MeloongCore Git 子模块对象：固定提交为 `c774ae4c85fd94e0eaa171db21e78a65870df19b`，未使用本地当前分支或上游最新版本代替。
3. 此子模块的 [StringUtils.cs](https://github.com/Meloong-Git/MeloongCore/blob/c774ae4c85fd94e0eaa171db21e78a65870df19b/Shared/Utils/StringUtils.cs) 和 [LICENSE.txt](https://github.com/Meloong-Git/MeloongCore/blob/c774ae4c85fd94e0eaa171db21e78a65870df19b/LICENSE.txt)。通过固定 Git tree 的 blob SHA-1 和原始文件 SHA-256 核对下载字节。仅将 FormatTimeSpan 的 isShort=false 分支适配为 VB.NET；完整 Apache-2.0 许可、来源与修改说明加入现有 THIRD_PARTY_NOTICES.md，继续随现有打包入口携带，不增加程序集或 NuGet 依赖。

适配差异：上游下载计数为 Int32，本工具使用可空 Int64，以 Decimal 做整数舍入；数字格式不依赖 Windows 区域设置。相对时间以 DateTimeOffset 比较实际时刻，避免同一时刻因时区偏移不同而显示错误间隔。完整计数提示和异常辅助字段降级属于本工具补充，不声称上游包含相同实现。

## 验证

- 新增 105 项 Core 断言：两平台各覆盖 6 种有效计数、10 种未知／异常声明及独立快照；另覆盖计数阈值／偶数舍入／Int64 极值、详细时间单位边界、过去／未来、时区和不同数字文化。
- 将固定 StringUtils 原函数的完整方法体直接编译为独立参考，在边界前后和固定随机样本共 1120 组时间跨度中逐项对照 VB 结果。该回执绑定实际 Core DLL 和固定源码摘要，不计入 Core 套件数量。
- 新增 9 项搜索界面断言，覆盖相对日期、下载量、精确提示、未知和零计数、未来日期、固定当前时间、原行／卡片身份及安装确认快照。既有同名版本、取消／重试、返回和文件身份断言继续保留。复查亮色和深色的实际隐藏呈现源截图。

当前策略 `PCL-DB-checks/2026-10-23.10`：Core 5846、UI 799、motion 637、pack-search 204，四套合计 7486 项断言；工具契约另有 97 项。固定源码／资源及验证进程工具继续运行。最终通过状态及摘要以回执为准。

```powershell
./build.ps1
# 搜索定向检查通过 VerificationProcessTools.psm1 等待真实进程退出。
# 固定来源函数对照脚本位于 artifacts/release-metadata/verify-source-format.ps1。
```

忽略目录 `artifacts/release-metadata/` 保存固定来源、修复前失败、目录回归、函数对照、明暗截图和完整回归。validation.json／committed-validation.json 绑定测试源码、程序、报告与最终提交。

本轮未修改搜索排序、分页、版本筛选／展开、页面动画、归档缓存键、共享文件、版本隔离及原子安装发布；没有新增真实联网目录、游戏安装或真人桌面验收。源码获取的联网证据仅用于固定来源核对，不代表运行时服务可用性。
