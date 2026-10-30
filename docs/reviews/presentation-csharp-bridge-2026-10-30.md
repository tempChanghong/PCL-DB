# 第四轮首批：C# 界面辅助库与单 EXE 桥接

基线为 `8e8435a3878840138fd05b288a628e339752f336`。本批建立 `PclDownloadBridge.Presentation` net48 类库，迁入 Fluent、Back、PageY 三个纯计算函数；原 VB 标准模块保留名称、Double 签名和 Back 的默认参数，通过原入口转发。Core、VB 测试、页面、XAML、三个 WPF 动画类型、依赖属性 owner 和刚修复的 Loaded 策略保持。

## 源码与数值语义

实际读取固定 PCL2 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 ModAnimation.vb:539–544、612–621、674–684 与 MyPageRight.vb:410–415；C# 参考 PCL-CE `915ac29795efbdf6373417389e181e1504117048` 的 ModAnimation.cs:1220–1226、1313–1326、1396–1409 和 MyPageRight.cs:578–586。四份 Git 原字节和摘要在 `artifacts/presentation-csharp/source/manifest.json`。

页面入场仍从 -16 开始，叠加 5/250 ms Fluent 与 11/350 ms Back，曲线强度保持原值。上游 Fluent 的 Clamp(1-t) 与当前工具的 1-Clamp(t) 不借迁移互换；保留原运算顺序、NaN、Infinity、负零和越界值语义，不做曲线优化或时长调整。

新增库只使用 Framework 数学，不反向引用 App/Core，不依赖 WPF 或 JSON；类型保持 internal，仅向既有 App 程序集开放。App/Core 名称、资源 URI 和 VB WPF 类型的装配位置保持，未新增用户配置。

## 启动与工件兼容

Main 仍先注册 RuntimeBootstrap，再经 NoInlining 的 StartApplication 触及应用类型。Bootstrap 的字段和签名仍只使用 Framework；resolver 只增加 Presentation 的规范名称，沿原 Lazy 并发复用及版本、文化、公钥身份校验。

App 项目显式引用新库，普通 Debug 保留相邻 DLL 调试；单 EXE 将 Core、Presentation、Newtonsoft.Json 三项实际解析结果内嵌。解决方案保留 PCL-DB 显示名、原项目 GUID 和配置，新增类库在两个启动组合中均为 None。

当前候选与构建清单显式升级为 v4，精确要求三依赖；旧 v2/v3 归档仍精确要求 Core/JSON 两依赖，只能证明 ArchiveIntegrity，不能借旧回执通过当前候选。没有按数量猜格式，也没有改写历史 ZIP 或 dist。构建、嵌入、加载、冷启动、清单与归档校验同步处理，不止验证 IDE 可编译。

## 验证与提交门槛

独立同一 net48 探针分别执行旧 VB 基线与候选：1,801 项唯一数值/动画观测逐位比较，核对默认参数、三个动画类型、Clone/Freeze、实际时钟、DP owner/default、App manifest 名称及 g.resources 的键、类型与内容摘要。只允许新增 Presentation 内嵌资源。探针与比较器冻结，八个负控制拒绝数值、DP、BAML、资源清单与探针身份破坏；最终 EXE 必须重新跑，初版结果不冒充最终结果。

新增 14 项永久界面桥接检查，注册到 all/navigation/motion/control-motion；原检查保留。standalone 从 25 增至 30，增加第三依赖的字节加载、并发身份复用及三种不兼容身份拒绝；原未知/卫星请求检查同时包含新库。当前策略 `.23` 为 standalone 30、all 7214、motion 6059、navigation 4653、control-motion 725、搜索 3444，其余数量、超时不变。各组有重叠，不合计为独立场景。

最终门槛为冻结源码上的 Release、VS2026 Debug 四项目 net48/未优化/Portable PDB，同一冷目录单 EXE 的 standalone/all/motion/搜索、三依赖与七文档实际摘要；再用该最终 EXE 重验独立差分。工具合约须在 PS7、Windows PS5.1 各 126 项通过，并以实际 Beta 8 v3 ZIP 只读验证历史兼容。固定 PCL 资源检查继续保留。

实际结果、被测输入/输出摘要、源码清单和提交树绑定见 `artifacts/presentation-csharp/validation.json` 及本批 cold 下 `result.json`。Core 与原 VB 测试源码未改，本批不重跑无关 Core 全量；既有 Core 8323/旧宿主7305是之前被测 DLL 的结果，不冒称本批新执行。验证使用受控 WPF、临时目录与本地 HTTP，不等于真人帧率、真实平台完整下载安装或手工 F5 验收。未打包、推送、发版。

## 后续

首批交付是 C# 界面层的装载与身份兼容基础，不代表整个界面迁移完成。下一批可在当前基线稳定后逐个迁移无页面依赖的时钟/策略，继续保护 Loaded、卸载、快速切换、返回与取消；保留原 VB 入口及 WPF 资源身份，不与页面编排或下载算法一起改。
