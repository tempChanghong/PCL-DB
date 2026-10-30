# 第四轮第四批：控件按压、悬停与选中反馈引擎

基线 `f52bbcfbb29662b3432e1245ef43418b50ce95b9`。本批将 Press、ListHover、ResourceHover、ListSelection、LocalModSelection 五个反馈引擎迁入 C# Presentation；原五个 VB 类型、Friend 构造/方法/Transition 属性、控件事件入口、XAML 与资源身份保持。

## 源码依据

实际读取固定 PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`：`Controls/MyButton.xaml.vb:154–175`、`Controls/MyIconButton.xaml.vb:70–87`、`Controls/MyListItem.xaml.vb:545–606`、`Pages/PageDownload/Resource/MyResourceItem.xaml.vb:166–203` 及 `Pages/PageInstance/MyLocalModItem.xaml.vb` 的 Checked 动画。它们提供按压双增量、状态去重、180 ms 淡出后复位、选中高度 40%/60% 并行增量与延后离场透明度的依据。

C# 参考固定 PCL-CE `915ac29795efbdf6373417389e181e1504117048` 的 `Controls/MyButton.xaml.cs:170–206`、`Controls/MyIconButton.xaml.cs:375–405`、`Controls/MyListItem.xaml.cs:32–129`、`Pages/PageDownload/Comp/MyCompItem.xaml.cs:17–83` 与 `Pages/PageInstance/MyLocalModItem.xaml.cs`。本批保留 PCL-DB 基线的已验证时长与行为，不用 CE 不同的默认参数覆盖原版 PCL 风格。

## 跨语言边界与保持项

原 `PclPressKind`、`PclPressAnimation`、`PclSelectedHeightAnimation`、`PclRadioColorAnimation` 均留在 VB App 中；动画工厂由 VB 固定委托传入。Press 使用泛型保留原枚举类型；选中引擎使用原 `PclListItem.ForegroundProperty` 和原资源画刷解析器。Presentation 不引用 App，也不注册替代依赖属性或移动 WPF 类型。

状态只在 C# 引擎中持有。保留读取显示值再脱离旧时钟、写入基值后建立新时钟、完成时核对身份、输入门控与退载取消。Press 的 Cancel 仍先通知手势回调再清理，不借迁移增加 finally、异常吞并或重入规则。ListHover 保留独立整行/背景缩放与 541 ms 复位；ResourceHover 保留同一画刷、181 ms 背景复位及原 VB 颜色动画。

调色不重启几何动画；悬停完成读取最新颜色。ListSelection 完成捕获本次 selected，LocalModSelection 完成读取当前 selected/key；前者保留 ink 引用，后者在移除旧控制器前清空 ink，均按原顺序。稳定布局继续使用 Auto/Stretch 标记。CInt 使用银行家舍入；LocalMod key 比较保留 VB 的 Nothing 与空字符串相等语义。工厂与桥接带来的分配、闭包类型和私有堆栈变化不作为兼容性承诺。

Core、原 VB 控制台测试、12 份 XAML、20 份图片/字体、已有曲线/时钟/动画策略与 Bootstrap 保持。net48、程序集名称、三个内嵌依赖、七份根文档和 v4 候选模型不变；没有配置格式或资源名称变更。

## 验证与证据

新增 42 项永久检查：19 项 VB facade、枚举、动画和依赖属性身份/签名，23 项真实 WPF 时钟、快速反转、相同状态去重、调色、输入门控和清理。相同测试源注入固定旧 VB 生产代码后应 42/42，迁移候选也须 42/42；不借语言迁移修复行为。

旧完成回调检查先捕获真实 WPF 时钟保存的 Completed 委托，替换时钟后有意投递原处理器，以验证身份保护；不把 Remove 后 SkipToFill 不再回调当作证据。这是受控迟到通知，不等于复现了真实桌面事件竞争。宿主先初始化原 Bootstrap 和 App 资源，测试不改系统偏好或用户配置。

负对照仅在副本中删除 ResourceHoverMotion 的旧完成回调身份保护；核对实际加载的相邻变异 DLL 的 Location 与 SHA-256 后，同一检查准确在第 29 条资源悬停断言失败，已通过 28 条，余下未执行。生产源码与永久测试不变；负对照的相邻 DLL 验证与正常候选的仅 EXE 冷加载分别记录。

最终冻结门槛为 Release、VS 2026 Debug 四项目 net48/未优化/Portable PDB、同一单 EXE 的 standalone 30、all 7328、motion 6173、搜索 3444、三依赖/七文档摘要及源码绑定。策略 `.27` 只因新增 42 项调整 all/motion/navigation/control-motion（后两者为 4767/831），其他组与时限不变；组间有重叠，不能相加为独立场景。工具合约须在 PS7 与 Windows PS5.1 各 126 项通过，固定 PCL 资源验证继续保留。

实际完成状态、旧/新测试、独立审查、程序集/源码摘要和提交树绑定见本地 `artifacts/control-feedback-csharp/validation.json` 及 cold 下的 `result.json`。Core 未改，不把历史 Core 验证当作本轮新执行。隐藏 WPF、受控时钟与本地 HTTP 不证明真人帧率、手工 F5 或真实平台完整下载安装。未写外部配置或原实例，未打包、推送、发版。

## 后续

下一批可单独评估卡片高度/箭头辅助与页面/卡片切换编排；原 WPF 类型和依赖属性 owner 继续保留，页面编排不与本批五个反馈引擎混改。
