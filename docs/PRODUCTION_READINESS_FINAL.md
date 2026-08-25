# Production Readiness Finalization

**Repository:** `NeoKirito/ReportPlatform`

**Assessment date:** 2026-08-25

**Implementation evidence commit:** `616c0066d160bd6616354541de576bea2cd2223e`

**Base main:** `f8484acadc7c5628a9c3e3eaf5b54595d603656e`

**Source branch:** `origin/feat/printagent-multi-workstation` at `b83b7ed4f7fc8310d82295693cb0d9f9c34b568a`

## 1. Executive Conclusion

> **READY_FOR_CONTROLLED_FIELD_DEPLOYMENT**

在本轮范围内，已关闭可在代码仓库内解决的已知 P0：打印任务和幂等键持久化、重启恢复、状态机约束、生产环境 Agent 注册令牌强制、制品的代理绑定短时授权、制品保留/清理、受保护的管理端点、命令打印 shell 禁用与可执行文件边界、受控 Windows 发布脚本均已有实现与自动测试。

该结论只表示**代码、自动化测试与部署资产适合进入受控现场验收**。它不表示所有历史 FRX、真实 A4/条码实体打印、字体、水印视觉、旧新 PDF 或生产负载已经通过。真实 SQL/FastReport 集成开关在本执行环境中未获授权，因此相关测试保持跳过而未被替代或伪造。

## 2. Git Evidence

| 项目 | 证据 |
|---|---|
| Base main | `f8484acadc7c5628a9c3e3eaf5b54595d603656e` (`docs: add usage and deployment guide`) |
| Source branch | `origin/feat/printagent-multi-workstation`，`b83b7ed4f7fc8310d82295693cb0d9f9c34b568a` |
| Working branch | `feat/production-readiness-final` |
| Implementation commit | `616c0066d160bd6616354541de576bea2cd2223e` |
| Source integration | 工作分支从上述多工作站分支创建，因此其内容已包含在最终候选中。 |
| Worktree before final-report commit | 清洁；本报告作为独立审计文档提交。 |

## 3. Automated Gates

| Gate | Result | Evidence / limitation |
|---|---|---|
| `dotnet restore PEIS.ReportPlatform.sln` | PASS | 在 .NET SDK 10.0.111 隔离环境完成。 |
| `dotnet build PEIS.ReportPlatform.sln -c Release --no-restore` | PASS | 全解决方案 Release 编译通过。 |
| `dotnet test PEIS.ReportPlatform.sln -c Release --no-build` | PASS | **45 passed, 0 failed, 6 skipped**。 |
| API durability/security tests | PASS | `PEIS.Report.Api.Tests`：20 passed，覆盖重启恢复、幂等回放、状态机、制品清理、短时下载签名与注册令牌策略。 |
| PrintAgent tests | PASS | `PEIS.PrintAgent.Tests`：10 passed，覆盖稳定 AgentId、队列/重试及命令参数边界。 |
| Real SQL/FastReport smoke | SKIPPED — external approved connection not present | `REPORTPLATFORM_TEST_SQLSERVER` 与 `REPORTPLATFORM_TEST_FASTREPORT` 均未设置；没有尝试猜测连接串或访问医院网络。 |
| `git diff --check` | PASS | 最终源代码门禁执行时无空白错误。 |
| Dependency vulnerability scan | PASS | `dotnet list ... package --vulnerable --include-transitive` 未报告漏洞；SQLite 安全包覆盖已验证。 |
| Secret / artifact scan | PASS | 未发现高置信度 API 密钥、私钥、非空连接串、真实 PDF/FRX、数据库、`bin/`、`obj/`、`.runtime/` 或发布产物被追踪。 |
| Windows publish | PASS | 已生成 `publish/report-api/`（framework-dependent, win-x64）和 `publish/print-agent/`（self-contained, win-x64）。 |

## 4. Production Capability Matrix

| 能力 | 状态 | 说明 |
|---|---|---|
| Legacy `POST /api/Reports/GetReportByJson` / `application/pdf` 契约 | DONE | 未改变既有兼容控制器路径。 |
| 真实 `xmtm` 基础 SQL/FRX/PDF | PARTIAL | 既有获批 Smoke 已记录；本轮因无授权环境变量未重跑。 |
| 多工作站 Agent | DONE | 稳定 GUID、唯一 StationId、心跳超时、重新连接/注册、逻辑角色验证均在代码中实现。 |
| 打印任务、批次和幂等 | DONE | SQLite 保留 Job/Target/Artifact/Agent/Station/状态/错误/完成时间及幂等回放；API 重启恢复测试通过。 |
| 重复派发保护 | DONE | 成功 SignalR 派发后持久化为 `Dispatched`；断线期间不作盲目服务器重发，避免未知回执窗口重复实体打印。 |
| 制品访问与生命周期 | DONE | GUID 制品、AgentId 绑定、HMAC 短时签名、过期/容量清理、活动任务保护与清理异常隔离。 |
| 管理接口保护 | DONE | `/internal/*`、Agent 枚举、手工打印和任务查询由可配置内部令牌保护；开发放行必须明确 Opt-In。 |
| PrintAgent 命令后端 | DONE | 禁止 shell/script host，要求本机绝对可执行文件、无 shell、结构化 `ArgumentList` 和受限占位符。 |
| Windows 部署 | DONE | 提供发布脚本、用户登录计划任务安装/卸载模式与安全参数校验。 |
| 历史 FRX 全覆盖 | FIELD_REQUIRED | 见兼容矩阵。 |
| 实体打印、扫码、字体、水印、旧新视觉与生产负载 | FIELD_REQUIRED | 见现场清单和 PDF 比较方法。 |

