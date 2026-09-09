# PEIS.ReportPlatform 现场发布包

这是 Windows x64 自包含发布包；报表服务器不需要另行安装 .NET SDK 或 .NET Runtime。请先解压到固定目录，再按下表完成配置与启动。

| 步骤 | 模块 | 操作 |
|---|---|---|
| 1 | 01-ReportApi 报表服务 | 双击 `修改配置.cmd`（编辑根目录 `config.ini`），填写 `Port`（端口）与 `ConnectionString`（数据库连接）。 |
| 2 | 01-ReportApi 报表服务 | 双击 `启动服务.cmd`。服务在后台运行，浏览器访问 `http://服务IP:端口/health`，显示 `ok` 即启动成功。 |
| 3 | 02-PrintAgent 打印代理 | 在各打印工作站解压，双击 `修改配置.cmd`，填写 `ServerUrl`（报表服务地址，如 `http://192.168.0.237:82`）。首次运行建议保留 `PrintBackend=DryRun` 模拟打印。 |
| 4 | 02-PrintAgent 打印代理 | 双击 `启动服务.cmd` 启动后台代理。双击 `查看状态.cmd` 可随时检查运行与日志。 |

> 提示：各模块目录结构完全统一，外层为 `.cmd` 操作脚本与 `config.ini` 配置文件，核心程序位于 `app/`，日志位于 `logs/`。

## 水印

服务生成 PDF 时，从维护库 `dbo.qx_hospital.jgmc` 只读获取机构名称，并以斜向浅灰文字显示为水印。文字在内存中默认缓存一小时。维护库不可用或名称为空时，PDF 会继续生成，只是不加水印。

## 旧接口

旧系统不需要改接口，继续以 `POST` 调用 `http://服务IP:5080/api/Reports/GetReportByJson`。请求保留原有的 `querytype=djwh` 与 `bbid` 字段，响应为 `application/pdf`。

## 重要提醒

发布包不包含数据库连接字符串、机构名称、患者资料、报表 FRX 或 SQL。请勿把现场填写后的 `appsettings.Production.json` 上传至 Git、聊天工具或公共网盘。
