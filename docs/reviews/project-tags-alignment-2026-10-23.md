# 整合包分类徽章与加载器声明

日期：2026-10-23。基线：`a447d50`。本轮补齐搜索列表和详情顶部的项目分类与加载器展示，验证后本地 Commit，不推送、打包或发版。

## 来源与复现

重新读取未限制 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的六份 Git 文件：`ResourceProject.vb`、`ResourceVersion.vb`、`MyResourceItem.xaml`、`MyResourceItem.xaml.vb`、`PageDownloadCompDetail.xaml.vb` 和 `PCLCS/Constants.cs`。规范化副本及 SHA-256 记录于 `artifacts/project-tags-alignment/source/`；最终绑定时重新核对原 Git 对象。

原解析器没有保留平台的分类和项目加载器声明。新增回归在修复前失败：`Modrinth project categories survive the accepted catalogue response as PCL translated tags`。`before-core.json`、stdout/stderr 保留了该次失败；不把修复后的夹具当作原始失败证据。

## 实现

- CurseForge 使用固定 PCL 的 16 项整合包分类 ID，去重后按 ID 降序排列。Modrinth 使用 7 项共用分类和 6 项整合包分类翻译，按声明顺序去重。未知类别不猜测翻译，缺少、null 或错误类型的可选分类不影响正常项目返回。
- Modrinth 从 `categories` 与 `loaders` 合并加载器；兼容搜索只在分类中返回加载器的情况。基础顺序沿用 PCLCS 的 Forge / LiteLoader / Fabric / NeoForge，另外保留本工具已有发布标签中的 Quilt / Rift / 原版与别名。明确列举实际声明，不按加载器集合推断“任意”。
- CurseForge 只合并 `latestFiles` 中属于当前项目、声明为可用客户端 ZIP 的加载器标签；忽略服务端包、提前访问、非已批准状态、其他项目文件和畸形字段。没有从数字 `latestFilesIndexes.modLoader` 猜测名称，也没有把少量最新文件推断为整个项目的游戏版本范围。缺失仍保留未知状态。
- 可选展示字段独立解析，既有项目 ID、项目类型、日期、版本数组等必需字段的校验保持原有规则。新增标签不会触发额外目录、版本或文件请求，也不会保存下载 URL。
- 徽章沿用 PCL 的 11 DIP 文字、3/1 DIP 内边距、3 DIP 圆角、3 DIP 右边距及亮色 `#11000000` / `#868686` 配色；深色模式改用浅色半透明底和可读文字。通过动态资源立即跟随主题，无单独动画时钟。
- 徽章与简介共用现有 18 DIP 单行文字带，资源行仍为 64 DIP。为处理大量分类，采用 InlineUIContainer 使整行统一省略并裁切；这属于容器适配，PCL 源码使用独立的 Auto 列和标签 StackPanel。正常标签数量保持相同的视觉排列，完整分类和简介保留在提示中。
- 加载器前缀与数字排序后的发行系列共同展示。提示明确其为项目标签，各文件支持情况仍以版本列表及包内清单为准。

## 与安装和动画的关系

仅给 `ModpackProject` 增加 `Tags` / `Loaders`；没有给 `ModpackRelease` 或归档缓存键增加新字段。项目标签不作为安装授权或兼容性结果，不新增 Quilt / Rift 等组件安装能力。

已核对详情接口的新标签只替换其自身摘要，不改写已有搜索快照。夹具故意让详情声明 Fabric、具体文件声明 Forge，验证两者仍独立。实际选中的发布 ID、游戏版本、加载器、下载 URL、摘要及归档安装流程继续由原来的文件对象承载。版本隔离、共享 Mods、已有实例设置及原子发布均未修改。

徽章不会使静态详情头部可点击；禁用与恢复交互继续撤销旧手势，搜索行仍按原对象触发一次。快速明暗切换后没有残留的徽章时钟或旧悬停动画，所有颜色跟随当前资源。

## 验证与交付边界

新增 89 项 Core 断言、20 项搜索界面断言：平台完整分类映射、重复/未知/畸形可选字段、两种目录入口、源 JSON 不变、请求数不增加、外来/服务端/不可用文件排除、别名与加载器顺序、保留未知状态、文件身份与摘要不变、分类徽章、主题变化、长标签省略、最小窗口、旧手势撤销及搜索/详情元数据隔离。

策略 `PCL-DB-checks/2026-10-23.13`：Core 6007、UI 799、motion 637、pack-search 269，主套件合计 7712 项断言；工具契约 97 项、验证进程工具 19 项另计。最终状态、相同 EXE/Core 摘要、源码清单和 Commit 绑定以 `artifacts/project-tags-alignment/validation.json` 与 `committed-validation.json` 为准。

执行 `./build.ps1`，不带 Package；复查亮色、深色和多分类隐藏呈现源截图。此轮未访问用户游戏目录、未联网搜索或安装真实整合包，离屏截图不作为真人桌面、DPI 或帧率验收。没有创建发行候选。

下一轮可处理 PCL 的 LoaderWait 与最小停留时间，重点验证快速返回、旧请求回调、取消重试和隐藏页面，避免加载动画短暂闪现；全局发行范围压缩仍应等完整且可信的版本序列接入后再实现。
