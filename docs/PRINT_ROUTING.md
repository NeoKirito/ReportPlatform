# PEIS Print Routing

## Primary API

```http
POST /api/print/actions
Content-Type: application/json
```

Example:

```json
{
  "actionCode": "REGISTRATION_PRINT",
  "stationId": "REG-01",
  "parameters": {
    "tjh": "TJ202608140001"
  },
  "jobName": "登记打印"
}
```

No physical printer is supplied by the B/S page.

## Scenario configuration

Current development example in `PEIS.Report.Api/appsettings.json`:

```text
REGISTRATION_PRINT
  ├─ guide-sheet -> GUIDE_A4 -> A4_GUIDE
  └─ barcode     -> REG_BARCODE -> BARCODE
```

The first production iteration may keep these definitions in configuration. The recommended final implementation is a small central table/admin module so sites can add or alter actions without publishing the service.

Suggested tables:

```text
print_scenario
- scenario_code
- scenario_name
- enabled

print_scenario_item
- scenario_code
- item_key
- report_id
- printer_role
- profile
- copies
- duplex
- sort_order
```

Physical Windows printer names should not be stored in these central scenario rows.

## Agent binding

Each Windows workstation has a stable station id and role bindings:

```json
{
  "StationId": "REG-01",
  "PrinterBindings": {
    "A4_GUIDE": "HP LaserJet Pro M404",
    "BARCODE": "TSC TE244"
  }
}
```

Common roles can later include:

```text
A4_GUIDE
A4_REPORT
BARCODE
RECEIPT
WRISTBAND
A5_FORM
```

## Failure behavior

Before rendering, the server validates:

- station is online,
- required role has a binding,
- bound Windows printer is currently installed/reported by the agent.

If any required output cannot be routed, the action fails before partially printing by default.

A later option can add scenario-level policies such as `AllOrNothing` vs `BestEffort`, but `AllOrNothing` is the safer default for registration printing.
