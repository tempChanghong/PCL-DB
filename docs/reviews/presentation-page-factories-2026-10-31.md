# 第四轮第六批：页面入退场时间线工厂

基线 `1d5f45ff3cd21937285246d1a61ae0c1358d18dd`。本批把卡片透明度、退场 Y、页面揭示与淡入淡出、滚动条入退场这七个时间线工厂，以及卡片反转位移的纯计算移至内部 C# `PageMotion`。原 VB 控件类型、入口和自定义 WPF 动画类型保留，.NET Framework 4.8、程序集与资源名称不变。

## 固定源码依据

实际读取 PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Controls/MyPageRight.vb:392–451`、`FormMain.xaml.vb:1503–1554`，以及 PCL-CE `915ac29795efbdf6373417389e181e1504117048` 的 `Controls/MyPageRight.cs:554–632`、`FormMain.xaml.cs:1960–2018`。入场采用 100 ms alpha、并行 5 DIP/250 ms fluent 与 11 DIP/350 ms back、25 ms 错峰；退场采用 70 ms alpha/向上 6 DIP、15 ms 错峰；滚动条为 350/90 ms。具体兼容基线仍是当前 PCL-DB，不把公开上游源码当作全部发行版行为的证明。

## 保留的逻辑与边界

`PclPageCardMotion` 和 `PclSurfaceMotion` 仅将内联动画创建交给 C#，可见区筛选、快照、错峰索引、时长汇总、取样、时钟所有权、输入门控、重新 prime、BindingBase 恢复、变换恢复、事件及 generation 重入保护仍按原顺序执行。没有合并两个状态机或修改布局、页面导航、下载和管理机制。

CardOpacity 保持显式 Duration，只在 delay > 0 时加入第二个 hold 帧；SurfaceReveal/SurfaceOpacity 保持 Automatic Duration，即使 delay = 0 也保留同时间的两个离散帧。滚动条退出的 BeginTime 仍为默认值，进入仍显式赋值。保留默认 FillBehavior、RepeatBehavior、SpeedRatio 等 WPF 元数据，不增加参数裁剪或容错。整数 delay + 100/70 保持 checked。

`PclPageCardYAnimation` 继续由原 App 中的 VB 类型实现；Origin DP 的 owner、名称、类型、默认值 -16、构造和 Clone 身份保持。Sample 仍先读取原 DP，再以原顺序计算 `-origin / 16 * PageY(milliseconds)`，保留符号零、非有限值及运算顺序。

## 验证

新增同源永久检查通过原生产类的 Change 和真实 WPF 时钟观察时间线及状态；同一个测试文件分别注入旧基线与新候选执行。检查零延迟关键帧、Duration、错峰、缓动、滚动条、原类型/DP/克隆、快速反转、过期回调、门控与绑定恢复。预期来自固定常量及独立公式，不以新工厂自身作为 oracle。

另用冻结的纯 STA 探针直接比较旧、新生产 `PclPageCardYAnimation.Sample`、克隆及 DP/时间属性；保留原始 IEEE 位和异常字段。该数值探针不创建 MainWindow/HWND，不冒称直接调用旧版不存在的 C# 工厂。工厂结构由源码审查和真实旧/新时间线检查互补验证。

负对照仅在复制的 Presentation 库将 CardOpacity 入场 Power 2 改为 3；核实实际加载 DLL 的 Location 和 SHA 后，要求同一永久测试准确拒绝。变异库旁加载用于验证断言灵敏度，与正常候选的单 EXE 冷启动分开记录。

最终 Release 单 EXE 的 standalone、UI、motion、搜索组，嵌入三依赖/七文档摘要，以及 VS 2026 四项目 net48 Debug/Portable PDB 均须绑定冻结源码和实际 EXE。结果、旧/新检查、探针、负对照与最终 Commit 见本地 `artifacts/page-motion-csharp/validation.json`；各检查组重叠，不相加为独立场景。隐藏 WPF、受控回调和模拟网络不证明真人帧率或真实平台完整下载安装；手工 F5 与完整 Core 套件不列为本轮执行。

本轮不修改用户实例或外部配置，不打包、推送或发版。后续再独立评估页面动画时钟所有权迁移，继续保留同步属性回调、绑定恢复和返回时序，避免一次迁移整个导航状态机。
