# 管理读取入口的取消异常与重试

基线：`8b49957201520fc9428b49f92955aaf9e44a3d78`。

## PCL2 依据

重新读取固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的四份 Git 原始文件，摘要见 `artifacts/management-entry-errors/source/manifest.json`。

- `PageSelectRight` 为设置入口绑定当前实例；`PageInstanceMod.LoaderInit/LoaderRun/Load_Click` 从当前实例 `PathIndie` 读取 Mods，失败保留原加载器重试入口。
- `ModLoader.LoaderTask.Start/Failed/Cancel/IsCanceledWithThread` 以执行身份约束输出、失败与取消；旧执行不能修改替代操作的状态。
- `MyPageRight.PageOnForceExit` 清理旧内容与冲突动画，沿原有页面加载、入场与重试机制恢复。

PCL-DB 本轮沿用既有卡片、状态提示、焦点和动画时序，修正自身 CancellationToken 通知与入口错误边界。

## 复现与修复

原管理列表刷新、打开目录、原版补全准备，以及本地 Mod 原生配置重读，在分配新请求或进入 Try 之前取消部分旧请求。取消通知若抛错，可能绕过已有错误反馈，令 Async Sub 的异常进入 Dispatcher；目录旧源已释放时也会误报。通知若已接受新页面或目录读取，旧入口不能再显示错误或清理新请求。

- 管理扫描、目录和补全入口把取消、归属检查、请求创建及状态绑定放进原有 Try。未创建请求时使用空引用，异常过滤和 Finally 仅访问已分配的取消源，并按代次与请求身份清理。
- 扫描和补全错误增加目录代次检查，保留取消通知接受的新目录操作。目录打开还在取消前记录实例与管理代次，之后确认同一页面、实例和目录动作仍有效。
- 捕获的已释放目录源复用既有取消辅助方法处理。Mod 原生配置重读在父管理取消失败时保留原列表，只向仍有效的 Mod 页显示重试反馈，不启动旧读取。
- 扫描入口在请求尚未创建时失败，若当前显示实例详情或闲置 Mod 页，也显示对应页面的错误；不会只更新隐藏的根列表提示，更不会覆盖正在读取的新 Mod 列表。

## 验证

相同最终夹具包含 48 个场景、144 项断言。基线构建 **51 项失败、93 项通过**，最终修复后全部通过，见 `artifacts/management-entry-errors/baseline-complete-Entry/`。

覆盖 PCL、HMCL、PCL-CE，四种入口，交错明暗及开关动画，以及取消通知报错、先切页后报错、先接受新目录／Mod 读取后报错、旧源提前释放。直接收集 Dispatcher 异常；检查真实可见反馈、新请求／时钟保持、旧读取不执行、捕获源清理、目录只打开一次和任务代次保持。独立重试重新读取原生实例并验证可用状态、配置及已有游戏文件字节保持。早期夹具编译与中间构建不计入最终验收。

最终同一 EXE 的专项 **144 项**、完整导航 **3496 项**、完整搜索 **2616 项**、下载分类／任务返回动画 **53 项**通过；专项已包含在导航，组间有重叠。Release 重建零警告、零错误。验证契约 110 项、限时工具 44 项、验证进程 19 项及固定 PCL 来源／资源 21 项检查通过。

策略 `PCL-DB-checks/2026-10-28.21` 注册 all 5707、motion 5092、navigation 3496、pack-search 2616；注册数不作为本轮执行数。未重跑完整 Core、all、motion 或单文件分发验证。使用临时原生配置、真实临时 Mod 文件、受控服务和隐藏 WPF，不写用户实例或外部配置，不作为真实下载安装或桌面帧率验收。最终源码、EXE、内嵌文档、回执与提交绑定见 `artifacts/management-entry-errors/validation.json` 和 `committed-validation.json`。未推送、打包或发版。
