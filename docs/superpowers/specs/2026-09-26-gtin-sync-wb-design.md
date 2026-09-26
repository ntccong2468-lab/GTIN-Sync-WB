# GTIN Sync WB design, 2026-09-26

Standalone native Windows desktop. WinForms on self-contained .NET 8, Inno Setup desktop shortcut. Sellers own their separate WB Content and NK keys. DPAPI CurrentUser protects each key. JSON config and JSONL history live under LocalAppData; no service.

Read NK owned published cards by dated windows and paginated /v4/product-list; split windows that exceed 10,000, fetch details in groups of 25 through /v3/feed-product. Read WB Content cards with cursor pagination. Convert API objects into GTIN and listing records. Require exact model/color/size, allow explicit product rules, mark incomplete, inaccessible, unpublished, ambiguous and cross-size barcode conflicts. The preview and per-store counts precede every write.

At confirmation, reread each card and abort on drift. Build an overwrite payload with supported card fields and every size, appending only selected GTINs. Rate limit writes, retry 429, inspect errors and poll the card for GTIN at the intended chrtID. Preserve errors in local history and export CSV. Demo data never invokes a write.

Risk: WB characteristic requirements and NK attribute labels differ by product category. Exact matching intentionally blocks cards without recognized identifiers. Live credentials and a Windows installer smoke test are separate release gates.
