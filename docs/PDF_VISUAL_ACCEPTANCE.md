# PDF 视觉验收方法

本方法用于受控现场将旧 PEIS 输出与 ReportPlatform 输出进行**视觉和结构性比较**。它不是字节级相等测试；PDF 的元数据、对象顺序和生成时间可以不同。任何真实患者 PDF、FRX、截图或比较输出均不得提交到仓库。

> 所有现场证据必须保存在 `.runtime/evidence/` 或其他受控、非版本控制目录。该目录已被 `.gitignore` 排除。

| 项目 | 现场要求 | 通过准则 |
|---|---|---|
| 输入一致性 | 对同一已获授权的就诊/登记请求分别生成旧、新 PDF | 请求业务参数、模板版本和打印配置相同 |
| 页面结构 | 记录页数、每页尺寸、文件大小 | 页数一致；尺寸差异须有业务解释 |
| 渲染比较 | 以固定 DPI 比较对应页面的哈希与差异图 | 差异由现场验收人员逐页判定，不以单一百分比自动判定通过 |
| 条码 | 对打印后的条码实际扫码 | 扫码结果与预期业务值一致 |
| 中文与水印 | 在现场目标字体、打印机、纸张条件下比对 | 无缺字、乱码、裁切、位置偏差或不可接受的透明度差异 |

## 本地比较命令

现场工作站须预先安装 Python、PyMuPDF 和 Pillow。工具不会上传文件或联网；仅在本机读取两份 PDF 并生成 JSON/可选 PNG 差异图。

```powershell
python -m pip install pymupdf pillow
python tools/compare_pdf_visual.py `
  C:\PEIS-Evidence\legacy.pdf `
  C:\PEIS-Evidence\reportplatform.pdf `
  --output .runtime/evidence\comparison.json `
  --diff-images .runtime/evidence\diffs `
  --rendered-images .runtime/evidence\renders
```

输出 JSON 包含页数、页面 point 尺寸、文件大小、渲染图 SHA-256、逐页像素差异百分比，以及总平均差异。差异图仅辅助人工验收，不能代替实际纸张、条码和字体检查。

## 验收记录

每份现场比较记录至少应写明测试编号、操作者、时间、是否使用去标识化测试数据、旧/新 PDF 的受控保存位置、逐页结论及异常处置。若因视觉、字体、条码或纸张差异无法接受，应保留为 **FIELD_REQUIRED**，并按回滚方案恢复旧报表服务。
