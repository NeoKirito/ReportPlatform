# ReportPlatform 本机部署与 Postman 调用说明

本说明适用于已部署在本机的 ReportPlatform API。默认地址为 `http://127.0.0.1:5080`，仅监听本机回环地址，不会向局域网暴露服务。运行时数据库连接仅保存在部署目录的 `api/appsettings.Production.json` 中，不会写入源代码仓库。

## 1. 启动、停止与日志

部署目录默认为 `D:\\PEIS.ReportPlatform.Local`，API 程序位于 `api` 子目录。若服务未在运行，可双击 `D:\\PEIS.ReportPlatform.Local\\api\\Start-ReportPlatform.cmd` 启动。需要关闭时，以 PowerShell 执行 `D:\\PEIS.ReportPlatform.Local\\api\\Stop-ReportPlatform.ps1`；该操作只停止 API 进程，不会删除部署文件、日志或本机验证文件。

| 项目 | 位置或地址 | 用途 |
|---|---|---|
| 健康检查 | `GET http://127.0.0.1:5080/health` | 确认 API 已启动。 |
| API 启动日志 | `D:\PEIS.ReportPlatform.Local\api\api.stdout.log` | 查看监听地址、启动与运行信息。 |
| API 错误日志 | `D:\PEIS.ReportPlatform.Local\api\api.stderr.log` | 查看未处理的启动或运行错误。 |
| 真实 PDF 验证证据 | `D:\PEIS.ReportPlatform.Local\verification\legacy-xmtm-smoke.pdf` | 本机冒烟验证生成的私有 PDF，不应上传到仓库或对外发送。 |
| 验证摘要 | `D:\PEIS.ReportPlatform.Local\verification\verification-summary.json` | 不含报告正文和病人字段的验证结果。 |

## 2. Postman 环境

导入附件中的环境文件后选择 **ReportPlatform - Local** 环境，默认只需要保留 `baseUrl`。每次调用真实报告时，填写当前业务数据对应的两个 ID；不要把真实报告请求或响应保存到公共 Postman Workspace。

| 变量 | 示例或填写原则 | 必填 |
|---|---|---|
| `baseUrl` | `http://127.0.0.1:5080` | 是 |
| `bbid` | `xmtm` | 是 |
| `grtjgcjjgid` | 当前受检登记结果 ID | 是 |
| `sfxmddid` | 当前收费项目明细 ID | 是 |
| `yhmc` | 当前操作用户名；可按遗留系统使用方式填写 | 建议填写 |
| `fileName` | 下载文件名，不含扩展名，例如 `report-preview` | 建议填写 |

## 3. 健康检查

在 Postman 中新建请求，选择 `GET`：

```text
{{baseUrl}}/health
```

响应应为 HTTP `200`，并包含：

```json
{
  "status": "ok",
  "service": "PEIS.Report.Api"
}
```

## 4. 遗留兼容 PDF 报告接口

这是 PEIS 既有 B/S 调用应使用的接口，保持原地址语义和 JSON 结构：

```text
POST {{baseUrl}}/api/Reports/GetReportByJson
Content-Type: application/json
Accept: application/pdf
```

在 Postman 的 **Body → raw → JSON** 中填写：

```json
{
  "pageNo": 1,
  "pageSize": 100,
  "djh": {
    "grtjgcjjgid": "{{grtjgcjjgid}}",
    "sfxmddid": "{{sfxmddid}}"
  },
  "yhmc": "{{yhmc}}",
  "bbid": "{{bbid}}",
  "fileName": "{{fileName}}",
  "querytype": "djwh"
}
```

成功时服务器返回 HTTP `200`、`Content-Type: application/pdf`，同时带有下载文件名。Postman 默认可能只在响应窗口展示二进制内容；请点击响应区右侧的 **Save Response**，选择 **Save to a file**，保存为 `.pdf` 后使用本机 PDF 阅读器打开。

> 该请求会从遗留报表库执行已维护的只读模板与数据查询。不要使用无效 ID 反复压测生产库，也不要将真实请求体、PDF 或 Postman 历史导出到公开位置。

## 5. 渲染诊断接口

以下接口用于本机排障与性能确认，不属于遗留 B/S 正常业务调用：

```text
GET {{baseUrl}}/internal/diagnostics/rendering
```

重点查看：`definitionSource` 应为 `LegacySqlServer`；执行过报告后，`recentRenders` 应出现本次渲染的 SQL、模板、准备和 PDF 导出阶段耗时。该接口只监听本机时可用于开发排障，若未来改成局域网或服务器部署，应增加访问控制后再开放。

## 6. 常见问题

| 现象 | 处理方式 |
|---|---|
| Postman 无法连接 `127.0.0.1:5080` | 先执行健康检查；确认 `api.stdout.log` 出现 `Now listening on`；若未运行，双击启动脚本。 |
| HTTP 500 或 PDF 未生成 | 查看 `api.stderr.log` 和 `api.stdout.log`；确认当前电脑可连遗留 SQL Server；确认两个业务 ID 来自同一次有效登记。 |
| 返回的 PDF 没有数据或不是预期模板 | 核对 `bbid`、`querytype`、两个 ID 的组合；遗留兼容接口会按原有模板解析规则处理。 |
| 水印未出现 | 当前部署已配置从同一维护库的 `dbo.qx_hospital.jgmc` 读取文字水印；检查请求模板是否启用水印及运行日志。 |
| 需要从另一台电脑访问 | 当前服务故意仅绑定 `127.0.0.1`。若确有局域网访问需求，应单独评估认证、Windows 防火墙和 HTTPS，再修改监听地址。 |
