# LegacyReportDatabase synthetic fixture

This directory is a **synthetic fixture**, not a production schema assertion. It models only the minimum contract required to exercise a database-driven report definition path:

| Synthetic object | Intended test role | Production status |
|---|---|---|
| `xt_bbdy` | Stores report ID, FRX text, SQL text, version, and update time | UNVERIFIED |
| `xt_djwh` | Represents a possible registration/guide definition source | UNVERIFIED |
| `xt_cxdy` | Represents a possible query-definition source | UNVERIFIED |
| `xt_bgdy_djwh_zzj` | Represents a possible report-to-guide relationship | UNVERIFIED |

The field names `bbid`, `bb_frx`, `bb_sql`, `djid`, `djsql`, and `dj_frx` are used only as test placeholders based on names supplied during development. Before enabling `ReportEngine:DefinitionSource=LegacySqlServer`, replace the mapping with evidence from a read-only production schema and sanitized legacy report samples.

The fixture's `GUIDE_A4` definition is a minimal contract sample. It is not a real FRX, not a real production SQL statement, and must never be treated as evidence of FastReport compatibility or production performance.