## 5. P0 Findings

| Finding | Risk | Fix | Tests | Status |
|---|---|---|---|---|
| 任务、回执和幂等键仅在 API 内存中 | API 重启丢失状态，浏览器重试可能生成第二任务 | 新增 SQLite `PrintJobs`、`PrintJobTargets`、`PrintIdempotency`；任务先持久化，完成幂等映射后才派发 | 重启恢复、同键回放、失败释放测试 | CLOSED |
| 派发与 Agent 首次回执之间没有可识别状态 | 无法区分尚未发送和已发送未确认 | 新增 `Dispatched` 状态；只在 SignalR 调用成功后写入，禁止非法回退 | 状态机测试：派发前拒绝下载、派发后允许下载、终态拒绝回退 | CLOSED |
| 生产环境可空令牌注册 | 非授权工作站可能注册 | 空 token 仅在 `AllowInsecureDevelopment` 明确开启且非 Production 时允许 | 注册安全选项测试 | CLOSED |
| 知道 ArtifactId 即可下载 PDF | 医疗 PDF 非授权访问 | Agent 绑定、短时 HMAC 签名、状态库授权校验、固定 MIME 与不可预测 GUID | 签名绑定/过期/生产空密钥测试 | CLOSED |
| 制品目录无生命周期管理 | PHI 文件积累或磁盘耗尽 | 定期与启动清理、保留时长、字节/数量上限、活动任务保护、错误隔离 | 过期删除、活动保留、异常隔离测试 | CLOSED |
| 命令打印可由模板形成高风险命令线 | 可能被 shell 解释或启动不受控程序 | 禁止 shell/script host，绝对路径存在性、受限占位符、`ArgumentList` | 命令边界测试 | CLOSED |

## 6. P1 Findings

| Finding | Risk | Fix / disposition | Status |
|---|---|---|---|
| Agent 在线连接本身不跨 API 重启持久化 | API 重启后连接对象必然失效 | Agent 自动重连并重新注册；持久化 Job 状态不依赖连接表。单节点受控部署的恢复契约已满足；多节点共享在线状态属于后续扩展，不是本轮遗留门禁。 | DONE（单节点） |
| PDF 视觉差异不能字节比较 | 可能把元数据差异误判为报表差异 | 提供本地页面尺寸、渲染哈希、像素差异和差异图工具，且 `.runtime/evidence/` 忽略。 | CLOSED（工具）；FIELD_REQUIRED（结论） |
| 性能证据只有单页真实 Smoke | 不能外推全系统容量 | 不作性能 PASS 声明；大型真实报告属于现场容量门禁。 | FIELD_REQUIRED |

## 7. Remaining Field Gates

| Field gate | Required evidence |
|---|---|
| 真实 A4 和条码实体打印 | 正确纸张、驱动、页边距、每次业务点击仅一次实体输出。 |
| 条码扫码可读性 | 目标标签、目标扫码设备的可读业务值。 |
| 中文字体与水印视觉 | 目标 Windows 字体、屏幕和纸张的人工验收。 |
| 旧/新 PDF 对比 | 依据 `PDF_VISUAL_ACCEPTANCE.md` 在本地受控证据目录生成比较结果并逐页判断。 |
| 大型真实报告压力 | 获批只读连接下的容量、并发、内存与队列观测。 |
| 医院网络、TLS、权限和故障演练 | HTTPS、计划任务用户上下文、打印机离线/卡纸/缺纸、API/Agent 重启及回滚演练。 |

## 8. Deployment and Rollback

运行 `scripts/Build-Release.ps1` 可生成 `publish/report-api/` 与 `publish/print-agent/`。生产配置应由环境变量或受控 `appsettings.Production.json` 提供 `PrintAgentSecurity:RegistrationToken`、`InternalApiSecurity:AccessToken` 与 `ArtifactAccess:SigningKey`，不得写入仓库。使用 `Install-PrintAgentAutoStart.ps1` 时必须提供 HTTPS `ServerUrl`、非空注册令牌及逻辑打印机绑定；不安全模式只允许显式开发 Opt-In。

回滚保持服务级而非页面级：停止/撤销新 API 服务或将反向代理切回旧 PEIS 报表服务；禁用 `PEIS PrintAgent` 计划任务；恢复先前受控配置。旧 PEIS 调用契约未改动，因此不应需要批量修改 PEIS 页面。现场回滚应按 `FIELD_ACCEPTANCE_CHECKLIST.md` 的 F-19 记录。

## 9. References

本报告中的运行结论来自本仓库内的命令输出、自动测试和提交记录；现场验收方法与模板边界分别见 [兼容性矩阵](REPORT_COMPATIBILITY_MATRIX.md)、[PDF 视觉验收方法](PDF_VISUAL_ACCEPTANCE.md) 和 [现场验收清单](FIELD_ACCEPTANCE_CHECKLIST.md)。SQLite 依赖的已知高严重度漏洞复核依据 GitHub Advisory 数据库对受影响版本的记录。[1]

[1]: https://github.com/advisories/GHSA-2m69-gcr7-jv3q "CVE-2025-6965: SQLitePCLRaw.lib.e_sqlite3 vulnerability advisory"
