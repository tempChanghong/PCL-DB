# 本地 Mod：JAR 清单与交互交接

基线 `c1eb204`。继续完善现有本地 Mod 管理，保持 PCL2 风格、PCL-CE 辅助参考。只本地 Commit，不更改版本号、不打包发版。

## 固定源码与范围

核心为 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `LocalResourceFile`、`LocalResourceLoaders`、`PageInstanceMod.EDMods/RefreshUI` 和 `MyLocalModItem`。沿用末尾状态后缀、启用名称、实际运行目录、44 DIP 行和既有页面入退场。PCL 原版成功启停只替换变更行；本工具保留全量摘要校验重扫，单独修正其交互衔接，不照搬覆盖文件行为。

PCL-CE `915ac29795efbdf6373417389e181e1504117048` 的 `ModLocalComp` 仅补充本地元数据及 `${file.jarVersion}` 读取依据。六份固定源码的原字节、Git blob 与 SHA-256 仍见 `artifacts/local-mods/source/manifest.json`。

## JAR 清单识别

真实 JDK 25.0.2 的 `jar --create` 会按字节折行，可能在中文 UTF-8 码点中插入 CRLF 与续行空格。旧实现先把整个清单解码为文本再合并续行，导致这类合法归档产生解码警告、丢失 `Implementation-Title` 或 `${file.jarVersion}` 对应版本。修前复现、原归档、生成脚本与独立读取结果保存在 `artifacts/local-mod-audit-c1/`。

现在仅对 `META-INF/MANIFEST.MF` 先按字节合并主节续行，再严格解码 UTF-8。主节在空行结束，附加命名节不参与 Mod 元数据；保留 2 MiB 上限、重复属性拒绝和错误清单警告。Fabric/Quilt JSON、Forge/NeoForge TOML 的严格读取、错误元数据导入拒绝及坏文件禁用能力保持。

依据 [JAR 规范：Line length / Continuation](https://docs.oracle.com/en/java/javase/25/docs/specs/jar/jar.html)与 [UTF-8 清单编码说明](https://docs.oracle.com/javase/tutorial/deployment/jar/modman.html)。用真实 JDK 生成清单，并以 `java.util.jar.JarFile` 独立读取，确认 29 个“中”字与版本 `7.8.9`。回归内嵌该清单的原字节，摘要 `7B555393B52C9E35CFAA07CBAFF246F65EDD3CA1E8162BC6BFFACAA5EA60DEE6`；无需在测试中依赖本机 JDK 或执行 Mod。

## 启停后的焦点与动画

修前在 39 个 Mod 的列表中滚动到 600 DIP，键盘焦点位于某一启停按钮。操作后重扫保留了滚动位置，但键盘焦点丢到窗口，逻辑焦点仍指向已移除按钮。以原生文件操作、现有控件和隐藏 WPF 表面复现；保留实际文件重扫及共享目录保护，只修复焦点与显示状态交接。

现在文件操作开始前记录实际行焦点，完成重扫后按原文件名、再按启用名称定位当前的新按钮；在页面输入放行后恢复。当前操作与读取代次、启动器/实例/运行目录仍须相符。用户已聚焦搜索框或其他控件时放弃恢复，离页、新读取和新操作清理旧请求。被筛选隐藏的行、读取失败或临时取消按钮退役时只回到可用视口；恢复不改变原滚动位置。没有改变全量读取、摘要校验、PCL 卡片时序或输入门控。

另复现动画门控期间搜索文本变化后列表保持旧结果：普通按键与文本输入由现有 Shell 门控拦截，但原生 `EditingCommands.Delete` 等命令仍可产生 `TextChanged`。现在只记录待处理查询，在 `ContentRevealed/Settled` 交接中应用最新文本，避免显示内容与查询不符；既有门控及旧动画清理继续保留。修前脚本和观测保存在 `artifacts/local-mod-manifest/ui-before/`。

新增 UI 10 项与动画 7 项，检查真实 `Keyboard.FocusedElement`，而非仅逻辑焦点；覆盖无动画、实际文件启停、取消/失败、重扫失败、筛选隐藏行、新搜索选择、切页、旧重扫与新操作、动画放行、快速反向和旧时钟回调。测试为当前线程的隐藏 WPF 来源建立并还原焦点，不显示或激活桌面窗口。

## 验证与边界

新增 31 项清单检查，覆盖 Forge、NeoForge、主清单回退、LF/CRLF/CR、无效主节字节、未使用附加节、重复属性、孤立续行、大小限制、其他元数据严格校验和原文件/配置保持。共享目录、启停、导入、取消回滚与重试继续运行原有 365 项专项；没有因未找到新缺陷而改动文件事务机制。

Release 构建零警告、零错误；PCL 界面来源、验证契约和验证进程工具检查通过。策略为 `PCL-DB-checks/2026-10-25.4`，最终检查绑定同一 App SHA-256，Core/Test 二进制也与完整 Core 回归时的摘要一致。

| 范围 | 通过数 |
| --- | ---: |
| 完整 Core | 6709 |
| 原本地 Mod 专项（包含在 Core） | 125 |
| 状态别名专项（包含在 Core） | 174 |
| TOML 专项（包含在 Core） | 66 |
| 新清单专项（包含在 Core） | 31 |
| 全界面 | 1158 |
| 导航子集（包含在全界面） | 167 |
| 动画 | 1269 |
| 整合包搜索 | 508 |

新交互 10 项和动画 7 项另以最终二进制独立运行通过，再纳入完整套件。上述子集有重叠，不合计为独立场景数。61 个既有真实 JAR 副本全部识别成功、无解析问题、文件摘要相同；原 JDK 归档也从修前版本为空恢复为 `7.8.9` 且无警告，原文件未变。

精确收据、修前失败记录、构建摘要及 Commit 绑定回执保存在 `artifacts/local-mod-manifest/`。验证使用临时目录与受控数据，没有写入用户真实实例、执行 Mod 或启动游戏；隐藏表面时钟检查不代替桌面帧率验收。

保留既有范围：不扫描旧 Forge 的版本子目录、不展开内嵌 Mod，也未接入在线 Mod 更新、删除或依赖安装；不提供进程强制终止后的批次恢复日志。
