# 组件支持库发布的散列取消

基线 b1fc205032cbcf7545753d8d28f3ec7289905f2c。保留 VB WPF、既有 VB 测试、C# Core、net48、程序集、公开接口、配置和资源名称。

## 复现与修改

ComponentRuntimePublication.Publish 有四个不带 token 的 Matches 调用：源文件、已有目标、临时副本、单库更名后的目标。虽在调用之间检查取消，但读取整个摘要期间使用 CancellationToken.None，无法及时响应已经请求的取消。

新增六个受控场景在首个散列块触发取消，记录实际字节而非耗时。原产品 55/61、六项早停断言失败：每次仍读满 524307 字节，ETag 为 524323（含二次 MD5）；候选 61/61，每次在首个 65536 字节后停止。取消均携原调用方 token；源文件／正确目标状态、临时副本清理、短期读租约释放和新 token 重试均检查。

生产仅在四个原调用增加现有 token 实参，复用既有 Matches(token) 和块级 HashStream；无新接口、算法或生产测试 hook。保持原文件换行与 EOF，四处逆向替换可恢复原 Git 与工作字节；独立审查曾发现编辑工具添加 EOF 换行，已在最终构建前恢复原字节风格。

## 阶段与取消后的状态

| 阶段 | 行为 |
| --- | --- |
| 源文件、已有目标的散列 | 取消后释放读取句柄，不发布新目标或改写原件 |
| 临时副本终验 | 取消后原 finally 删除未提交副本，已有目标保持 |
| 单库原子更名后的终验 | 此时尚未发布整个实例；取消释放局部租约，完整共享库保留，不登记未接受的 FrozenFiles 项，重试可复用 |
| 全局实例成功提交后 | publication.Complete、modsPublication.Complete、ReleasePublishedClient 沿原不可取消清理流程，迟到取消不得反向失败或删除成功实例 |

四个调用原来在 Matches 后就已有取消检查。新增行为是提前停止散列；若坏摘要与取消同时发生，原 Matches(token) 的调用方取消可能先于完整摘要失败，这是此次预期变化。未取消时的校验结果、发布顺序、同级任务首故障与清理异常规则不改。

## 源码依据

PCL2 固定 5ef81e490ae46b1100d61e33d0183094c7a80ca8 的 ModDownloadLib.vb:1837–1845 是组件支持库从私有工作树迁入游戏目录的语义边界；ModLoader.vb:275–287 用执行身份／取消判断工作有效性。C# 参考 CE 915ac29795efbdf6373417389e181e1504117048 的 FileDownloader:195–229 校验／取消后晋升、DownloadResourceManager:249–269 租约释放和 CommonFileProvider:16–30 临时文件替换。

CE 的 ValidateTempFileAsync 使用 Task.Run(checker.Check, token)，不证明运行中的 checker 按块检查取消；本轮只借鉴分阶段和资源归属，不宣称上游与本工具有相同实现。十一份固定 Git 原字节／摘要与独立阶段矩阵见 artifacts/runtime-publication-hash-cancellation/source/manifest.json 和 audit。

## 验证

新增 61 项：六阶段场景各九项，加六个原算法映射及回调恢复检查。算法按阶段分配：source SHA1、existing-match SHA512、existing-mismatch MD5、temporary-copy ETag、committed-new SHA512、committed-replace MD5；不称全交叉。探针仅通过既有 CryptoConfig／FileMatchHashProbe 注入，在进程内恢复原映射与回调，无生产修改。原测试正文不变，Program 只添加专项／全量入口，全量 8045。

提交验收使用最终 DLL：专项 61、全量 VB 8045、Beta 8 原编译宿主 7305、相对当前 C# 的原始／规范 API 零差异（公开 979／Friend 1245、资源 2、JSON 15）。相对原 Beta 8 的两个历史 Friend 新入口仍单独记录，元数据总比较失败不伪称通过。

另核对最终 Release／VS2026 Debug、同一冷目录单 EXE 启动 25／兼容 299／完整 UI 7166、两依赖及七根文档摘要、验证契约 110。实际进程、测试输入、源文件摘要和提交树见 artifacts/runtime-publication-hash-cancellation/validation.json。独立代理负责新增回归、阶段审查和旧宿主／接口验证，主代理实现并完成最终范围检查。

这是临时文件与可控散列验证，不证明真实机器的取消延迟、吞吐量、网络下载、任意 ACL 或游戏安装兼容性；没有用户配置写入、手工 F5 或真实游戏／性能验收。本轮不另跑完整 motion／搜索专项。未打包、推送或发版。

ComponentInstaller 的两个前置库校验、Forge 处理器、共享 Mods 与旧资源发布仍是独立后续范围；没有据此宣称所有组件取消等待都已解决。
