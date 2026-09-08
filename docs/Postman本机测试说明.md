# Postman 本机测试（2026-09-04）

## 当前已配置

- ReportApi：http://192.168.0.88:82，数据库连接保持原配置。
- Java：http://192.168.0.88:8095/TJ。已修改 src/main/resources/application.yml 及 target/classes/application.yml，开启 desktop-delivery，并将生成和投递地址指向 .88:82；运行中的 Java 已加载。
- Agent：当前工作站 PC-20210421ZVBD 在线；ServerUrl=http://192.168.0.88:82，SilentPrint=false，PrintBackend=DryRun。
- 未修改 Java 业务代码或数据库模板；已有生成接口也会使用调整后的 fastReportUrl。
- 实际包目录：D:\gongzuo\A_python\PEIS.ReportPlatform\artifacts。没有修改 ZIP；本次配置针对正在使用的解压目录和本机 Java 项目。

## 测试

在 Postman 中导入同目录的 PEIS-Local-DesktopDelivery.postman_collection.json，按 1～5 顺序执行。第 4 步重复查询至 Delivered，集合会自动保存 jobId 和 remoteJobId。第 5 步应返回 Opened。

第 6 步是可选打印请求。当前 DryRun 不会出纸；如果之后改成 Command，就可能真实打印。不要不加检查地运行整套集合。

手动请求：POST http://192.168.0.88:8095/TJ/exportTemplate/desktopReportDelivery

Body 选择 raw / JSON：

```json
{
  "action": "Preview",
  "report": {
    "grtjgcjjgidArr": ["C48E053041174B41B32C9CE9EB5ECE7F"],
    "templateid": "724071198644850688",
    "filename": "123456"
  }
}
```

注意：以前说明中 Java 请求填写 templateid=jktjbbd 的示例不正确。当前 Java 按短字符串查询业务报告类型、按长字符串查询模板 bgurl。这里使用查询到的 bgurl=724071198644850688，对应“个检报告”，其 FastReport bgid 才是 jktjbbd。直接调用 ReportApi 旧接口时 bbid 仍为 jktjbbd。

Java 会把请求的 templateid 作为 djid 传给 Agent，所以本请求的打印机记忆键为 724071198644850688，不是 jktjbbd。前端沿用原 exportPdf 的业务模板参数即可。

## 本次验证结果

- Java 工作站列表 code=0，自动匹配 PC-20210421ZVBD。
- 实际预览任务 f68734c9-03d7-4fe7-ade5-183e35560a3a：Delivered。
- ReportApi 任务 2384675f-9502-4895-8f35-8ed74ca9e094：Opened。
- 提交到 Agent 报告打开约 2.4 秒（单次端到端耗时，不等同于纯 PDF 生成性能）。
- 未测试真实打印，未触发出纸。

任务状态可能在服务重启后丢失。请使用新提交任务返回的 ID 查询。
