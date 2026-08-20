# FRX 驱动的 DOCX 转换契约

## 目标

PDF 与 DOCX 的**唯一版式来源**均为维护库中的 FastReport `FRX`。DOCX 转换器不维护平行的 Word 模板、报表坐标表或业务字段映射。维护人员修改报表定义中的 FRX 后，下一次 DOCX 导出会读取同一份模板内容并重新编译页面、元素坐标、字体、字段表达式与条码对象。

> 旧的 `POST /api/Reports/GetReportByJson` 继续只返回 PDF，以保持遗留 PEIS 的二进制响应兼容性。FRX→DOCX 是独立的渲染能力，不会改变 PDF 或静默打印路径。

## 转换流程

| 阶段 | 输入 | 输出 | 说明 |
|---|---|---|---|
| 报表定义解析 | `ReportDefinition` | `ReportTemplate` | 沿用现有维护库定义、版本与缓存边界。 |
| FRX 编译 | XML 或 Base64 UTF-8 XML | `DocxTemplateDefinition` | 读取 `ReportPage` 的纸张尺寸、`TextObject`、`BarcodeObject` 及其坐标。 |
| 数据绑定 | `ReportDataSet` | 元素文本/条码值 | 将 FRX 的 `[Master.column]` 转为通用的 `{{Master.column}}` 绑定表达式。 |
| Word 生成 | 通用模板定义与数据集 | 可编辑 `.docx` | 文字元素写入可编辑的 Word 文本框；Code 128 由当前数据重新生成图片。 |

## 当前支持范围

| FRX 对象或属性 | DOCX 处理 | 说明 |
|---|---|---|
| `ReportPage` 的 `PaperWidth`、`PaperHeight`、页边距 | 支持 | 生成相同物理尺寸的 Word 页面。 |
| `TextObject` 的坐标、尺寸、文本、字体、粗体、水平对齐 | 支持 | 文本保留为 Word 中可编辑的内容。 |
| `[数据源.字段]` 表达式 | 支持 | 使用现有 `ReportDataSet` 的同名表和字段解析。 |
| `BarcodeObject` 的 `Code128` | 支持 | 条码依据当前字段值生成，条码图片本身不可按文字修改。 |
| 多页、`DataBand` 重复、表格、线条、富文本、图片、图表、脚本、非 Code128 条码 | 暂不完整支持 | 转换器会在编译摘要中记录未支持对象；不得静默伪造样式一致性。 |
| 机构水印 | PDF 已支持 | DOCX 背景水印需使用不挤压固定坐标内容的浮动背景形状后再启用。 |

## 安全约束

FRX 正文、SQL 正文、连接字符串、机构名称和患者数据均不得进入版本库或公开文档。测试使用只读数据库连接；FRX 编译只在请求内存或被忽略的私有运行目录中进行。
