# 补充版本启动配方校验修复

本批依据早期版本第四至第六阶段研究落地。范围限定于现有七个补充版本，不新增 RC1、Infdev、Classic 或 Indev 条目，不改下载页面、动画、自动选源或打包逻辑。

## 修复行为

此前文件摘要正确、元数据可解析时，缺失启动参数、错误主类以及混合新旧参数仍可能发布成实例。新流程分三层检查：

1. 选中补充版本的身份核对后，检查主类、参数类型、位置参数、目录变量和有效 tweaker。受控旧版元数据不接受混合 arguments 分支、未知选项、重复关键键、无值或歧义引号。
2. 2.0 的库经固定官方参考补全后，检查对应 LaunchWrapper 的坐标、SHA-1、大小及有效声明。原始 2.0 不会因为尚缺 downloads 而提前被错误拒绝。
3. 补全已有实例时，按选中版本检查其启动语义；JSON id 继续允许自定义实例名称。保留窗口、服务器、自定义普通值及 profile 参数，识别 Java 实际支持的单横线选项和简写，避免被未知参数跳过。

六个版本使用 LaunchWrapper 1.5 的默认 VanillaTweaker；c0.0.12a_03 使用 1.6 的显式 AlphaVanillaTweaker。普通官方版本和现代参数没有套用该模板。

未知参数不会消耗下一 token；已识别的保留选项按 Java 的必需值规则取值。空引号保留为空 token，不能令校验器跳过一个空目录值。新源参数限制为 4,096 字符／64 项，已有实例为 65,536 字符／1,024 项；超限会明确拒绝，保留原文件。

校验也保留原始 token 的引号信息：变量前后附加空引号可能阻止 PCL 自动包围含空格路径，即使去掉引号后的文本看似相同，仍会被拒绝。中文空格路径下的截断已通过真实 Java 进程参数解析复现；普通自动加引号和完整引号仍可用。

## 保留的边界

已有 JSON、PCL/Setup.ini 的编码、BOM、格式和隔离设置均不重写。不兼容参数会在索引和内容请求前被拒绝；重新点击不会自动迁移参数。用户明确修正，或换新实例名后，才继续安装。

现有根目录锁、最终 JSON 逐字节重查、读取共享模式和客户端原子替换继续生效。发布前取消、锁内取消和外部同长度修改都纳入补充实例回归。

## 验证入口

```powershell
dotnet build PclDownloadBridge.sln -c Release
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-recipes
./tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe --regression supplemental-versions
./build.ps1
```

专用补充 fixture 使用真实的小型 Mojang wrapper 和明确标记的合成客户端／资源；通用普通版本 fixture 保持原用途。七份真实历史元数据、558 项真实解析器对照、生产安装拒绝／重试、UTF-8 BOM／UTF-16 保留、索引冲突和最终锁均有检查。

这些是自动化和本地集成证据。PCL 界面刷新、真实游戏启动、实际目录落点和存档仍需人工验收；没有因为安装或参数检查通过而宣称已经验证运行兼容。本批不打包发版。
