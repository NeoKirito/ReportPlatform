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

报表服务已支持在根目录 `config.ini` 中灵活自定义水印：
1. **开关控制**：`WatermarkEnabled=false` 可彻底关闭水印；设为 `true` 开启。
2. **动态取字段（自适应不同报表）**：`WatermarkField=xm,hzxm,b_name,name`，系统会自动按顺序在报表数据中寻找匹配的列（例如自动取患者姓名），完美解决不同报表列名不一致问题。
3. **固定文字与模板**：可配置 `WatermarkText=仅供预览` 或 `WatermarkTemplate={Field} - {HospitalName}`。未配置时默认安全回退到维护库 `dbo.qx_hospital.jgmc` 机构名称。
4. **排除与条件隐藏**：`WatermarkExcludeReports=xmtm,tjdj`（条码与指引单默认排除不加水印）；支持通过 `WatermarkConditionField=sh_flag` 配合 `WatermarkHideWhenValue=1` 实现已审核报告自动隐藏水印。
5. **视觉微调**：支持在 `config.ini` 中调整透明度 `WatermarkOpacity`、倾斜角度 `WatermarkAngle` 与字号 `WatermarkFontSize`。

## 旧接口

旧系统不需要改接口，继续以 `POST` 调用 `http://服务IP:5080/api/Reports/GetReportByJson`。请求保留原有的 `querytype=djwh` 与 `bbid` 字段，响应为 `application/pdf`。

## 重要提醒

发布包不包含数据库连接字符串、机构名称、患者资料、报表 FRX 或 SQL。请勿把现场填写后的 `appsettings.Production.json` 上传至 Git、聊天工具或公共网盘。
