# 多窗口安装目标的成组保存

基线 a7723df1cff04e371dbfc0aadeaba2bcace2b1c2。本轮修复 VB 界面的配置提交，不改 C# Core、公开接口、资源、布局或动画参数，保留 .NET Framework 4.8。

## 复现

两个窗口从同一配置载入目录一／PCL。窗口 A 改为目录二／PCL 并保存；旧窗口 B 选择目录一／HMCL。原来的递归差量只产生 targetBindingId，因为 B 眼中的目录没有变化。与最新磁盘配置合并后成为目录二／HMCL，构造出了 B 从未选择的目标。

新增回归先在原实现的真实 SavePreferenceValues 路径失败，标签为 editing either install target half publishes the whole current target: 0，见 baseline-compatibility.json。独立只读审查另用当前 App 的 ChangedValues／MergeValues 纯内存反射调用复现同一结果。未将其归因于 C# 迁移。

## 修复

SavePreferenceValues 计算差量后，若本窗口使用已知绑定格式且 gameRoot、targetRoot、targetBindingId 任一有变化，将当前三项一并加入差量。整个保存仍经过现有每路径跨进程锁与临时文件原子发布。

没有目标编辑时不加入这组字段，因此关闭旧窗口或仅修改速度不会回滚新目标。其余字段和绑定仍按原逻辑合并，保留别的窗口新增的绑定、选择、未知字段与独立偏好。保存失败仍不会推进偏好基线，可在锁释放后重试。

本窗口读入的未知绑定格式保持原有只读门控，修改手动 gameRoot 不向未知对象注入目标字段。这不是全面跨版本并发保护：窗口已读为格式 1 后，其他未来版本再升级磁盘格式的竞争，本轮未解决，列为后续审查项。

## 源码依据

以 PCL2 5ef81e490ae46b1100d61e33d0183094c7a80ca8 的 ModMinecraft.vb:17–21／Settings.vb:248–272 为主：目录选择是一个逻辑设置，保存取当前值。参考 CE 915ac29795efbdf6373417389e181e1504117048 的 JsonFileProvider.Set:85–88 和 FileConfigStorage:142–145 的逻辑键序列化及待写值捕获。

PCL-DB 的三元组与跨进程差量合并为本工具自己的协议，不声称上游提供同名事务。固定源码和当前基线的保存链路原字节见 artifacts/target-save-conflicts/source/manifest.json。

## 验证

新增 28 项界面内受控检查：三种启动器各自的换身份／换根交错、原生与手动目录互换、未编辑旧窗口关闭、往返恢复、其他窗口字段和绑定保留、锁文件失败不推进基线、释放后重试，以及本窗口已读为未知格式的显式／自动保存。

原 49 项兼容性检查保留，专项共 77 项；完整 UI 由 6666 增为 6694。验证策略升至 PCL-DB-checks/2026-10-30.14，仅更新这两个计数，不减断言或放宽超时。

提交要求：最终 Release／VS2026 Debug、VB 全量 7984、原编译 VB 宿主 7305、Core 原始／规范接口零差异（979／1245，资源 2／JSON 15），同一冷目录单 EXE 的启动 25／兼容性 77／完整 UI 6694，以及依赖／文档摘要与验证契约。实际结果、源码清单与提交树见 artifacts/target-save-conflicts/validation.json。

配置均位于临时目录，无真实外部启动器配置修改或实际游戏验收。完整 motion 和搜索专项本轮不另跑，不计入新结果。未打包、推送或发版。独立代理进行了缺陷复现和补丁只读复审，最终源码与提交由主代理核对。

后续优先审查跨版本配置升级时的写入归属和用户提示，再考虑继续提炼保存职责；本批不混入该协议扩展。
