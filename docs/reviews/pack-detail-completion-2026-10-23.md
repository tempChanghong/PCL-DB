# 整合包详情完成状态与重试修复

日期：2026-10-23。基线：`6ccf587`。验证后本地 Commit，不推送、打包或发版。

## 问题与结果

原逻辑以版本列表数量判断详情是否已经加载。接口成功返回空清单后，返回搜索再打开同一项目会再次请求项目及版本信息，并重新显示加载卡片。离线夹具已复现：两类请求均由 1 次增加到 2 次，中性空结果提示正常但未被缓存；回归断言在此失败。

改为记录已成功完成的项目所有者。完成与结果数量分离，成功的空清单和非空清单采用相同缓存策略；加载中继续共享原请求。缓存范围仍限当前选中项目和搜索上下文，未引入持久化或跨项目缓存。

| 当前状态 | 返回后重进详情 |
| --- | --- |
| 同一项目仍在加载 | 保留原请求及加载卡片 |
| 同一项目成功完成，包括零个可下载文件 | 使用已有元数据、结果及提示 |
| 请求失败或取消 | 允许重新请求 |
| 切换项目或接受新搜索 | 清除旧完成状态 |
| 现有重试入口主动发起加载 | 清除完成状态并重新查询 |

发起加载前核对当前项目，拒绝过期项目调用；只有未取消且仍属于当前请求／项目的成功响应可以标记完成。清单渲染成功后才接受完成状态，失败路径会清除它。旧响应和旧请求的 Finally 不得修改新项目的完成状态或请求引用。

同时验证：已有非空清单在刷新失败后，保留的文件数量也不能阻止下一次重进时重试。详情请求的取消令牌与安装任务继续独立，未改变版本隔离、共享文件、下载校验、安装器和原子发布。

## 固定 PCL 源码依据

读取并保存未限制版提交 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的四份 Git 文件及规范化行尾后的 SHA-256：

- [ModLoader.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Base/ModLoader.vb)：LoaderTask.ShouldStart 按输入、Loading／Finished 状态、强制重启及 ReloadTimeout 决定是否加载。默认 ReloadTimeout=-1；不以 Output.Count 判断成功，失败／取消可以重新启动。
- [MyPageRight.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Controls/MyPageRight.vb)：PageOnEnter 按加载器状态选择内容或加载层；PageLoaderInit 将完成回调和页面动画衔接。
- [PageDownloadCompDetail.xaml.vb](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/Resource/PageDownloadCompDetail.xaml.vb)：项目版本来自 ResourceFileLoader，Load_OnFinish 和 UpdateFilterResult 将成功输出转换为筛选及惰性版本卡片。
- [PageDownloadCompDetail.xaml](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/Resource/PageDownloadCompDetail.xaml)：简介、版本结果与独立加载卡片的结构。

本项目沿用自己的中性“无可安装版本”提示，未声称 PCL 源码包含相同文案；也未更改当前的筛选、展开状态缓存和动画曲线。

## 验证与工具冲突

新增 15 项整合包检查，覆盖空结果完成、加载中往返、成功结果复用、显式刷新、失败后恢复、取消与重进、不同项目及迟到响应、过期显式调用、新搜索释放完成所有者，以及旧非空文件不能掩盖失败状态。检查同时核对搜索对象／结果行及安装任务未被详情查询替换。保存成功空结果和刷新失败两种隐藏 WPF 渲染截图。

扩大套件后，实际运行发现验证契约脚本有两处将完整搜索检查数写死为 171。已改为读取当前策略，并检查旧的 171 项回执和当前少一项的 185 项回执不能通过当前候选验证。用于错误类型测试的字符串／小数样本及历史发行包规则保持原用途。修复后工具契约 97 项通过；它们使用临时解析夹具，不生成新发行包。

当前策略为 `PCL-DB-checks/2026-10-23.7`：Core 5741、UI 799（含 navigation 68）、motion 554、pack-search 186，主要四套合计 7280 项断言。固定 PCL 源码／资源及验证进程工具继续运行。最终通过状态和摘要以回执为准。

```powershell
dotnet build PclDownloadBridge.sln -c Release --nologo
# 定向搜索检查通过 VerificationProcessTools.psm1 等待真实独立进程退出。
./build.ps1
```

忽略目录 `artifacts/pack-detail-state/` 保存固定源码、修复前失败、请求次数、定向回归、工具冲突及修复后的契约报告、最终构建与完整回归。`validation.json` 和 `committed-validation.json` 绑定源码清单、二进制、回执及提交。

这是离线 HTTP 夹具、真实 WPF 隐藏呈现源布局／渲染和受控异步时序验证；未做真实联网下载、真人桌面或帧率验收。本轮不产出发行工件。
