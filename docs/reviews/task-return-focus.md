# 任务返回的焦点与视口修复

基线 `cafe5f8aa773a860d132ef566789665433642dda`。本轮继续检查任务页动画的输入与焦点交接，修复后本地 Commit，不打包、推送或发版。

## 问题与证据

任务卡片返回、标题返回和 Escape 共用 `ResumeInstallation`。返回安装页时，原代码在动画结束后调用通用的默认焦点逻辑：空闲时尝试聚焦名称框，任务仍进行时尝试聚焦顶部返回按钮。这两个控件都可能位于当前视口之外；WPF 的自动 BringIntoView 会与保留安装滚动位置的约定冲突。

新增检查在修改前得到预期失败，见 `artifacts/task-return-focus/before-motion.json`：392 项通过后，返回交接未将逻辑焦点交给安装视口。在隐藏 WPF 窗口中，旧代码观察为 TaskReturnButton，滚动偏移仍是 200 DIP。这个记录说明旧实现没有可靠的视口焦点交接，**没有在真实桌面复现自动跳顶**；名称框聚焦引发滚动是根据调用路径与 WPF 行为确定的风险。

## PCL 源码依据

从相邻 PCL 仓库读取固定 `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的四份 Git 对象：

- `Controls/MyScrollViewer.vb`：`PreviewGotKeyboardFocus` 对 MySlider 的 #3854 防护，注释明确说明阻止获得焦点时自动滚动。
- `Controls/MyPageRight.vb`、`FormMain.xaml.vb`：页面入场、退场与交接的控制权。
- `Pages/PageDownload/PageDownloadInstall.xaml.vb`：开始新版本选择及退出选择时显式 `ScrollToHome`；不能把新选择的回顶行为无差别用于恢复已有表单。

规范化副本和 SHA-256 位于本轮 `source/`、`source.json`。上游防护针对滑条，本轮将“避免自动焦点改变已有视口”的原则应用于本程序的任务返回，未声明 PCL 有相同的返回安装表单 API。

## 修改

任务返回安装表单时，把保留视口的意图作为焦点请求闭包参数保存；动画交接结束后，将逻辑焦点明确交给 InstallScroll 并尝试键盘聚焦。滚动容器可聚焦、不是额外 Tab 停靠点。普通首次进入安装页仍沿用原来的名称编辑焦点，新选版本仍明确回顶。

继续经过现有的页面/模式校验和用户输入代次防护；用户在入场期间点击或切换焦点到名称框时，不在动画结束后抢回视口焦点。切到设置、快速切页和卸载沿用原有失效规则。没有增加持久标志、全局禁止 BringIntoView 或事后强制恢复滚动的计时器。

## 验证

新增 24 项，分别运行有动画的蓝色模式与静态深色模式。810 × 470 DIP 窗口中实际滚动 200 DIP，检查名称框确在视口外，不能以 0→0 的偏移冒充滚动保持。覆盖完成任务返回、进行中任务返回、动画交接、用户主动编辑、切设置与过期时钟、新选版本以及卸载。

修复后两组观察均为 InstallScroll 逻辑焦点、200→200 DIP；`task-return-focus-animated.png` 与 `task-return-focus-static.png` 记录返回后的实际呈现。最终门槛 `PCL-DB-checks/2026-10-24.24`：UI 973 / motion 824 / pack-search 320，共 2117 项；另核对固定 PCL 源码/资源、97 项验证契约及 19 项进程工具。最终回执、EXE 摘要、源码清单与 Commit 绑定在 `artifacts/task-return-focus/validation.json` 和 `committed-validation.json`。

隐藏 WPF 窗口的逻辑焦点和真实时钟测试不等于人工桌面键盘/指针验收，也不代表帧率测量。下载/安装内核、真实 PCL 设置及打包实现未变；本轮没有真实联网安装、打包发行或重跑未改动的 Core 套件。
