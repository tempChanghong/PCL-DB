# 来源与依赖

PCD（Plain Craft Downloader）是独立下载补全项目。关于页中的原作者与贡献者信息用于来源致谢，不表示上游参与、维护或认可本项目。

- PCL2（Meloong-Git/PCL）：目录读取、版本安装、下载与视觉设计的主要代码参照，原作者及贡献者保留相应权利。具体基线和适配关系见 `SOURCE_REFERENCES.md`。本应用是独立的本地预览项目，不是 PCL 官方发行版。
- 0.26 的标题纹理直接使用 PCL `Images/Themes/12.png`，保留原始图片字节和透明度；配色预设除默认蓝色参数外为本项目独立设置，未搬运主题解锁、赞助或账号数据。
- 0.27 的关于页参照同一固定基线的 `PageOtherAbout.xaml(.vb)`，采用信息行、圆形图标和操作区布局；项目圆标及文字缩写为本项目制作。BMCLAPI 由 bangbang93 维护，源码链接为 https://github.com/bangbang93/openbmclapi。PCL-CE、HMCL 与 PrismLauncher 的补充参照范围见 `SOURCE_REFERENCES.md`。
- PCL 的许可文件实际名为 `LICENCE`。本项目根目录原样保留固定基线的该文件，其中链接《PCL 分发有限许可》，并附《PCL 存储库合理使用指南》。此前“未见统一 LICENSE”的说明遗漏了这个拼写，现予更正。
- 本项目大量参照 PCL 的下载功能、控件及素材；应按上游指南的“重度使用”部分核对，而不能仅按少量代码参考处理。该部分涉及第三方声明、项目命名、关于页首位署名及赞助链接、公开源码，以及 Windows 版主题表现。当前 PCD 命名与多色主题仍需额外授权或调整；附上许可文件不表示这些事项已经解决。
- 本仓库不将 PCL 源码、字体、图片或其他第三方内容重新许可为 MIT、Apache 或 GPL；各自原有权利与要求继续适用。原始资源的固定提交及 SHA-256 见 `reference/pcl-assets.json`，源码快照见 `reference/PCL/manifest.json`。
- Newtonsoft.Json 13.0.3：MIT License，Copyright (c) 2007 James Newton-King。

## Newtonsoft.Json MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
