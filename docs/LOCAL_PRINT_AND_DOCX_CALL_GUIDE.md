# 本机静默打印与 Word（DOCX）调用说明

本机 ReportPlatform API 地址为 `http://127.0.0.1:5080`。浏览器或 Postman 不直接控制 Windows 打印机；正确链路是 **调用方 → Report API → PrintAgent → Windows 打印后端 → 物理打印机**。这样多台工作站不会相互抢占打印机，也不会暴露打印机名称给普通 B/S 页面。

## 1. 本机已配置状态

| 项目 | 当前值 | 说明 |
|---|---|---|
| API 地址 | `http://127.0.0.1:5080` | 仅监听本机。 |
| 工作站代码 | `LOCAL-WSQ-01` | B/S 业务打印按此代码路由。 |
| A4 逻辑角色 | `A4_GUIDE` → `DASCOM DL-620Z` | 已从本机已安装打印机中绑定。 |
| 条码逻辑角色 | `BARCODE` → `DASCOM DL-620E` | 已从本机已安装打印机中绑定。 |
| PrintAgent | 已运行并已向 API 注册 | 可通过 `GET /api/print/agents` 查看。 |
| 当前打印后端 | `DryRun` | 已完成投递、下载、排队和回执验证；**不会实际出纸**。 |

> 当前采用 `DryRun` 是因为尚未指定现场认可的静默 PDF 打印程序及其参数。它会完整验证报表生成、代理下载、每打印机队列和 `Completed` 回执，但不会调用 DASCOM 驱动。配置正式后端前，任何“完成”都应理解为干运行完成而非纸张已打印。

## 2. 查看本机 PrintAgent

```text
GET http://127.0.0.1:5080/api/print/agents
```

响应中应能看到 `stationId` 为 `LOCAL-WSQ-01`、两台 DASCOM 打印机和 `printerBindings`。返回的 `agentId` 是本机自动持久化的安装身份；如果使用手工诊断打印接口，需要把它填入请求体。

## 3. 推荐的 B/S 业务打印调用

正式 B/S 页面优先调用业务动作接口，不传物理打印机名称：

```text
POST http://127.0.0.1:5080/api/print/actions
Content-Type: application/json
```

```json
{
  "actionCode": "<已在 PrintRouting:Scenarios 配置的动作编码>",
  "stationId": "LOCAL-WSQ-01",
  "parameters": {
    "pageNo": 1,
    "pageSize": 100,
    "djh": {
      "grtjgcjjgid": "<当前登记结果ID>",
      "sfxmddid": "<当前收费项目明细ID>"
    },
    "yhmc": "<操作用户>",
    "bbid": "xmtm",
    "fileName": "print-preview",
    "querytype": "djwh"
  },
  "jobName": "登记打印",
  "idempotencyKey": "<一次业务动作唯一键>"
}
```

该接口需要在 API 的 `PrintRouting:Scenarios` 中预先维护 `actionCode`，并把每份报告配置为逻辑角色（如 `A4_GUIDE`、`BARCODE`）。调用前可用下列接口查看当前可用动作：

```text
GET http://127.0.0.1:5080/api/print/actions
```

成功后返回 `jobId`。轮询：

```text
GET http://127.0.0.1:5080/api/print/jobs/{jobId}
```

状态数值对应为：`0=Queued`、`1=Downloading`、`2=Printing`、`3=Completed`、`4=Failed`。

## 4. Postman 手工诊断打印

手工接口用于安装和排障，必须先从 `/api/print/agents` 获取当前 `agentId`，并且打印机名称必须是该代理上报的 Windows 打印机名称。

```text
POST http://127.0.0.1:5080/api/print/jobs
Content-Type: application/json
```

