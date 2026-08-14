# Performance plan

## Baseline to capture first

For every render log these stages independently:

- DefinitionLoad
- SqlQuery
- ImageDiscovery
- ImageResolve
- FrxLoad
- RegisterData
- Prepare
- Watermark
- PdfExport
- ArtifactWrite
- Total

Also log: rows, image count/bytes, page count, PDF bytes, cache hit/miss.

## Highest-value optimizations

1. **Render each unique document once per business action**. A4 guide and barcode are different artifacts; never re-render either artifact merely because of printer routing.
2. Cache immutable FRX/SQL/parameter definitions by report id + version/hash.
3. Bounded parallel image prefetch; deduplicate identical image URLs/content keys.
4. Resize/encode images for actual report placement instead of embedding camera/original resolution everywhere.
5. Add export profiles (`screen`, `print`, `archive`) controlling image resolution/JPEG quality/font behavior.
6. Apply watermark during report rendering/export. Avoid reopening and rewriting the finished PDF unless the newest legacy behavior proves this is unavoidable.
7. Limit heavy render concurrency with a bounded gate. More simultaneous FastReport instances can reduce throughput through CPU/GC contention.
8. Use file-backed artifacts/streams for large PDFs to reduce LOH copies.

## Initial performance acceptance targets

These are engineering targets, not guarantees until the newest watermark build is profiled:

| Workload | Target |
|---|---:|
| small/simple report | < 2 s |
| normal report | < 5 s |
| 20+ page image-heavy report | < 10 s |
| business-action routing | one render per unique document; no extra render per physical printer |

The current ~50 s / 20+ page / 20+ MB case should be retained as a regression fixture.
