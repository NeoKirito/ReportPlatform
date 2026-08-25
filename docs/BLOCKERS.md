# Resolved Blockers and Current Field Gates

本文件保留历史阻塞项，并以截至本轮生产就绪审计的实际状态替代过期描述。**代码存在、Mock 通过或 DryRun 通过均不构成实体打印或全部历史模板的通过证据。**

## 已解决的开发/集成阻塞项

| 历史阻塞项 | 当前状态 | 证据范围 |
|---|---|---|
| .NET SDK 10.0.100 | RESOLVED | 使用 SDK 10.0.111 完成隔离副本 restore、Release build 和自动测试；`global.json` 所需功能带仍可满足。 |
| FastReport 运行时 | RESOLVED（限定范围） | `FastReport.OpenSource 2026.2.3` 与 PDF Simple Export 已进入项目；获批真实 `xmtm` 基础 PDF Smoke 已记录。 |
| 只读 legacy SQL Server 定义/数据映射 | RESOLVED（限定范围） | 已记录 `dbo.xt_bgdy_djwh_zzj`、Base64 UTF-8 FRX、Master DataSet 与 `xmtm` 的获批真实读取路径。 |
| 受管多工作站 Agent 基础能力 | RESOLVED（代码层） | 稳定 GUID AgentId、站点冲突拒绝、心跳超时、自动重连/重新注册、逻辑打印机角色及持久化任务状态均已实现并有自动测试。 |
| 打印任务进程内存丢失 | RESOLVED（代码层） | SQLite 持久化 Job/Target/Idempotency 记录；重启恢复、状态机和回放测试已加入。 |
| 无制品生命周期 | RESOLVED（代码层） | 保留期限、周期清理、容量上限、活动任务保护与异常隔离实现并已测试。 |

## 当前现场门禁

| 现场门禁 | 原因 | 所需验收证据 |
|---|---|---|
| 真实 A4 与条码实体打印 | 依赖真实 Windows 驱动、纸张、打印机和权限 | 受控现场清单 F-11、F-12 记录。 |
| 条码扫码、中文字体与水印视觉 | 依赖目标字体、标签介质、扫码设备和人工判断 | 受控现场清单 F-05、F-06、F-12。 |
| 旧/新 PDF 视觉等价 | 真实患者 PDF 不可进入仓库；PDF 不是字节稳定格式 | 按 `PDF_VISUAL_ACCEPTANCE.md` 的本地比较和人工结论。 |
| 大型真实体检报告负载 | 不得猜测生产数据、连接或容量特征 | 已批准只读环境下的脱敏/合成负载与容量记录。 |
| 医院网络、TLS、工作站权限 | 只能在目标网络及身份策略下验证 | API/Agent HTTPS、计划任务、服务账户及回滚演练记录。 |

> 当前剩余门禁均是 **FIELD_REQUIRED**。任何现场失败都应触发 `FIELD_ACCEPTANCE_CHECKLIST.md` 的停止条件及服务级回滚，而不是通过降低安全设置绕过。
