# 页面入场动画的同步重入

基线 `eca8935ea7eba36cfb32c321dda9834ae13ab609`。本轮修改共享 PclSurfaceMotion 和资源内容合成辅助分支，不改 Core、下载／安装、外部配置、主题配色、动画曲线、错峰及持续时间。

旧控制器在执行内容委托或 incoming.UpdateLayout 时，允许这些通知同步接受另一个页面，但返回后仍给同一内容挂旧入场时钟。专项四组绑定／布局×有／无退场快照均复现：新时钟被替换，卡片重复包裹两层变换、数量从 2 变 4；Finish 后页面与卡片透明度都为 0，原变换未还原。另四组在绑定／布局期间调用 Cancel／Finish，旧操作仍会在停止后挂钟并隐藏已接受内容。

每次 Change、Cancel、Finish 现在更新入场代次，在快照、旧时钟清理、内容绑定、布局及可见单元收集边界复查；过期调用不再挂钟，异常清理也只处理仍由本次拥有的动画。正常内容测量可能改变 host 大小，此时尚无时钟的 SizeChanged 不取消新入场；已有运行时钟时，缩放仍会完成并还原。

取消的语义变严格后，资源辅助分支原先的 FinishPackContentMotion 会误停同一控制器正在绑定的父入场。集成检查实际发现两项失败：仅改控制器时 33/35，缓存详情失去入场／揭示时序。辅助分支现在只完成未参与本次绑定的子控制器，嵌套更新继续由父入场收集最终卡片。明确调用 Cancel／Finish 仍会停止，不能把合成误当成取消，也不能撤销新操作。

参考固定 PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`：ModAnimation.vb:753–783 的同名 AniStart 先 AniStop，MyPageRight.vb:236 起强制退出先改变 PageState 并停止页面动画，FormMain 先更新 PageCurrent 再换 UI。C# 参考 CE `915ac29795efbdf6373417389e181e1504117048` 对应 ModAnimation.cs:1497–1526、MyPageRight.cs:287 起及 FormMain。固定 Git 原字节共 10 份见 `artifacts/surface-entry-reentry/source/manifest.json`。本地 WPF 代次保护是本工具对同步重入的适配，不声称上游存在同名补丁。

新增独立 VB 检查 35 项，仅接入 motion，旧入口除追加这一调用外原字节不变。最终两侧使用相同检查源码、私有入口及冻结 Core／JSON，仅两个业务文件不同：旧代码 11/35、24 项失败；修复后 35/35。纯控制器检查外，包含真实资源详情合成、明确停止、180 ms 揭示、迟到完成、原始透明度／变换、正常与禁用动画、无刷新快速路径，以及布局测量与运行中缩放。最初 30 项、随后 32 项和中间 33/35 候选记录分别保留，不混为最终结果。

策略 `PCL-DB-checks/2026-10-29.12`，仅 motion 增加 35 至 6011。最终正式单 EXE 在新中文／空格冷目录中串行新验：standalone 25、UI 6662、motion 6011、搜索 3444；内嵌两依赖及七份根文档与当前源码／构建一致。另查 VS2026 Debug／三 Portable PDB、验证契约 110。Core 输入及 Release DLL `5D6EABC0…E7F2AA5` 未变，`3c143de61af42a3f9814ffac26e729a7259ba8a0` 的 Core 7814／旧 VB 宿主 7305／API 979、1245／资源 2／JSON 15 是精确复用证据，不是本轮重跑。

最终源码、二进制和提交树绑定见 `artifacts/surface-entry-reentry/validation.json`。只有主代理自查，未冒称独立复核。覆盖已复现的绑定／布局交接及真实详情合成，未穷尽所有依赖属性通知中 StopOwnedMotion 本身的重入和任意异常；离屏 WPF 不代表自然帧率、真人桌面／F5 或完整联网安装验收。未改用户实例、外部配置，未打包、推送或发版。
