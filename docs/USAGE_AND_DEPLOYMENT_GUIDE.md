# PEIS.ReportPlatform 使用与部署指南

> 本指南对应 `main` 分支的当前实现。它区分**已通过代码或真实运行验证的能力**与**仍需医院现场完成的部署验证**，不将未验证事项描述为已上线能力。

## 1. 程序解决什么问题

PEIS.ReportPlatform 是 PEIS 体检系统的报表/PDF 与 B/S 静默打印平台。它的首要目标是让旧 PEIS 页面继续按原方式调用报表接口，同时把报表定义、FRX、数据查询、PDF 生成和工作站打印拆分为可替换、可观测的组件。

| 能力 | 当前状态 | 使用入口或边界 |
|---|---|---|
| 旧报表接口兼容 | **可用** | `POST /api/Reports/GetReportByJson` 接收原始 JSON，返回 `application/pdf`。 |
| 遗留 `djwh + bbid` 报表解析 | **已按真实 `xmtm` 样本验证** | `querytype=djwh` 且 `bbid` 解析为遗留定义表的 `djid`。 |
| 遗留 SQL Server 定义/FRX/数据加载 | **已按真实 `xmtm` 验证** | 从 `dbo.xt_bgdy_djwh_zzj` 读取 Base64 UTF-8 FRX、数据库 SQL 与数据集。 |
| 免费 FastReport PDF 渲染 | **已按真实 `xmtm` 验证** | 使用 `FastReport.OpenSource` 与官方 `PdfSimple` 插件；每请求独立 `Report` 实例。 |
| 渲染观测 | **可用** | 内部诊断接口展示定义缓存、渲染并发和最近渲染遥测。 |
| PDF 获取 | **可用** | 兼容接口直接返回 PDF；打印工作流的制品通过 `/api/print/artifacts/{artifactId}` 下载。 |
| B/S 业务静默打印编排 | **代码完成，需现场代理验证** | `POST /api/print/actions` 按逻辑打印角色路由至在线工作站代理。 |
| Windows PrintAgent | **前台运行与 DryRun 可用** | SignalR 注册、心跳、下载 PDF、同打印机串行队列、重试。 |
| 物理打印输出 | **未验证** | 需要医院工作站、真实 A4/条码打印机、实际命令行打印后端的现场验收。 |
| 应用层水印 | **未验证** | 基础 PDF Smoke 明确禁用应用层水印；不得据此声称生产水印已等价。 |

## 2. 系统组成与数据流

```text
旧 PEIS / 新 B-S 页面
       |
       |  原始 JSON
       v
PEIS.Report.Api
  ├─ 兼容控制器：保留 LegacyPayload
  ├─ Legacy SQL Server：定义、FRX、数据 SQL
  ├─ Report Engine：缓存、数据集、并发门控、遥测
  ├─ FastReport.OpenSource：Load FRX / RegisterData / Prepare / PDF
  └─ Print 协调器：保存制品、路由 SignalR 批次
                                      |
                                      v
                              Windows PEIS.PrintAgent
                              ├─ 注册本机打印机
                              ├─ 下载 PDF 制品
                              └─ 按物理打印机队列打印
```

报表定义、FRX 模板和报表 SQL 的生产来源是遗留 SQL Server，不能把某个本地 FRX 文件夹当作生产定义源。当前已证实的 `djwh` 映射使用 `dbo.xt_bgdy_djwh_zzj`，其 `dj_frx` 为 Base64 UTF-8 FRX，首个查询结果集需命名为 `Master`。

## 3. 报表 API 怎么用

### 3.1 旧 PEIS 兼容接口

旧页面保持原 URL、HTTP 方法和 JSON 结构即可：

```http
POST /api/Reports/GetReportByJson
Content-Type: application/json
```

请求体由调用方原样提供。控制器会完整保留请求作为 `LegacyPayload`；不要为了新服务先改名、删除或扁平化旧字段。响应是直接的 PDF 二进制流：

```http
200 OK
Content-Type: application/pdf
Content-Disposition: attachment; filename=<report>.pdf
```

目前已通过真实数据库和非空样本验证的是 `querytype=djwh`、`bbid=xmtm` 路径。direct `djid` 并非已确认的成功公共契约；`cxid` 目前不属于已证实的生产报表路径。详见 [LEGACY_REQUEST_RESOLUTION.md](LEGACY_REQUEST_RESOLUTION.md)。

