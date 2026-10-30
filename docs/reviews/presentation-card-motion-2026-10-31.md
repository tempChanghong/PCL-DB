# 第四轮第五批：卡片动画工厂、曲线与高度时钟

基线 `c12d0f18040caecdc9d01f8bccb25b52d1d4c920`。本批迁移卡片高度/箭头动画工厂、初速度曲线纯计算及高度时钟所有权；布局、测量、折叠事件、正文保留/回收和资源选择继续在原 VB `PclCard` 中处理。没有迁移页面切换编排或修改外观。

## 源码与兼容边界

实际读取固定 PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Controls/MyCard.vb:179–212` 与 `Modules/Base/ModAnimation.vb:638–652`；C# 参考固定 PCL-CE `915ac29795efbdf6373417389e181e1504117048` 的 `Controls/MyCard.cs:255–301` 与 `Modules/Base/ModAnimation.cs:1349–1368`。它们提供 800 DIP 分段、长距离匀速/缓动衔接、整数时间与初速度比例曲线依据；本批以当前 PCL-DB 的既有数值和 WPF 行为为精确基线，不混称原上游全部边界均已等价。

原 VB `PclCardMotion` 标准模块的两个 Friend 工厂签名不变。`PclInitialSpeedEase` 仍是原 App 中的 sealed WPF 类型，Alpha DP 的 owner、类型、默认值、构造、EasingMode 和 Clone 身份保留。VB 工厂通过固定委托创建原 easing；EaseInCore 仍先限幅、再读取 Alpha DP、最后调用 C# 公式。Presentation 不反向引用 App，不注册替代依赖属性。

三处 typed CInt 使用原位置的 `checked((int)Math.Round(...))`，整数时长相加也保留 checked。初次严格差分发现 Convert.ToInt32 在 32 个溢出样本中的异常文字不同；修正后不修改冻结探针或放宽比较，IEEE 位、方案、克隆、插值和异常字段均须一致。首次差异记录和后续通过记录分别保存。

高度状态只在内部 C# `CardHeightMotion` 持有。构造只保存输入，不提前回调或写 WPF 属性。Start 保持 Stop → 首次/不足 1 DIP 直接 settle → Clip → 原工厂/CreateClock → target/clock 发布 → Completed → Height 基值 → Apply。Stop 保持先清所有权和 target，再判断旧时钟；有旧时钟时先 Remove 再 detach，无所属时钟时不清理外部动画。完成回调仍先核对身份，再 Stop 并同步调用原 VB SettleHeight。

PclCard 的公开/Friend 接口保持；宽度变化仍按原顺序重新测量、对比当前 target 后重启。正文 Builder、延迟回收、输入门控及祖先 coercion 继续留在 VB。没有添加参数校验、catch、finally、Dispose 或新取消规则。对象分配、闭包类型、私有字段及私有堆栈变化不作为兼容性承诺。

## 验证设计与记录

新增 39 项永久检查：17 项接口、DP、克隆、边界和舍入，22 项真实卡片时钟与生命周期。固定旧生产代码只注入同一测试，迁移候选也使用该源；两侧均须 39/39。覆盖快速反转并投递旧 Completed、FoldChanged 重入、已接受动画的输入门控、延迟正文刷新、宽度重新排版、卸载和不足 1 DIP 的直接终态。

新增 wrapping 夹具的首次旧基线运行在 33 条后因时钟获取时序失败；修正夹具的滚动条稳定性与同步取样/暂停，保留原断言，另存失败与 v2 结果。旧完成回调测试捕获原时钟保存的真实委托，再受控投递，不能当作真实桌面竞争或依靠 Remove 后 SkipToFill 无回调来证明保护。

独立 net48 冻结探针共 173 条观测：125 个高度方案、15 个箭头、32 个 easing 样本和 1 个合约；包含分段相邻 double、整数中点、正负零、NaN/Infinity、溢出、WPF 克隆/冻结与插值。冷目录仅含 EXE，宿主初始化原 Bootstrap；探针不创建 MainWindow/HWND、不修改系统偏好。最终 EXE 须重新运行同一探针，初候选结果不冒称最终结果。

负对照仅在复制库删除 CardHeightMotion 的旧 Completed 身份保护；核实实际相邻变异 DLL 的 Location/SHA 后，第 22 条旧回调所有权断言准确失败，已通过 21 条，余下未执行。该证据与正常候选的仅 EXE 冷启动分别记录，生产与永久测试不变。

最终冻结门槛为 Release、VS 2026 Debug 四项目 net48/未优化/Portable PDB，以及同一单 EXE 的 standalone 30、all 7367、motion 6212、搜索 3444、三依赖/七文档实际摘要和源码绑定。策略 `.28` 仅因新增 39 项调整 all/motion/navigation/cards（后两者为 4806/173），control-motion 831 与其他组/时限保持。组间有重叠，不相加为独立场景。PS7/Windows PS5.1 验证工具合约各需 126 项通过；固定 PCL 资源检查继续保留。

实际结果、首次差异/夹具失败、旧/新测试、负对照、最终数值差分、源码清单与提交绑定见本地 `artifacts/card-motion-csharp/validation.json` 与 cold 下的 `result.json`。Core、原 VB 测试、12 份 XAML、图片/字体、前批反馈/策略/时钟、配置与程序集/资源名称保持；不把历史 Core 检查当作本轮新执行。隐藏 WPF 与受控 HTTP 不证明真人帧率、手工 F5 或真实平台完整下载安装。未写用户实例或外部配置，未打包、推送、发版。

## 后续

下一批再独立评估页面/卡片的入退场编排，继续保留原入口、内容绑定、输入门控和同步回调顺序，避免与本批工厂及单卡片高度时钟混改。
