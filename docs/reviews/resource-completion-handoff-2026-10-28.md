# 资源安装完成与列表通知交接

基线：`e28798611319d08cbff6f9c3c7ba81b2665d2baf`。

## PCL2 依据

重新读取 PCL 固定提交 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的三个 Git 原始文件：

- `PageDownloadCompDetail.Save_Click` 构造资源下载加载器，终态提示与打开结果位置在状态回调中处理。
- `PageInstanceMod` 按更新加载器的成功／失败／取消终态显示结果，缓存清理另行处理并记录错误；本地 Mod 列表按当前实例刷新。
- `ModLoader.State` 先更新状态，再发送取消和状态通知。

文件与摘要见 `artifacts/resource-completion-handoff/source/manifest.json`。指定实例资源安装和异步请求代次是桥接实现，本轮借鉴上述状态与后续通知的分工，保留 PCL 风格页面、按钮和原动画时序。

## 问题与修复

指定实例资源安装成功后，原代码先使本地列表失效，再标记任务成功；`Finally` 中又失效一次。旧本地读取的取消回调若抛错，已写入的文件会被显示为安装失败。成功、失败、取消的结束通知一旦再次抛错，还会直接跳过 `_busy`、取消请求和控件状态清理，阻止后续任务。回调中新发起的读取也可能被重复失效误伤。

现在先记录写入任务自身的结果，再在结束阶段仅通知一次。通知异常单独记录，内层 `Finally` 保证任务状态释放，随后沿用既有管理刷新流程。`RetireLocalModRequest` 已在通知前使旧读取失效，因此删除通知后的重复赋值，避免再覆盖回调内的新状态。文件校验、原子发布、目标绑定及原生隔离策略均沿用原机制。

## 验证

相同夹具包含三种启动器 × 成功／失败／取消 × 四种取消回调行为，每例检查终态与任务释放、旧读取结束／导航／文件保护、显式重试与本地管理恢复，共 108 项。基线 36 项失败、72 项通过，修复后全部通过。回调覆盖报错、替换读取及第二个取消观察者报错、转去设置页、正常通知；交替使用共享／隔离目录、明暗外观和动画启停。

最终同一强制重建 EXE 的新增专项、相邻任务结果／入口／管理刷新、资源安装目标／搜索返回／输入重入以及任务页／安装动作／返回动画专项通过。精确执行数保存在 `artifacts/resource-completion-handoff/validation.json`，组间有重叠，不相加作为独立覆盖总数。构建零警告、零错误；验证契约、限时工具、验证进程及固定 PCL 来源检查通过。

- [基线：文件已写入却显示安装失败](../../artifacts/resource-completion-handoff/baseline-complete-Completion/completion-result.png)
- [修复：保留成功结果与可用结果动作](../../artifacts/resource-completion-handoff/final-Completion/completion-result.png)

使用临时原生配置、真实格式 JAR、实际安装目标计划、受控文件写入和可控取消回调，不执行真实平台下载，不写用户实例或外部启动器配置；不能替代实机完整安装和帧率验收。未重跑完整 Core、all、motion、navigation、pack-search 或单文件分发验证。

新增专项已注册到 navigation、motion 和 pack-search，all 经 navigation 包含该组。策略 `PCL-DB-checks/2026-10-28.28` 注册 all 6511、motion 5896、navigation 4300、pack-search 3240；注册数不是本轮执行数。源码、EXE、内嵌文档、截图、回执与提交的绑定见 `validation.json` 和 `committed-validation.json`。未推送、打包或发版。
