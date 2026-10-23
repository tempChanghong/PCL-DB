# 原版安装页操作按钮动画

日期：2026-10-23。基线 `12741bf`。本轮依据用户两段录像，对原版具体版本点击后的动效做局部修正；验证后本地 Commit，暂不打包、推送或发版。

## 录像与固定源码

第一段为 Beta 2 的原版选择与返回；第二段为 PCL 的资源详情页面，呈现标题返回、详情卡和加载内容的分层切换。录像原始路径、SHA-256、元信息及抽帧位于 `artifacts/version-entry-motion/`，不将录屏采样或离屏帧视为桌面帧率测试。

重新读取 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 PageDownloadInstall.xaml/.vb、MyExtraTextButton.xaml/.vb、ModAnimation.vb、FormMain.xaml.vb、MyPageRight.vb；七份规范化源码及摘要见本轮 `source.json`。

PCL 的资源详情走通用逐卡 Y 位移与交错入场，原版安装则使用另一套 260 ms 横移；本工具的横移参数已经对齐。确实缺少的是 BtnStart：原版将它放在滚动内容的同级，借助 MyExtraTextButton.Show 独立展示，原实现把普通 Button 放在 InstallPage 中，导致它跟随整页一起移动、淡入。

## 本轮修改

- 将开始下载按钮放入 InstallPage 同级的 InstallActionHost，仍位于 DownloadViewHost 内，位置、尺寸、模板及安装可用性规则保持原样。
- 按 MyExtraTextButton 的源曲线实现：显示延迟 50 ms，80 ms 淡入；400 ms Back 与 160 ms Fluent 缩放增量并行，250 ms 达到 1.0375，450 ms 归一。返回时 100 ms 缩小，后 50 ms 淡出。
- 展示使用独立的外层缩放，保留按钮模板内层变换；反向从当前缩放和透明度续接，避免重置透明度闪烁。动画只改变呈现，不等待、不启动或取消组件请求。
- 隐藏立即禁止按钮输入，显示透明度完成后才放开本地门控，仍服从页面原有门控。StartInstall 额外拒绝非安装/整合包视图及退出中的按钮事件，防止旧 Click 触发安装。
- 同目标刷新不重播、不截断 450 ms 回弹尾段；离开到设置、整合包、任务进度或关闭时收束。隐藏、卸载和旧完成回调均清理所属时钟；减少动画分支直接显示终态。

本轮不叠加第二套逐卡页面切入动画，不改横移时序、名称、组件选择、搜索、下载校验、隔离或原子发布逻辑。

## 验证

新增 79 项：独立公式和 Clone/Freeze 37 项、真实 WPF 控制器生命周期 23 项、主窗联动 19 项。检查反向连续性、显示/隐藏时长、输入门控、旧时钟、父隐藏与卸载、正常完成、页面返回、深色、名称、组件和活动请求所有权；生成 90/250 ms、退出 75 ms 与深色 250 ms 四张采样图。

策略 `PCL-DB-checks/2026-10-23.19`，UI 876、motion 716、pack-search 320，合计 1912 项；固定 PCL 源码/资源、验证契约和进程工具另查。完整结果、程序及源码摘要和 Commit 绑定位于本轮 `validation.json`、`committed-validation.json`。Core 与其测试未变，不重复计入之前的 Core 6123 项。
