# 第四轮第二批：单属性动画时钟的 C# 实现

基线 `b2477a553fbf1b38f731ddc392f83b9f54d1dc02`。仅将 `PclMotionSlot` 的取样、替换、完成与清除实现迁入 Presentation 内部 `MotionSlot`。原 VB 类型、构造/方法签名、可写 Clock 元数据及 private setter 转发保留，时钟只有一个 C# 状态；不保留会分叉的 VB shadow clock。

## 源码依据与保持项

实际读取固定 PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 ModAnimation.vb:753–784、876–888，以及 C# 参考 PCL-CE `915ac29795efbdf6373417389e181e1504117048` 的 ModAnimation.cs:1497–1526、130–149；页面退役依据 MyPageRight.vb:236–248 与 MyPageRight.cs:287–309。同名执行先停止再登记、完成后退役为行为依据；本地 WPF slot 是自身实现，不照搬上游全局动画组或 CE 并发容器。

构造仅保存输入，不增加校验。Animate 保持 GetValue → SetValue(origin) → CreateClock(true)/显式 cast → 可选 Pause → 发布 Clock → 注册自身 Completed → Apply。SetValue 保持先清 Clock，再 detach、移除旧 controller，最后写本地基值。旧 Completed 同步核对引用身份，不能覆盖重入后产生的新时钟；不改为延迟 Dispatcher 清理。异常仍沿原发生阶段和 DP 状态传播，不用提前检查或 try/finally 改写优先级。

Presentation 开始承担 WPF 时钟，仅增加 WindowsBase/PresentationCore 框架引用；没有 PresentationFramework、UseWPF、XAML 或反向 App/Core/JSON 依赖。原架构断言相应改为这个精确的框架引用边界。原曲线、三个 WPF 动画类型/依赖属性 owner、Loaded 策略（含首次延迟创建修复）、页面和 XAML 保持。新库仍是原第三依赖，v4 工件模型和旧归档模型不变。

新实现对象分配、闭包声明类型和私有调用栈属于结构差异，不承诺任意 OOM 或 private stack 字符串完全相同。

## 验证

新增 24 项永久 WPF 行为检查：UIElement.Opacity 与 Animatable.TranslateTransform.Y 各 12 项。覆盖中间帧与基值、外部修改基值后的显示值取样、替换、清除/重复、真实 Completed 重入的归属保护、自然完成和与 timeline.To 不同的显式 finalValue。相同回归源注入旧基线后须 24/24，候选也须 24/24，不把语言迁移当作行为修复。

独立固定 net48 SlotProbe 对照旧/新 88 项观测，包含 SolidColorBrush、外部 Remove、DP/完成回调重入、绑定清除及无效/只读/frozen/错误动画与终态等异常路径；比较阶段状态和相对身份，不比较绝对时刻、地址或私有堆栈。另复用冻结曲线探针比较 1801 项数值/动画观测、三个原 WPF 类型、DP owner、32 个 g.resources 项（其中 12 个 BAML），本轮两侧均已有 Presentation，manifest 名称不应增减。

最终探针在仅含同一 EXE 的隔离目录运行，避免相邻 DLL 绕过内嵌解析；初版 bin 旁运行的凭据另存，不冒充最终冷加载。比较器的输入破坏负控制和移除身份 guard 的受控实现检查另存 ignored artifacts，均不修改生产源码。

受控删除 C# 引擎的身份 guard 后，同一 24 项回归为 16 通过/8 失败，两种目标的真实 Completed 重入检查均失败，其余失败含同一缺失的连锁状态变化，不当作八个独立 Bug。单独实例化 VB facade 并读取其实现程序集 Location/摘要，确认 mutant host 实际使用受控 DLL，而非回退原内嵌库；正常候选和旧 VB 的正向结果保留。

源码审查固定 VB 非 private 入口/private Clock setter 形状、C# 完整保序模型、原 policy 后缀、启动与 Bootstrap、曲线、XAML、Core/旧测试及 friendship。八个源码负控制应拒绝语句次序、取样、cast、身份或清理方式破坏。源码比较按 Git LF；涉及保留后缀的 raw byte 口径由审查凭据明确，不混为同一证明。

策略 `.24` 只因新增 24 项调整 all 7238、motion 6083、navigation 4677、control-motion 749；standalone 30、搜索 3444 与其他组/超时保持。最终冻结后运行 Release、VS2026 Debug 四项目 net48/未优化/Portable PDB，同一冷 EXE 的完整 standalone/UI/动画/搜索、三依赖/七文档摘要；最终 EXE 新跑同一两个差分探针，再校验实际 v4 候选/清单绑定。工具合约须在 PS7/Windows PS5.1 各 126 项通过。不同组有重叠，不相加作为独立场景。

实际完成状态、结果/输入/源码摘要及提交树绑定见 `artifacts/motion-slot-csharp/validation.json`。Core 与原 VB 测试源码未改，不重跑无关 Core 全量，不将历史 8323/7305 当作本批新执行。受控 WPF/临时文件/本地 HTTP 不代替真人帧率、手工 F5 或真实平台完整下载安装。未写用户实例/配置、未打包、推送或发版。

## 后续

下一批可独立评估控件动画策略的迁移，重点保护 Loaded 前后构造、系统偏好订阅去重、卸载/重挂、输入门控及同步回调重入；继续保留 VB 入口和页面边界，不与页面编排一起改。