### 3.2 健康与诊断接口

| 目的 | 路径 | 说明 |
|---|---|---|
| 健康检查 | `GET /health` | 返回服务状态。 |
| 兼容接口连通性 | `GET /api/Reports/Test` | 返回 `OK`，不生成报表。 |
| 渲染诊断 | `GET /internal/diagnostics/rendering` | 定义缓存、并发门控和最近渲染遥测。 |
| 单报表缓存失效 | `POST /internal/cache/reports/{reportId}/invalidate` | 移除该逻辑报表的版本化定义缓存。 |
| 新集成诊断 PDF | `POST /internal/reports/pdf` | 新的类型化接口；不是旧 PEIS 的迁移契约。 |

> **安全要求：**当前这些内部路由和 PDF 制品下载路由没有在应用内实现身份认证。生产部署必须把 `/internal/*` 与 `/api/print/artifacts/*` 放在受控内网、反向代理认证或网关授权策略之后，绝不能直接暴露至互联网。

## 4. B/S 静默打印怎么用

正常 B/S 页面不传物理打印机名称，而是提交业务动作和工作站标识：

```http
POST /api/print/actions
Content-Type: application/json
```

```json
{
  "actionCode": "REGISTRATION_PRINT",
  "stationId": "REG-01",
  "parameters": {
    "<业务参数名>": "<业务参数值>"
  },
  "jobName": "登记打印",
  "idempotencyKey": "<可选且稳定的业务操作键>"
}
```

`REGISTRATION_PRINT` 的开发配置示例会生成两份文件：导检单路由到 `A4_GUIDE`，条码路由到 `BARCODE`。API 根据在线 PrintAgent 上报的本机打印机和 `PrinterBindings` 找到实际物理打印机。缺失代理、角色绑定或目标打印机时，默认在开始打印前失败，避免部分打印。

```text
业务动作 -> 场景文档 -> 逻辑打印角色 -> 工作站绑定 -> 物理 Windows 打印机
```

同一物理打印机上的作业串行执行；不同物理打印机可并行。启用 `idempotencyKey` 后，网络重试可复用同一个业务打印作业。物理打印需要现场验证，部署初期应先以 `DryRun` 验证路由与下载流程，再切换到经过批准的 `Command` 后端。

## 5. 生产配置

源代码中的 `appsettings.json` 只提供开发默认值。生产连接字符串、站点绑定、物理打印机名和任何机密都必须放在部署平台的密钥管理、受保护的环境变量或受 ACL 保护的生产配置文件中，**不得提交到 Git**。

### 5.1 报表 API 的必要配置

以下是已证实 `djwh` 路径的最小配置形状；`ConnectionString` 必须替换为经过批准的只读连接，不应写入文档或仓库。

```json
{
  "ReportEngine": {
    "DefinitionSource": "LegacySqlServer",
    "Renderer": "FastReportOpenSource"
  },
  "ReportDatabase": {
    "Provider": "SqlServer",
    "ConnectionString": "<secret:approved-read-only-connection>",
    "CommandTimeoutSeconds": 120,
    "DefinitionCacheTtlSeconds": 300
  },
  "LegacyReportSchema": {
    "DefinitionTable": "dbo.xt_bgdy_djwh_zzj",
    "ReportIdColumn": "djid",
    "TemplateColumn": "dj_frx",
    "SqlColumn": "djsql",
    "VersionColumn": "",
    "UpdatedAtColumn": "",
    "TemplateContentEncoding": "Base64Utf8",
    "FirstResultSetTableName": "Master",
    "TemplateKeyPrefix": "legacy-djwh"
  },
  "Rendering": {
    "MaxConcurrentRenders": 2
  }
}
```

在 Windows 环境中，环境变量使用双下划线代替层级分隔符，例如：

```powershell
$env:ReportEngine__DefinitionSource = 'LegacySqlServer'
$env:ReportEngine__Renderer = 'FastReportOpenSource'
$env:ReportDatabase__ConnectionString = '<只读连接，仅部署环境保存>'
$env:LegacyReportSchema__DefinitionTable = 'dbo.xt_bgdy_djwh_zzj'
$env:LegacyReportSchema__ReportIdColumn = 'djid'
$env:LegacyReportSchema__TemplateColumn = 'dj_frx'
$env:LegacyReportSchema__SqlColumn = 'djsql'
$env:LegacyReportSchema__TemplateContentEncoding = 'Base64Utf8'
$env:LegacyReportSchema__FirstResultSetTableName = 'Master'
$env:LegacyReportSchema__TemplateKeyPrefix = 'legacy-djwh'
```