```json
{
  "jobName": "Postman-本机打印诊断",
  "report": {
    "reportId": "xmtm",
    "parameters": {},
    "profile": "legacy",
    "watermark": {
      "enabled": true
    },
    "fileName": "postman-print",
    "legacyPayload": {
      "pageNo": 1,
      "pageSize": 100,
      "djh": {
        "grtjgcjjgid": "<当前登记结果ID>",
        "sfxmddid": "<当前收费项目明细ID>"
      },
      "yhmc": "Postman",
      "bbid": "xmtm",
      "fileName": "postman-print",
      "querytype": "djwh"
    }
  },
  "targets": [
    {
      "agentId": "<从 agents 接口复制>",
      "printerName": "DASCOM DL-620Z",
      "copies": 1,
      "duplex": false
    }
  ]
}
```

本机已用该模式完成一次 `Completed` 干运行验证。正式出纸前，需要将 `D:\PEIS.ReportPlatform.Local\print-agent\appsettings.Production.json` 中的 `PrintBackend` 从 `DryRun` 改为 `Command`，并填写医院批准的静默 PDF 打印程序路径和参数模板，例如其支持的 `{file}`、`{printer}`、`{copies}`、`{duplex}` 占位符。保存后重启 `Stop-PrintAgent.ps1` 和 `Start-PrintAgent.ps1`。请先在一张非业务测试 PDF 上验证所选打印程序与 DASCOM 驱动的纸张、标签尺寸和份数，再改为正式模式。

## 5. 导出并打开 Word

新增 Word 接口与旧 PDF 接口使用**同一份 JSON 请求体**，仅 URL 和返回类型不同：

```text
POST http://127.0.0.1:5080/api/Reports/GetReportDocxByJson
Content-Type: application/json
Accept: application/vnd.openxmlformats-officedocument.wordprocessingml.document
```

请求 Body 可直接复用 `POST /api/Reports/GetReportByJson` 的 JSON：

```json
{
  "pageNo": 1,
  "pageSize": 100,
  "djh": {
    "grtjgcjjgid": "<当前登记结果ID>",
    "sfxmddid": "<当前收费项目明细ID>"
  },
  "yhmc": "Postman",
  "bbid": "xmtm",
  "fileName": "report-word",
  "querytype": "djwh"
}
```

成功时返回 HTTP `200` 和 `.docx` 文件。Postman 中点击 **Save Response → Save to a file**，保存为 `.docx` 后双击即可由本机默认 Word/WPS 打开。Windows API 调用方可将响应写入本地 `.docx` 文件后，以 `Process.Start(..., UseShellExecute=true)` 使用默认关联程序打开。

响应头 `X-ReportPlatform-Docx-Unsupported-Objects` 是当前 FRX 中未完整转换对象数量。`0` 表示本次模板未发现已知不支持对象；它不代表 PDF 与 Word 像素级完全一致。当前 Word 导出以 FRX 为唯一来源，已支持文字、位置、字体、基础绑定和 Code128 条码；复杂表格、线条、图表、图片、脚本和多页重复等对象仍需要按模板扩展。

## 6. 本机文件与进程管理

| 项目 | 路径 |
|---|---|
| API 启动/停止 | `D:\PEIS.ReportPlatform.Local\api\Start-ReportPlatform.ps1` / `Stop-ReportPlatform.ps1` |
| PrintAgent 启动/停止 | `D:\PEIS.ReportPlatform.Local\print-agent\Start-PrintAgent.ps1` / `Stop-PrintAgent.ps1` |
| API 日志 | `D:\PEIS.ReportPlatform.Local\api\api.stdout.log`、`api.stderr.log` |
| Agent 日志 | `D:\PEIS.ReportPlatform.Local\print-agent\print-agent.stdout.log`、`print-agent.stderr.log` |
| 本机私有验证文件 | `D:\PEIS.ReportPlatform.Local\verification` |

请勿将真实病人请求体、生成的 PDF/DOCX、运行时连接配置或验证目录上传到公共仓库、公共 Postman Workspace 或聊天附件中。
