# PEIS.ReportPlatform 现场发布包

这是 Windows x64 自包含发布包；报表服务器不需要另行安装 .NET SDK 或 .NET Runtime。请先解压到固定目录，再按下表完成配置与启动。

| 步骤 | 操作 |
|---|---|
| 1 | 打开 `01-报表服务API\appsettings.Production.json`，只填写 `ReportDatabase.ConnectionString`。维护库与报表库不同时，再填写 `WatermarkDatabase.ConnectionString`；相同时保持为空。连接账号仅授予所需的 `SELECT` 权限。 |
| 2 | 双击 `01-报表服务API\启动报表服务.cmd`。浏览器访问 `http://服务IP:5080/health`；显示 `ok` 即服务已启动。 |
| 3 | 在每台打印工作站打开 `02-静默打印代理\appsettings.Production.json`，填写服务地址和实际 Windows 打印机名称。首次安装请保留 `PrintBackend.Mode=DryRun`，确认 PDF 流程后再由现场人员配置真实打印程序。 |
| 4 | 双击 `02-静默打印代理\启动静默打印代理.cmd`。 |

## 水印

服务生成 PDF 时，从维护库 `dbo.qx_hospital.jgmc` 只读获取机构名称，并以斜向浅灰文字显示为水印。文字在内存中默认缓存一小时。维护库不可用或名称为空时，PDF 会继续生成，只是不加水印。

## 旧接口

旧系统不需要改接口，继续以 `POST` 调用 `http://服务IP:5080/api/Reports/GetReportByJson`。请求保留原有的 `querytype=djwh` 与 `bbid` 字段，响应为 `application/pdf`。

## 重要提醒

发布包不包含数据库连接字符串、机构名称、患者资料、报表 FRX 或 SQL。请勿把现场填写后的 `appsettings.Production.json` 上传至 Git、聊天工具或公共网盘。
