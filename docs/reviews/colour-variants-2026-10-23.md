# 2.0 三色版说明与选择联动

本轮完善已接入的三色版说明，没有增加补充版本（仍为 86 项），没有改动下载 ID、URL 映射、摘要、启动配方或资源路径。

## 用户可见变化

|版本|列表副标题与选中提示中的说明|
|---|---|
|2.0（蓝色版）|新增彩蛋功能，不含超级敌对模式|
|2.0（红色版）|新增内容仅含超级敌对模式|
|2.0（紫色版）|新增彩蛋功能与超级敌对模式兼有|

“新增内容”指 2.0 愚人节追加的功能，并非红色版没有基础游戏玩法。依据：[中文 Minecraft Wiki 的下载栏与备注](https://zh.minecraft.wiki/w/Java%E7%89%882.0?variant=zh-cn)。说明只适用于三个明确的 UVMC canonical ID；普通清单的 `2.0` 保留原 PCL 彩蛋文案，不根据无颜色的名称猜测客户端。也不将过去 PCL 的单个 `2.0` 条目认定为某个颜色。

## PCL 源码对照

参考固定提交 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`：

- `ModDownloadLib.vb / McDownloadListItem` 使用 42 DIP 行，优先将 `lore` 显示为副标题。本轮沿用原字体、图标、行高与布局，在现有副标题中说明三色差异。
- `ModMinecraft.vb / GetMcFoolName` 对 2.0 使用统一的 2013 年彩蛋文案。本轮保留这份原文及其余九项文案，三色说明仅覆盖明确的补充版本呈现。
- `PageDownloadInstall.xaml.vb / MinecraftSelected` 分别保存标题、Tag 元数据与图标。本轮继续将显示名与下载身份分离，回归检查蓝／红／紫切换后仍绑定原始条目，ID、来源 ID、URL、摘要、分类和日期不变。
- 同文件的 `EnterSelectPage` 提供页面透明度、位移和输入门控参考。本轮不调整动画时间或布局结构，使用现有动画及刷新回归检查切换过程。

源码：[列表呈现](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/ModDownloadLib.vb#L76-L104)、[选择流程](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Pages/PageDownload/PageDownloadInstall.xaml.vb#L90-L150)、[彩蛋文案](https://github.com/Meloong-Git/PCL/blob/5ef81e490ae46b1100d61e33d0183094c7a80ca8/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb#L901-L906)。

## 验证

扩展既有界面工作流检查，依次选择红、紫、蓝并核对副标题与选中提示跟随当前条目；不发起组件请求，不改变原始身份，也不进入最新推荐。普通 `2.0` 保留通用文案。未为纯文案字面值单独增加单元测试。

内部离线预览增加 `versions-colours`、`versions-colours-dark` 与 `version-colour-red` 场景，不增加用户页面或设置项。已检查 850×500 浅色／深色列表、680×500 小窗口列表及红色版安装页的真实 WPF 控件渲染，确认三色说明未被截断、提示换行正常。预览使用合成目录数据，不能作为游戏安装或实际游玩证据。

```powershell
./build.ps1
./src/PclDownloadBridge.App/bin/Release/net48/PclDownloadBridge.exe --snapshot './artifacts/colour-variants-20261023/minimum.png' --page versions-colours --size 680x500
./src/PclDownloadBridge.App/bin/Release/net48/PclDownloadBridge.exe --snapshot './artifacts/colour-variants-20261023/dark.png' --page versions-colours-dark --size 850x500
./src/PclDownloadBridge.App/bin/Release/net48/PclDownloadBridge.exe --snapshot './artifacts/colour-variants-20261023/red.png' --page version-colour-red --size 680x500
```

最终源码清单、核心／界面／动画／搜索回执、进程记录和截图保存在忽略目录 `artifacts/colour-variants-20261023/`，通过状态以 `validation.json` 为准。本轮不重复下载游戏文件；既有安装证据参见此前各批报告，不作为本轮重新执行的证据。未进行游戏启动验收，不推送、不打包、不发版。
