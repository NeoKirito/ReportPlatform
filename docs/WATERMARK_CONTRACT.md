# 机构名称水印契约

PEIS.ReportPlatform 在免费 FastReport Open Source 渲染路径中使用维护库 `dbo.qx_hospital.jgmc` 作为 PDF 机构名称文字水印的唯一来源。该实现不将机构名称、连接字符串、患者数据、FRX 正文或 SQL 正文写入代码、公开文档或测试证据。

| 项目 | 已确认的契约 |
|---|---|
| 数据表 | `dbo.qx_hospital` |
| 标识列 | `hospitalid`，`int`，不可为空 |
| 水印列 | `jgmc`，`varchar(100)`，可为空 |
| 现场核验 | 已按只读元数据查询确认；核验数据库中当前仅有一行且有非空机构名称 |
| 查询语句 | `SELECT TOP (1) jgmc FROM dbo.qx_hospital ORDER BY hospitalid ASC` |
| 读取权限 | 仅执行 `SELECT`；不执行写入、DDL 或任何数据修改操作 |

当 `WatermarkDatabase:ConnectionString` 已配置时，水印读取使用该独立维护库连接；未配置时，系统回退使用 `ReportDatabase:ConnectionString`。水印文本在进程内按 `WatermarkDatabase:CacheTtlSeconds` 缓存，默认 3,600 秒。维护库不可访问、表为空或机构名称为空时，系统记录告警并跳过水印，报告 PDF 仍会继续生成。

水印开关保留在 `WatermarkOptions.Enabled`。水印文字不接受调用方覆盖：启用时，渲染管线以维护库机构名称替换任何请求中的文字，避免旧接口调用方伪造机构身份。免费渲染器在 `Prepare()` 完成后逐页替换准备页面，再交由 `PDFSimpleExport` 导出，从而保证叠加内容进入实际 PDF。

## 运行时配置

以下配置只展示键名和安全占位符。现场必须将连接字符串写入本机 `appsettings.Production.json` 或受保护的环境变量，绝不能提交至 Git 仓库。

```json
{
  "ReportDatabase": {
    "ConnectionString": "<遗留报表库连接字符串>"
  },
  "WatermarkDatabase": {
    "ConnectionString": "",
    "CommandTimeoutSeconds": 30,
    "CacheTtlSeconds": 3600
  }
}
```

> 当维护库与报表库相同，请保留 `WatermarkDatabase:ConnectionString` 为空；系统会安全复用报表库连接。维护库独立时，填写只具有 `SELECT dbo.qx_hospital` 权限的独立连接字符串。

## 验证状态

真实 `xmtm` 遗留 FRX 的单页 PDF 烟雾测试已通过：维护库水印文本查询、准备页面叠加、PDF 导出和高分辨率可视化检查均完成。私有测试产物及其具体文字不进入源代码库。