`FastReport.OpenSource` 与 `FastReport.OpenSource.Export.PdfSimple` 均锁定为 `2026.2.3`，由官方 NuGet.org 提供并采用 MIT 许可证。分发包时保留 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。免费 PDF Simple 插件已通过基础 PDF Smoke，但不应声称已具备商业版的加密、数字签名、字体嵌入或像素级等价能力。

### 5.2 PrintAgent 必要配置

在每一台需实际打印的 Windows 工作站上，复制并修改 `PEIS.PrintAgent` 的配置：

```json
{
  "Agent": {
    "ServerUrl": "https://<受控内网报告服务地址>",
    "AgentId": "<稳定的工作站代理编号>",
    "StationId": "REG-01",
    "PrinterBindings": {
      "A4_GUIDE": "<本机 A4 打印机名称>",
      "BARCODE": "<本机条码打印机名称>"
    },
    "HeartbeatSeconds": 20,
    "WorkDirectory": "D:\\PEIS\\PrintAgent\\runtime",
    "PrintBackend": {
      "Mode": "DryRun",
      "Executable": "",
      "ArgumentsTemplate": "{file} {printer} {copies} {duplex}",
      "RetryCount": 1,
      "RetryDelaySeconds": 2
    }
  }
}
```

只有在本机选定、授权并人工验证了命令行打印工具和参数后，才把 `Mode` 改为 `Command` 并配置 `Executable` 与 `ArgumentsTemplate`。不能把示例中的 `Microsoft Print to PDF` 当作生产物理打印配置。

## 6. 构建、发布和启动

### 6.1 构建前提

| 项目 | 要求 |
|---|---|
| SDK | .NET SDK 10（仓库 `global.json` 约束 SDK 系列）。 |
| API 主机 | 当前真实 Smoke 在 Windows/.NET 10 环境完成；其他操作系统需先完成本地 FastReport PDF 验收。 |
| 数据库 | 经批准的遗留 SQL Server **只读**账号、网络连通性和 TLS/证书策略。 |
| 包源 | 能访问官方 NuGet.org 的构建环境，或使用经批准的内部镜像。 |
| 工作站 | 安装目标打印机驱动，允许 Agent 的工作目录读写。 |

### 6.2 从源码构建

在仓库根目录执行：

