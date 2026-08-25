# FastReport 兼容性矩阵

本矩阵只记录已知的代码与测试证据。状态 **CONFIRMED** 不等同于所有历史模板或物理打印均已验收；未在受控现场获得证据的项必须保留为 **UNVERIFIED** 或 **FIELD_REQUIRED**。

| 能力/报表类型 | 状态 | 已有证据 | 未覆盖边界与下一步 |
|---|---|---|---|
| `xmtm` 基础 PDF 路径 | CONFIRMED | 已记录的获批真实 SQL Server `dbo.xt_bgdy_djwh_zzj`、Base64 UTF-8 FRX、Master DataSet、FastReport OpenSource 2026.2.3 解析/准备/导出 Smoke；结果为 1 页、45,365 bytes、`%PDF-`、约 383 ms | 仅限已批准的基础路径；仍需旧/新视觉及实际纸张验收 |
| A4 导检单/报告 | PARTIAL | 逻辑路由与 PDF 渲染契约已自动测试 | 真实 A4 打印机、页边距、纸张、中文字体为 FIELD_REQUIRED |
| 条码报表 | PARTIAL | 条码逻辑角色可被路由；PrintAgent 角色绑定验证已实现 | 真实条码尺寸、对比度、扫码可读性为 FIELD_REQUIRED |
| 多页报表 | UNVERIFIED | 引擎每请求独立 Report 实例并有并发门控 | 需要实际多页 FRX 与分页视觉证据 |
| 图片密集报表 | UNVERIFIED | 图片解析与并发限制存在 | 需要脱敏或合成图片密集 fixture 与现场渲染证据 |
| 中文字体 | FIELD_REQUIRED | 代码路径支持 UTF-8 FRX/文本配置 | Windows 工作站目标字体安装及实体打印效果必须现场验收 |
| 应用层水印 | PARTIAL | 水印配置进入渲染请求；现有渲染测试覆盖开关和参数 | 不宣称与旧 PEIS 等价；需现场视觉验收 |
| 大型报告 | UNVERIFIED | 已有单页真实 smoke，不足以代表负载 | 需要合成/脱敏大数据 benchmark 和现场容量测试 |
| 旧/新 PDF 视觉等价 | FIELD_REQUIRED | 提供 `tools/compare_pdf_visual.py` 和现场方法 | 只可在受控现场对获批数据执行，禁止将 PDF 提交仓库 |

## 状态定义

| 状态 | 含义 |
|---|---|
| CONFIRMED | 有明确、可追溯的自动化或获批真实运行证据，且限定范围已说明。 |
| PARTIAL | 代码和部分自动化已完成，但仍有关键模板、设备或视觉边界未验证。 |
| UNVERIFIED | 尚无足以支持声明的运行证据。 |
| FIELD_REQUIRED | 只能依赖目标医院网络、工作站、打印机、字体、纸张、扫码设备或人工视觉判断完成。 |
