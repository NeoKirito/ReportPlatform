# 实现状态

> 本台账坚持证据优先：**DONE** 表示代码边界已实现且至少具备相应的测试或运行证据；**PARTIAL** 表示可用实现已存在，但生产验收范围尚未闭合；**UNVERIFIED** 表示不应对外宣称通过。

| 区域 | 状态 | 已交付内容 | 当前验证边界 |
|---|---|---|---|
| 旧 API 兼容 | **DONE** | `POST /api/Reports/GetReportByJson` 保留 Controller/Action 路由，接收任意 JSON，保留 `LegacyPayload` 并直接返回 `application/pdf`。 | 使用私有批准的 `xmtm` 请求、本地 API、真实 SQL Server 和免费 FastReport 端到端返回 200/PDF 已通过。 |
| `djwh + bbid` 解析 | **DONE** | `LegacyPayloadReportResolver` 将已证实的 `querytype=djwh + bbid` 路径映射为定义 `djid`。 | 仅证实该路径；direct `djid` 成功路径与 `cxid` 仍不扩展为公共契约。 |
| 遗留 SQL Server 定义与模板 | **DONE** | 定义、Base64 UTF-8 FRX 解码、数据库 SQL、TTL 版本回退和 `Master` 首结果集映射已实现。 | 真实 `dbo.xt_bgdy_djwh_zzj` / `xmtm` 已验证；其他定义家族需各自证据。 |
| 数据提供器和参数绑定 | **DONE（已证实路径）** | 方括号占位符、嵌套 JSON 展平、ANSI 参数绑定与只读 SQL 执行已实现。 | `xmtm` 的 `grtjgcjjgid` / `sfxmddid` 路径和非空 `Master` 已验证；未证实参数语法不模拟。 |
| 免费 FastReport 渲染 | **DONE（基础 PDF）** | `PEIS.Report.FastReport.OpenSource` 隔离项目使用官方 `FastReport.OpenSource` 与 PdfSimple；每请求独立 Report。 | 真实 FRX Load、`Master` RegisterData、Prepare、1 页 `%PDF-` 导出及原兼容接口已通过。 |
| FRX 数据契约 | **DONE（xmtm）** | `Master`、`XMMC/xmmc` 查询大小写边界、`nl` 的实际 CLR 类型和非空数据夹具已记录。 | 真实 `xmtm` 通过；不代表所有遗留 FRX 均已验证。 |
| 水印 | **PARTIAL** | `WatermarkOptions` 与渲染阶段边界存在。 | 基础 PDF Smoke 禁用应用层水印；当前生产水印来源、行为和视觉结果未验证。 |
| PDF 配置 | **PARTIAL** | `legacy`、`screen`、`print-a4`、`label`、`archive` profile 已归一化。 | PdfSimple 的基础导出已验证；高级商业 PDF 能力、旧/新视觉等价和大报告性能未验证。 |
| 渲染指标与并发 | **DONE（实现）** | 阶段计时、缓存、行数、页数、PDF 大小和进程内并发门控已实现。 | 真实单页 `xmtm` 基线已记录；生产负载目标和调优未验证。 |
| B/S 静默打印动作 | **DONE（编排）** | 业务动作、逻辑角色、制品存储、SignalR 批次、幂等和工作站校验已实现。 | 需在线 Agent 和批准的打印机角色绑定完成现场验收。 |
| PrintAgent | **PARTIAL** | SignalR 注册/心跳/自动重连、PDF 下载、工作目录清理、队列和 DryRun/Command 后端已实现。 | Windows Service 安装、受控 Command 后端和物理设备输出未验证。 |
| 物理打印 | **UNVERIFIED** | 代码按同物理打印机串行、不同打印机并行，并带有限次重试。 | 需要 A4、条码设备、驱动、尺寸、中文字体、条码可读性和故障重试现场测试。 |
| 自动测试 | **DONE** | 解决方案包含 API、Engine、SQL Server、PrintAgent 测试；真实数据库/FastReport 测试为显式门控。 | 最近 Release 基线为 `30 total / 24 passed / 0 failed / 6 skipped`；真实 FastReport Smoke 为 `1 passed / 0 failed`。 |

## 当前明确非主张

系统不主张已经完成应用层水印等价、旧/新 PDF 像素级比较、生产大报告压测、IIS/Windows Service 安装包、对外路由认证或物理打印验收。部署与验收顺序见 [USAGE_AND_DEPLOYMENT_GUIDE.md](USAGE_AND_DEPLOYMENT_GUIDE.md)。真实证据与兼容边界见 [FASTREPORT_SMOKE_TEST_STATUS.md](FASTREPORT_SMOKE_TEST_STATUS.md)、[LEGACY_DATABASE_CONTRACT.md](LEGACY_DATABASE_CONTRACT.md) 和 [REAL_FRX_DATA_CONTRACT.md](REAL_FRX_DATA_CONTRACT.md)。
