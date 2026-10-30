# 第四轮第七批：原版安装页水平切换时钟

基线 `30a405460d160fe55b86369198c99e1da4f1757f`。本批将 `PclPageSwitch` 的纯 `TransitionPlan` 和专用水平 ClockGroup 执行器迁到内部 C# `PageSwitchMotion`；原 VB 类型及入口、逻辑选择、卡片并行动画、输入状态、`_finishing`、最终 Settle 和事件 sender 保持。继续使用 .NET Framework 4.8，不修改程序集、配置或资源名称。

## 固定源码依据

实际读取 PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Pages/PageDownload/PageDownloadInstall.xaml.vb`：EnterSelectPage 72–103、ExitSelectPage 116–147；C# 参考 PCL-CE `915ac29795efbdf6373417389e181e1504117048` 对应 `.xaml.cs` 的 EnterSelectPage 168–284、ExitSelectPage 286–326。两者保留 10 ms 后退场、70 ms alpha/90 ms X、100 ms 后进入、70 ms alpha/160 ms X、第五次幂 EaseOut。兼容基线是当前 PCL-DB；公开上游源码不等于全部发行版行为的证明。

## 迁移边界

C# 构造只保存两页面、两个原 TranslateTransform 和固定的同步完成回调，不写 WPF 属性、不提前回调或新增参数校验；在 VB 首个可观察属性写入前完成初始化。Transition 的唯一状态在 C#，原 VB getter 和卡片 Settled 条件实时读取该状态。

Start 保持从当前有效 opacity/X 生成原四子动画计划，CreateClock、发布所有权、挂接时间/完成回调，再按原顺序挂接四个属性。Start 不额外 Stop、不新增暂停或取消策略。100 ms 的隐藏逻辑只作用于进入安装页；完成回调先核对旧时钟身份，再 Stop，随后同步进入原 VB Settle，并在 Stop 之后读取当前卡片动画状态。主页面在原 260 ms 可释放交互，卡片剩余 rebound 和局部门控继续独立运行。

Stop 保持 capture 旧时钟 → 清所有权 → Controller.Remove → 两页面 Opacity → 两个 X 的 detach 顺序；即使没有所属时钟也仍清理四个属性。没有复用 detach-before-Remove 的通用 MotionSlot，也没有套用无时钟即返回的卡片高度清理规则。VB SelectInstall、Finish、卡片使能回调、事件和异常处理顺序保留，不顺带修改整个导航状态机。

## 验证与证据边界

新增永久检查通过原 VB 类执行，并将同一测试源注入旧基线验证；覆盖原计划/API、真实时钟采样、100/260 ms 边界、快速反转、受控投递原旧 Completed、最终事件、卡片并行和清理。预期来自固定常量与独立公式，不以新引擎自身作为 oracle。原有导航、卡片、焦点和动画回归继续保留。

同源检查固定为 32 项。首个旧版夹具在 9 项通过后，因混淆根门控下子页面的有效值与局部命中值失败；修正为同时核对两者，并保留首次 EXE、测试源和失败记录。修正后的同一测试在旧生产代码上 32/32 通过，生产代码未因该夹具问题改变。

独立冻结的纯 STA 探针直接调用旧、新生产原 VB `TransitionPlan`，比较计划/子类型、Duration/BeginTime 及局部值、克隆冻结、真实缓动函数返回值的原始 IEEE 位和异常字段。该探针不创建 Application/MainWindow/HWND，不写系统偏好；不以无渲染 tick 的时钟返回基值冒称插值覆盖。开发期的无 tick 自检失败及被弃用的私有时钟实验另存；最终探针不操作私有时钟，实时时钟插值由同源旧/新界面检查覆盖。

负对照仅在复制的 Presentation 库删除 Completed 的身份保护，保留时间回调保护；同一永久检查应准确拒绝旧完成回调破坏新切换。实际旁加载 DLL 的 Location、SHA 与唯一程序集身份必须核实，不能把嵌入库被意外使用后的结果当作变异验证。

最终 Release 单 EXE 的冷启动、UI、motion、搜索组、三依赖/七文档摘要，VS 2026 四项目 net48 Debug/Portable PDB，以及固定 PCL 样式和验证工具合约均绑定冻结源码与实际候选。具体结果、旧/新测试、数值探针、负对照、范围审查及最终 Commit 见本地 `artifacts/page-switch-csharp/validation.json`。各组有重叠，不合计为独立场景；受控旧回调不是自然桌面竞争复现，隐藏 WPF 不证明真人帧率。手工 F5、完整 Core 套件及真实平台完整下载安装不列为本轮执行。

本轮不写用户实例或外部配置，不打包、推送或发版。后续可独立评估标题栏或页面入退场的时钟所有权，继续保留原 VB 外观入口和同步回调顺序。
