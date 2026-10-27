# 组件文件名：补齐目录与安装入口

基线：`ecdd201`。沿上一轮整合包文件名与共享目录继续复查，确认组件主链路还有四处错误使用实例名称限制。

## 依据与复现

重读固定 PCL `5ef81e490ae46b1100d61e33d0183094c7a80ca8` 的 `ModDownload`、`ModDownloadLib`。上游 Fabric API／OptiFabric 目录加载资源版本，在 `McInstallLoadersGet` 中将对应文件交给 Mods 目录的下载器；OptiFine 区分独立安装与作为 Mod，LiteLoader 使用额外版本子目录。固定原始 Git 对象与 SHA-256 见 `artifacts/component-file-names/source/manifest.json`。

PCL-DB 的 Fabric API 元数据解析仍调用 `ValidateInstanceName`。匹配当前游戏／加载器的版本若含合法长文件名，会使整份来源被判无效；两来源均返回该内容时，列表报“所有元数据来源均失败”。组件 Mods 计划、OptiFine 安装入口和 API／OptiFabric 文件获取处也保留同样的 100 字符限制。

先加入最终夹具、保持原生产实现构建，得到 **12/25 项失败**：两条 API 目录、六条组件 Mods 计划、四条实际安装。错误与调用栈保留在 `artifacts/component-file-names/baseline.log`；不是编译失败或远端服务错误。

## 修复

四个调用点改用已有的 `ValidatePathSegment`，支持最多 255 字符的普通文件名。实例名称仍通过原入口限制为 100 字符，未修改公共实例规则或路径上界。

原非法字符、保留名、路径段检查和 Mods 计划的进一步目标检查保持；没有取消文件摘要、游戏／加载器匹配、OptiFabric JAR 身份核对或大小写冲突保护。目录分流、原子发布、共享 Mods 事务及动画代码不变。

## 验证

新增 `ComponentFileNameChecks` 25 项，注册到 Core 默认全套及 `--regression component-file-names`：

- 两条 API 目录：150／255 字符文件名可正常返回，保留原文件名及摘要。
- 六条 Mods 计划：Fabric API、OptiFabric、OptiFine 各覆盖两个长度，保留原始目标文件名。
- 十二条非法名称及一条大小写冲突仍拒绝：父目录、保留名、256 字符名称、冒号、同目录大小写别名。
- 四条实际 Fabric API 安装：两个长度 × 隔离／共享 Mods。使用有效元数据 JAR 和回环下载，发布前取消后核对无实例／目标文件；新请求零网络重试，检查完整字节、正确目录、反向目录无副本、既有共享 Mod 和全局配置保持、生成隔离设置、已有目标冲突及安装锁／暂存文件释放。

四条安装检查针对 PCL 配置上下文；OptiFabric／OptiFine 的新增长名称检查针对 Mods 预检计划，不描述为两者长名称的完整实际安装或游戏运行。三种启动器的原生路径与配置另由既有回归覆盖。

修复后新 **25 项通过**。同一最终构建另行执行完整组件目录 **234 项**（包含 Fabric API、OptiFabric 身份与 OptiFine 负载等分组）、组件依赖 **24 项**、隔离安装 **177 项**、原生隔离 **148 项**、上一轮整合包长路径 **36 项**，共 **644 项**。

Release 重建零警告、零错误；验证契约 97 项、限时策略 44 项、验证进程 19 项及固定 PCL 源码／资源／控件另验。源文件清单、二进制摘要和实际执行回执见 `artifacts/component-file-names/validation.json`。

本轮没有重跑 Core 全套、UI 套件、真实远端下载或桌面帧率检查。游戏文件和外部配置均为临时夹具，未写入用户实际实例或参考源码仓库；只本地 Commit，不打包、推送或发版。