```powershell
$env:ProgramFiles`(x86`) = 'C:\Program Files (x86)'
dotnet restore PEIS.ReportPlatform.sln
dotnet build PEIS.ReportPlatform.sln -c Release --no-restore
dotnet test PEIS.ReportPlatform.sln -c Release --no-build
```

### 6.3 发布 API

```powershell
dotnet publish src/PEIS.Report.Api/PEIS.Report.Api.csproj -c Release -o .\publish\report-api
```

将发布目录部署到受控 Windows Server 或经本地验收的运行环境。为运行身份授予应用内容目录和 `.runtime` 子目录的最小读写权限。静默打印制品会落在 `<ContentRoot>\.runtime\pdf-artifacts`；当前实现不自动清理该服务器制品目录，因此生产上线前必须制定容量、保留期和安全清理策略。

先在受控内网前台运行确认配置：

```powershell
Set-Location .\publish\report-api
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5080'
dotnet .\PEIS.Report.Api.dll
```

确认 `GET /health` 返回正常后，再接入 IIS、反向代理或企业进程托管。部署平台负责 HTTPS 终结、主机名、服务重启、日志轮转、访问控制和对内部诊断/制品路由的隔离。当前项目没有把 API 主机包装成 Windows Service；若使用 Windows Service 或 IIS，须按医院既有托管规范完成安装及重启策略验证。

### 6.4 发布 PrintAgent

```powershell
dotnet publish src/PEIS.PrintAgent/PEIS.PrintAgent.csproj -c Release -r win-x64 --self-contained false -o .\publish\print-agent
```

在每个工作站把已审批配置置于发布目录或受保护外部配置位置，先以前台方式启动：

```powershell
Set-Location .\publish\print-agent
.\PEIS.PrintAgent.exe
```

验证 Agent 出现在 `GET /api/print/agents` 后，执行 `DryRun` 打印动作。当前代码的 Agent 是长期运行的 Generic Host，但未提供受测的 Windows Service 安装脚本；将其注册为服务、任务计划或第三方守护进程属于现场部署工作，必须单独验收运行账户、网络、打印机访问权限和崩溃重启策略。

## 7. 上线验收顺序

建议按以下顺序降低风险：

1. **数据库只读验证：**使用 `PEIS.LegacyDbInspector` 检查目标库对象和定义；不允许任何写入操作。
2. **报表服务健康检查：**启动 API，检查 `/health` 和 `/api/Reports/Test`。
3. **真实基础 PDF Smoke：**在私有环境设置 `REPORTPLATFORM_TEST_SQLSERVER=1`、`REPORTPLATFORM_TEST_FASTREPORT=1`、受批准连接与请求夹具，运行 `LegacySqlServerIntegrationTests.Real_xmtm_frx_prepares_and_exports_pdf_with_free_fastreport_runtime`。
4. **兼容接口回归：**以真实旧 JSON 调用 `/api/Reports/GetReportByJson`，核对 `200`、`application/pdf`、`%PDF-`、页数和业务可读性。
5. **打印路由 DryRun：**启动已配置 Agent，确认站点在线、逻辑角色绑定正确、PDF 制品下载和队列状态正确。
6. **物理打印验收：**在 A4 与条码设备上分别验证尺寸、方向、份数、双面、中文字体、条码识别和重试行为。
7. **安全验收：**限制数据库权限、API 网络范围、内部诊断路由、制品下载、日志访问和 `.runtime` 目录访问。

## 8. 测试与故障排查

| 现象 | 首先检查 | 建议动作 |
|---|---|---|
| `/health` 失败 | 进程、端口、反向代理 | 查看宿主日志与端口监听；先恢复健康端点。 |
| 兼容接口返回 500 | `ReportEngine`、`ReportDatabase`、`LegacyReportSchema` | 确认 `DefinitionSource=LegacySqlServer`、`Renderer=FastReportOpenSource`，并核对确认映射。 |
| 找不到 `xt_bbdy` | 配置未加载或仍使用默认映射 | 显式设置本指南的 `LegacyReportSchema__*` 环境变量；不要猜测其他表。 |
| 非 PDF 响应 | FastReport、FRX、`Master` 数据集 | 记录 HTTP 状态和错误归因；用私有 Smoke 重跑，不保存 FRX/SQL/患者数据到仓库。 |
| Agent 不在线 | `ServerUrl`、网络、站点 ID | 验证 SignalR 路径 `/hubs/print-agent`、工作站时间和防火墙。 |
| 动作无法路由 | `PrinterBindings`、驱动、工作站上报打印机 | 使用 `/api/print/agents` 检查绑定的物理打印机是否真的安装。 |
| 任务下载失败 | `/api/print/artifacts/{id}` 网络或访问限制 | 检查 API 的受控访问、Agent 服务地址和本机工作目录权限。 |
| 打印失败或重复 | Command 后端参数、队列、幂等键 | 先回退 `DryRun`；同一物理打印机应串行，调用方为可重试操作提供稳定 `idempotencyKey`。 |

## 9. 回退原则

应用发布应保留上一个可运行的发布目录和配置快照（机密仍由密钥管理保存）。若新版本出现报表、打印或稳定性问题：先停止流量或将反向代理切回上一个健康版本，再还原上一版 API 与 Agent 发布目录；不要回写、修改或删除遗留数据库报表定义来“修复”新服务。缓存问题可在确认报表 ID 后调用内部缓存失效路由；数据定义仍以遗留数据库为准。

FastReport 免费运行时的真实 `xmtm` 基础 PDF Smoke 已通过，但水印、旧/新 PDF 视觉等价和物理打印仍需按本指南现场验收。详细证据见 [FASTREPORT_SMOKE_TEST_STATUS.md](FASTREPORT_SMOKE_TEST_STATUS.md)、[REAL_FRX_DATA_CONTRACT.md](REAL_FRX_DATA_CONTRACT.md) 与 [LEGACY_DATABASE_CONTRACT.md](LEGACY_DATABASE_CONTRACT.md)。
