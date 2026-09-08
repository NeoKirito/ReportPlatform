# 前端桌面 PDF 预览接入文档

## 概述

前端正常预览时**不需要传 stationId**。Java 后端从 HttpServletRequest 获取浏览器 IP，ReportPlatform 自动匹配本机 Agent 并打开 PDF。

## 核心流程

```
前端 → Java (desktopReportDelivery, action=Preview)
     → Java 获取浏览器 IP → 写入 clientAddress
     → Java 生成 PDF (PdfupdateUtil.exportPdf)
     → Java 上传 PDF 到 ReportPlatform
     → ReportPlatform 按 clientAddress 自动找到 Agent
     → Agent 下载 PDF → 调用默认 PDF 阅读器打开
     → 前端轮询 Java jobId → 显示 Delivered/Failed
```

## 请求示例

### 1. 调用 Java 发起投递（不传 stationId）

```javascript
// 前端调用 Java 接口
const response = await fetch('/TJ/exportTemplate/desktopReportDelivery', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    action: 'Preview',           // Preview | Print | PreviewAndPrint
    // stationId: 不传，Java 自动从 request 获取浏览器 IP
  })
});
const { jobId } = await response.json();
```

### 2. 轮询投递状态

```javascript
// 轮询 jobId 状态
async function pollJobStatus(jobId, maxAttempts = 60) {
  for (let i = 0; i < maxAttempts; i++) {
    const resp = await fetch(`/TJ/exportTemplate/desktopReportDelivery/${jobId}`);
    const state = await resp.json();
    
    if (state.status === 'Opened' || state.status === 'Completed') {
      return { success: true, status: state.status };
    }
    if (state.status === 'Failed') {
      return { success: false, error: state.message };
    }
    
    await new Promise(r => setTimeout(r, 1000));
  }
  return { success: false, error: '轮询超时' };
}
```

### 3. 状态流转

```
Queued → Downloading → Opened → Completed
                         ↓
                      Failed (如果出错)
```

- `Queued`: 任务已创建，等待 Agent 接收
- `Downloading`: Agent 正在下载 PDF
- `Opened`: Agent 已用默认 PDF 阅读器打开预览
- `Completed`: 用户在预览窗口点击了打印（仅 Print/PreviewAndPrint）
- `Failed`: 处理失败

## 多工作站兜底逻辑

当多个 Agent 在线且无法自动匹配时，前端需要显示工作站选择：

### 查询可用工作站

```javascript
// GET /api/report-deliveries/stations
// Java 需要转发浏览器 IP 作为 clientAddress 查询参数
const resp = await fetch(`/api/report-deliveries/stations?clientAddress=${browserIp}`);
const { clientAddress, autoMatchedStationId, stations } = await resp.json();
```

### 响应格式

```json
{
  "clientAddress": "192.168.0.100",
  "autoMatchedStationId": "PC-20210421ZVBD",
  "stations": [
    {
      "stationId": "PC-20210421ZVBD",
      "displayName": "PC-20210421ZVBD",
      "isCurrentClient": true
    },
    {
      "stationId": "PC-20210422ABCD",
      "displayName": "PC-20210422ABCD",
      "isCurrentClient": false
    }
  ]
}
```

### 前端选择逻辑

```javascript
function handleStationSelection(stationsData) {
  const { autoMatchedStationId, stations } = stationsData;
  
  // 优先使用自动匹配
  if (autoMatchedStationId) {
    return autoMatchedStationId;
  }
  
  // 只有一个在线 Agent，自动选择
  if (stations.length === 1) {
    return stations[0].stationId;
  }
  
  // 多个 Agent 且无法匹配 → 显示选择界面
  showStationPicker(stations);
  return null;
}

function showStationPicker(stations) {
  // 弹出模态框让用户选择工作站
  // 选中后调用投递接口时传入 stationId
}
```

### 手动指定 stationId 的投递（兜底）

```javascript
// 只在多工作站无法自动匹配时使用
const formData = new FormData();
formData.append('file', pdfBlob, 'report.pdf');
formData.append('action', 'Preview');
formData.append('stationId', selectedStationId);  // 手动指定

await fetch('/api/report-deliveries', {
  method: 'POST',
  body: formData
});
```

## 错误处理

| HTTP 状态 | 含义 | 前端处理 |
|-----------|------|----------|
| 400 | 请求参数错误 | 检查必填字段 |
| 401 | 上传 Token 无效 | 检查安全配置 |
| 409 | 工作站离线或冲突 | 提示用户检查 Agent |
| 202 | 投递成功，开始轮询 | 进入轮询流程 |

## 部署要求

- **ReportPlatform 服务器**: 只需配置端口和数据库
- **Agent 工作站**: 只需配置 `ServerUrl=http://服务器IP:端口`
- **Java 后端**: 从 `HttpServletRequest.getRemoteAddr()` 获取浏览器 IP
- **前端**: 正常调用 Java 接口即可，不需要知道 Agent 名称

## 按 djid 记住本机打印机

Java 会把 `report.templateid` 作为 `djid` 随最终 PDF 一起投递。物理打印机名称只保存在工作站：

1. 某个 `djid` 第一次打印时，Agent 预览窗口显示本机打印机列表。
2. 用户选择并打印后，Agent 将 `djid → Windows 打印机名称` 写入
   `%ProgramData%\PEIS\PrintAgent\printer-selections.json`。
3. 后续相同 `djid` 自动选中该打印机，不需要前端或 Java 记住打印机名称。
4. 打印机被卸载后，失效记录会自动移除；下次交互打印重新选择。
5. 运行 Agent 包中的 `清空打印机记录.cmd` 可清空全部选择记录。

Agent 的 `agent.ini` 支持：

```ini
# false=显示预览并允许选择打印机；true=直接静默打印
SilentPrint=false

# 静默模式首次遇到新 djid 时使用；留空则使用 Windows 默认打印机
DefaultPrinter=

# 生产静默打印需要配置医院批准的 PDF 命令行打印程序
PrintBackend=Command
PrintExecutable=C:\Path\To\ApprovedPdfPrinter.exe
PrintArgumentsTemplate={file} {printer} {copies} {duplex}
```

`SilentPrint=true` 时不会打开 PDF 预览。已有 `djid` 使用记住的打印机；新 `djid` 使用
`DefaultPrinter`，留空则使用 Windows 默认打印机，并自动保存该选择。
