# PEIS.ReportPlatform

体检信息系统报表平台（.NET 10 + FastReport）

## 项目简介

本项目是体检信息系统（PEIS）的报表服务，替代原有的 IIS FastReport 报表服务。

### 核心功能

- **PDF报表生成**：支持54种报表类型的动态生成
- **静默打印**：支持多工作站自动打印到指定打印机
- **兼容旧系统**：保持与原有Java体检系统的API接口兼容

### 技术架构

```
Java体检系统 → POST /BaseInfo/Report/GetReportByJson
  → 解析JSON参数
  → 加载FRX模板（从数据库）
  → 执行SQL获取数据
  → FastReport渲染生成PDF
  → 返回PDF流
```

## 项目结构

| 项目 | 说明 |
|------|------|
| PEIS.Report.Api | Web API 入口，兼容旧接口 |
| PEIS.Report.Engine | 报表渲染引擎核心 |
| PEIS.Report.FastReport.OpenSource | FastReport集成层 |
| PEIS.Report.Infrastructure.SqlServer | SQL Server数据库层 |
| PEIS.PrintAgent | 工作站打印Agent |
| PEIS.Report.Contracts | 共享合约 |

## 快速部署

### 1. API服务端

```powershell
# 生成独立部署包
.\scripts\New-PortableReportPackage.ps1

# 解压后编辑 config.ini 填入数据库连接信息
# 双击 启动服务.cmd
```

### 2. 工作站Agent

```powershell
# 生成Agent部署包
.\scripts\New-PortableAgentPackage.ps1

# 解压后编辑 agent.ini
# 双击 启动服务.cmd
```

## 配置说明

### API配置 (appsettings.json)

```json
{
  "Urls": "http://0.0.0.0:82",
  "ReportEngine": {
    "Renderer": "FastReportOpenSource",      // 必须用OpenSource版本
    "DefinitionSource": "LegacySqlServer"    // 从数据库加载模板
  },
  "ReportDatabase": {
    "Provider": "SqlServer",
    "ConnectionString": "Server=...;Database=...;User ID=...;Password=...;"
  }
}
```

### 数据库表

- `dbo.xt_bgdy_djwh_zzj` - 报表定义表（54种报表）
  - `djid` - 报表ID
  - `djmc` - 报表名称
  - `dj_frx` - FRX模板（Base64编码）
  - `djsql` - SQL查询语句

## Java系统对接

Java体检系统调用示例：

```java
String url = "http://YOUR_SERVER:82/BaseInfo/Report/GetReportByJson";
// POST JSON body 包含 djh 报表ID 等参数
```

### 常用报表参数

| 参数 | 说明 |
|------|------|
| djh | 报表ID（必填） |
| grtjgcjjgid | 个人体检结果ID |
| dwtjgcjjgid | 单位体检结果ID |
| sfxmddid | 收费项目ID |
| tjjfjlid | 体检结论ID |
| tjjsdjid | 体检检速ID |
| tjfyrzid | 体检预约ID |
| kpid | 考评ID |
| qjbj | 区间标识 |
| czymc | 操作员名称 |

## 开发说明

### 运行测试

```bash
dotnet test
```

### 构建发布

```bash
dotnet publish src/PEIS.Report.Api -c Release -r win-x64 --self-contained
```

## 注意事项

1. **数据库连接**：必须配置正确的SQL Server连接字符串
2. **渲染器选择**：必须使用 `FastReportOpenSource`，不要用 `FastReport`（会导致Stub错误）
3. **定义来源**：必须使用 `LegacySqlServer`，不要用 `Deterministic`（会跳过数据库）
4. **端口冲突**：默认端口82，如被占用需修改

## 相关文档

- [部署指南](docs/USAGE_AND_DEPLOYMENT_GUIDE.md)
- [架构说明](docs/ARCHITECTURE.md)
- [数据库契约](docs/LEGACY_DATABASE_CONTRACT.md)
