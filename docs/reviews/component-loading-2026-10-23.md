# 组件列表加载、折叠和旧操作的归属检查

日期：2026-10-23。基线：`57153ce`。本轮只修改组件选择页的展示与操作边界，验证后本地 Commit，不推送、打包或发版。

## 固定源码依据

重新读取未限制 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `Pages/PageDownload/PageDownloadInstall.xaml.vb` 和 `Controls/MyCard.vb`，规范化副本和 SHA-256 存在 `artifacts/component-loading/source/`，收尾重新对照 Git 对象。

PageDownloadInstall 的 ReloadSelected 根据各组件 GetLoaderError 隐藏展开箭头，并在列表不可用时设为 IsSwapped；七个组件的 PreviewSwap 会再次阻止无有效列表的展开。加载中状态来自各自的 LoadOptiFine / LoadForge / LoadFabric 等加载器。MyCard 的折叠继续保留退出中的 SwapControl，再在动画结束后收起。它们不是原版清单的整页 LoaderWait，不应再套一层 400 ms 等待。

## 修复前证据

先加入离线回归夹具与调用入口，保持 `MainWindow.Components.vb` 与 `57153ce` 的产品代码字节一致，构建后运行。`before-fix-source.json`、`before-fix/checks.json` 和 `before-fix/component-loading-probes.json` 记录该范围；不将这个新增夹具后的程序称作原始发行二进制。

- pendingFolded=false：待获取的组件仍可展开，刷新展开列表时立即替换为一行“正在获取版本列表”，没有沿用退出中的旧列表。
- staleRowRejected=false：移除后的旧版本行重新触发 Click，会用旧对象替换当前选择。
- staleRetryRejected=false：移除后的旧错误重试控件再次触发，会重新请求当前组件。

后两项是通过已有版本行 Activate / 键盘事件定向重放生产事件处理器的边界检查，不声称已复现真人鼠标在正常卸载后的再次点击。现有控件已清理卸载时的按下状态，本轮补上应用层归属校验。

## 修改与关系

- pending 或来源已失效的组件保持折叠并隐藏展开箭头；各组件仍独立并行加载，没有增加网络延时或整页等待。
- 开始刷新时先折叠，再交由已有 PclCard.RefreshBody 保留退出中的旧列表并禁用其输入。收到结果时只替换待展开的数据工厂；折叠完成后移除旧行及其动画，不自动展开新列表。
- 数据工厂在创建时捕获 Minecraft 对象与组件代次，懒行稍后创建时继续使用这个归属，不从当时的新页面借用上下文。
- 行与重试操作检查当前 Minecraft、代次、卡片、内容对象、待获取/来源状态、页面、忙碌和关闭状态；版本行还核对当前接受的条目对象和 Minecraft ID。旧内容不能因相同版本字符串或相同组件种类重新获得操作权。
- 清除按钮核对当前 Minecraft 和卡片对象，防止重新进入同一 Minecraft 对象后旧卡片清除新选择。它不受 pending 折叠限制，用户仍可在加载期间取消已选组件。
- 保留已选版本、手工名称、显式取消的 Fabric API、失败/缺失版本的安装阻止，以及单组件重试不会取消其他组件请求的约定。取消后迟到的数据进入原有失败/重试流程，不恢复用户已清除的选择。
- PCL 的失败状态也会阻止展开；本工具保留既有错误卡片内的明确重试入口，失败后默认折叠，用户可展开重试。这一处是功能适配，没有宣称失败展示与上游完全相同。

组件目录查询、网络超时、下载调度、安装取消源、隔离、共享 Mods、已有实例配置和原子发布均未修改。没有更改通用卡片或动画时长。

## 验证

新增 23 项界面断言，覆盖刷新折叠与退出内容、旧行/旧重试、并行独立请求、选项/名称保留、禁止旧源和旧 Minecraft 操作、忙碌与关闭、同 Minecraft 新卡片、深色状态、取消后重试及加载期间清除选择。原有折叠中接受列表、反向展开、懒加载长列表、键盘清除和依赖检查继续执行。

策略 `PCL-DB-checks/2026-10-23.16`：Core 6007 / UI 850 / motion 637 / pack-search 320，共 7814 项断言；验证契约 97 项、进程工具 19 项另计。执行 `./build.ps1`，不带 Package。最终源码清单、程序摘要和验证结果以 `artifacts/component-loading/validation.json`、`committed-validation.json` 为准。

明暗离屏图为 `component-loading-light.png` 和 `component-loading-dark.png`，检查了折叠状态、刷新说明、清除按钮和安装门控。使用隐藏呈现源、离线元数据及临时安装夹具；不访问用户真实游戏目录，不作为真人桌面、DPI、帧率、联网目录或真实游戏安装验收。

下一轮适合核对组件失败提示是否能清楚区分超时、源返回空列表与版本不兼容；在保持安装计划和现有重试入口的基础上，再按实际缺陷决定范围。
