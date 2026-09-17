# PEIS 体检信息系统全景实战核心手册与架构蓝图
> **文档定位**：用于体检系统全栈培训（开发、实施、售后）、新项目重构与实施运维落地的核心蒸馏资产。

---

## 目录
1. [体检核心业务生命周期与主干数据模型](#一-体检核心业务生命周期与主干数据模型)
2. [总检智能决策引擎与参数控制体系](#二-总检智能决策引擎与参数控制体系)
3. [报表引擎与存储机制（FastReport + 磁盘秒开）](#三-报表引擎与存储机制fastreport--磁盘秒开)
4. [工作站集成打印系统（PrintAgent 智能分流）](#四-工作站集成打印系统printagent-智能分流)
5. [历史 HTML 转 PDF 机制剖析与替代演进](#五-历史-html-转-pdf-机制剖析与替代演进)
6. [医院外联生态（LIS / PACS / HIS / 自助终端）](#六-医院外联生态lis--pacs--his--自助终端)
7. [实施与售后高频必备 SQL 工具箱（排错速查）](#七-实施与售后高频必备-sql-工具箱排错速查)
8. [新项目启动架构与代码资产结构](#八-新项目启动架构与代码资产结构)

---

## 一、 体检核心业务生命周期与主干数据模型

### 1. 业务五大阶段与状态流转

```
[ 1. 预约登记 ] (tjzt=0 未检)
      │ 刷身份证建档 -> 选套餐加减项 -> 生成体检号 -> 打导检单/条码
      ▼
[ 2. 科室分检 ] (jczt=8 科室完成)
      │ 临床小结录入 -> LIS检验回传 -> PACS影像图谱 -> 异常标记(gxsfyc)
      ▼
[ 3. 总检审核 ] (tjzt=8 总检完成)
      │ 异常诊断汇总 -> 专家建议匹配 -> 主检医生签名 -> 封单归档
      ▼
[ 4. 报告出具 ] (bgsczt=1 已生成)
      │ FastReport微服务渲染 -> 磁盘落地(E:\resourceFile\tjPdf) -> PrintAgent静默出纸
      ▼
[ 5. 团检交付 ] 
      │ 单位对账结算 -> 团体综合阳性率报告 -> 危机值随访追踪
```

### 2. 主干数据表关联链路 (Core ER)

| 核心表名 | 中文说明 | 关键字段 | 关联关系 |
| :--- | :--- | :--- | :--- |
| **`pe_tjryxx`** | 人员基本档案表 | `tjryid`, `xm`, `xb`, `sfzh`, `nl`, `dh` | 人员唯一主体 |
| **`pe_tjdj`** | 体检登记单 | `tjdjid`, `tjh`, `tjrq`, `tjdwid`, `djdzt` | 登记业务流水 |
| **`pe_grtjgcjjg`** | 个人体检结果主表 | `grtjgcjjgid`, `tjryid`, `tjdjid`, `tjzt`, `bgsczt`, `bgurl` | 体检全过程控制中枢 |
| **`pe_tjxmxq`** | 个人收费项目大单 | `tjxqid`, `grtjgcjjgid`, `sfxmddid`, `xmmc`, `jczt`, `deleted` | 科室级体检大项 |
| **`pe_tjxmxq_zdxj`** | 分检诊断小结表 | `tjxmzdid`, `tjxqid`, `tjxmxj`, `tjjy`, `gxsfyc` | 具体诊断与异常标记 |
| **`pe_tjzjshjl`** | 总检审核结论表 | `tjzjshjlid`, `grtjgcjjgid`, `zjysid`, `zjsj`, `tjjy`, `tjxmxj` | 最终医学鉴定结论 |
| **`pe_tj_bgdyjl`** | 报告打印审计表 | `grtjgcjjgid`, `czybm`, `dysj`, `dycs` | 打印合规与次数加锁 |

---

## 二、 总检智能决策引擎与参数控制体系

### 1. 核心业务规则配置表：`PE_XTCS_CSSZ`

| 参数名称 (`CSFL`) | 典型取值 | 业务逻辑与影响 |
| :--- | :--- | :--- |
| **总检综述是否只显示异常** | `是` / `否` | • `是`：只提取 `pe_tjxmxq_zdxj.gxsfyc = 1` 的异常诊断，自动过滤“未见异常”；<br>• `否`：所有科室小结无论正常与否全部展示。 |
| **分检无建议是否填充综述到建议** | `是` / `否` | • `是`：分检科室仅录入诊断但建议为空时，自动将诊断小结复制到建议中；<br>• `否`：建议项留空。 |
| **总检建议显示项目名** | `是` / `否` | • `是`：输出形如 `【内科检查】：窦性心动过速...`；<br>• `否`：直接输出 `窦性心动过速...`。 |
| **总检合并异常** | `合并` / `不合并` | • `合并`：同科室/同类型建议合并展示；<br>• `不合并`：每个异常诊断独立输出一条建议。 |

---

## 三、 报表引擎与存储机制（FastReport + 磁盘秒开）

### 1. 报表微服务架构 (`PEIS.ReportPlatform`)
* **技术选型**：.NET 10 + FastReport OpenSource（独立便携发布，脱离 IIS）。
* **接口规范**：`POST /BaseInfo/Report/GetReportByJson`（与 Java 旧接口 100% 兼容）。
* **模板定义表**：`dbo.xt_bgdy_djwh_zzj`
  * `djid`：单据唯一标识（如个检 `jktjbbd`、登记单 `tjdjd`、条码 `xmtm`）；
  * `dj_frx`：FastReport FRX 模板（Base64 编码的 UTF-8 XML）；
  * `djsql`：执行取数 SQL（支持主子查询，如 `jktjbbd_1` ~ `_N` 自动装配多表 DataSet）。

### 2. 报告物理落盘与数据库映射

```
磁盘根路径 (application.yml -> file.path): E:\resourceFile\
  ├── tjPdf\yyyy-MM-dd\xxxx.pdf       --> 个人体检报告
  ├── tjfzbgpdf\yyyy-MM-dd\xxxx.pdf   --> 单位/分组汇总报告
  └── image\                          --> 头像/医生手签(qm/)/公章
```

### 3. 已有报告“秒开”机制 (`CompleteInspectionService.batchExportPdf`)
1. **查询路径**：查 `pe_grtjgcjjg.bgurl`；
2. **命中缓存**：若物理文件在磁盘上存在，直接返回文件流，**耗时 10ms 以内，不占用数据库与渲染算力**；
3. **失效重算**：医生修改结果重新总检时，系统调用 `decideUrl` 删除磁盘旧文件并重置 `bgsczt=0`，触发重新渲染。

---

## 四、 工作站集成打印系统（PrintAgent 智能分流）

### 1. 解决医院串机与废纸痛点（按 `djid` 记忆打印机）
* 工位本地维护单据类型到物理打印机的绑定：
  * **采血项目条码 (`xmtm`)** ➔ 走 **Zebra / 得力条码标签机**；
  * **体检登记单 (`tjdjd`)** ➔ 走 **普通 A4 黑白激光机**；
  * **体检总报告 (`jktjbbd`)** ➔ 走 **高速彩色双面机**。
* 首次选择后永久记忆，后续直接出纸，彻底杜绝手工选机错误。

### 2. 核心技术栈
* **静默打印内核**：内置 SumatraPDF 独立命令行驱动，直通 Windows Spool 打印队列，零弹窗、不抢前台焦点。
* **高清置顶预览**：内置 WebView2 硬件加速预览，支持无失真缩放与翻页。
* **智能寻址**：根据发起请求的客户端 IP 自动匹配对应在线 Agent，前端与 Java 零感知。

---

## 五、 历史 HTML 转 PDF 机制剖析与替代演进

### 1. 历史机制 (`ConvertHtmlStringToPdf.java`)
* **旧流程**：Vue 前端生成长 HTML ➔ Spire.PDF HtmlConverter（调用 `plugins/QtWebKit` 动态库）转为 PDF ➔ `importImage(white.png)` 遮盖试用版水印 ➔ `RemoveBlankPages` 剔除尾部空白页 ➔ PDFBox 合并。
* **淘汰原因**：
  1. Qt WebKit 是本地 C++ 进程，多线程并发时极易内存溢出或死锁；
  2. HTML 打印分页截断不可控，文字经常被硬生生切断；
  3. 服务器需维护复杂的 `plugins` 动态库。
* **现代方案**：全面回归 FastReport 原生矢量模板，封面/目录/警示灯/明细统一由 FRX 绘制，渲染耗时由 3~5 秒降至 200~400 毫秒。

---

## 六、 医院外联生态（LIS / PACS / HIS / 自助终端）

* **LIS 检验接口**：试管条码双向通讯，检验仪器指标数值与高低箭头自动回填 `pe_tjxmxq`。
* **PACS 影像接口**：获取 DR、CT、B超影像描述及诊断结论，动态拉取高清关键帧图谱嵌入报告。
* **HIS 计费接口**：支持体检开单推送 HIS 门诊收费/刷医保，以及弃检退费冲正。
* **自助终端与排队**：刷身份证自助签到出单，智能导检算法计算科室拥堵指数指引分流。

---

## 七、 实施与售后高频必备 SQL 工具箱（排错速查）

### 1. 人员与报告生成/打印状态全字段排查
```sql
-- 排查体检进度与状态
SELECT 
    g.grtjgcjjgid, r.xm, r.xb, r.sfzh, g.tjbh,
    g.tjzt,         -- 体检状态(8为已总检)
    g.bgsczt,       -- 报告生成状态(0未生成 1已生成)
    g.bgurl,        -- 磁盘落地路径
    d.dycs, d.dysj  -- 打印次数与最后打印时间
FROM pe_grtjgcjjg g WITH(NOLOCK)
INNER JOIN pe_tjryxx r ON g.tjryid = r.tjryid
LEFT JOIN pe_tj_bgdyjl d ON g.grtjgcjjgid = d.grtjgcjjgid
WHERE r.xm = '邓鹏宇' OR g.tjbh = '2026090001';
```

### 2. 报表模板 (FRX Base64) 与主子查询 SQL 提取
```sql
-- 提取某个报表（主表及附属子查询）
SELECT djid, djmc, xh, djsql, 
       LEN(dj_frx) AS frx_length,
       dj_frx -- Base64 编码的 FRX 模板内容
FROM xt_bgdy_djwh_zzj WITH(NOLOCK)
WHERE djid = 'jktjbbd' OR djid LIKE 'jktjbbd[_]%'
ORDER BY xh, djid;
```

### 3. 强制清空旧报告缓存（支持重新总检与重新生成）
```sql
-- 清除指定人员已生成状态，强制下次请求重新触发渲染
UPDATE pe_grtjgcjjg 
SET bgsczt = '0', bgurl = NULL 
WHERE grtjgcjjgid = 'A5C0C3C57D99488E8B06D67677263B44';

-- 清除打印次数加锁限制（允许重新打印）
UPDATE pe_tj_bgdyjl 
SET dycs = 0 
WHERE grtjgcjjgid = 'A5C0C3C57D99488E8B06D67677263B44';
```

### 4. 科室分检结果、异常标识与总检综述排查
```sql
-- 查看科室小结与异常标识
SELECT 
    a.xmmc, a.jczt, b.tjxmxj, b.tjjy, 
    b.gxsfyc -- 1为异常，0为正常
FROM pe_tjxmxq a WITH(NOLOCK)
LEFT JOIN pe_tjxmxq_zdxj b ON a.tjxqid = b.tjxqid
WHERE a.grtjgcjjgid = 'A5C0C3C57D99488E8B06D67677263B44' 
  AND a.deleted != 1;
```

---

## 八、 新项目启动架构与代码资产结构

若以此知识库另起新项目进行开发或培训，建议采用如下标准目录组织：

```
PEIS.NextGen/
├── docs/                                  --> 架构设计、培训幻灯片与业务字典说明
│   ├── PEIS_全员实战培训讲座_开发实施售后.html
│   └── PEIS_架构与实施运维全景手册.md
├── src/
│   ├── backend-java/ (HealthCheckHv3.0)    --> Java 业务中枢 (Spring Boot / MyBatis)
│   │   ├── src/main/java/com/sxyckj/tj/
│   │   │   ├── service/register/          --> 导检、发卡、导出、打印审计服务
│   │   │   ├── service/zinspection/       --> 总检决策引擎、综述建议生成
│   │   │   └── common/utils/              --> PdfupdateUtil (PDFBox 后处理)
│   │   └── src/main/resources/mapping/    --> 49 个核心 Mapper XML
│   │
│   ├── report-service/ (PEIS.ReportPlatform) --> .NET 10 高性能报表微服务
│   │   ├── PEIS.Report.Api/               --> WebAPI 路由 (/BaseInfo/Report/GetReportByJson)
│   │   ├── PEIS.Report.Engine/            --> 报表渲染核心与参数映射
│   │   └── PEIS.Report.FastReport.*/      --> FRX 模板解析与多表合并
│   │
│   └── workstation-agent/ (PrintAgent)    --> 工作站智能打印与预览客户端
│       ├── SumatraPDF/                    --> 内置静默打印命令行引擎
│       └── WebView2/                      --> 高清置顶预览组件
│
└── db/
    ├── schema/                            --> 数据库建表与索引脚本
    └── templates/                         --> 54+ 套 FRX 报表初始 SQL 导入包
```
