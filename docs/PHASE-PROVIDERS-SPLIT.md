# Plan - Providers Tab Split (Cache Stats)

Status: DRAFT, awaiting approval. No code until Data split Step 1 and Step 4 are complete (this tab depends on DataOrigin being on the envelope).

## Goal

Split the Providers tab into two subtabs mirroring the Data tab:
- Raw Ticks - the "MT5 Parsed" group only
- Timeframes - every other source group

The tab stays read-only diagnostics. No queue checkbox, no fetch form. Purpose is cache inspection.

## Useful stats to add (beyond current)

Current per-group stats: DatasetCount, Volume (Yes/No/Mixed), Start, End, AverageSpan, Timeframes, plus per-row Dataset/Volume/Start/End/Span/Timeframe.

Additions worth adding while we are in the file:
- Per-group total bars (sum across datasets). Cheap from existing CacheMetadata? No - requires counting bars; do it lazily per group when expanded.
- Per-group latest FetchedUtc. Requires adding FetchedUtc to CacheMetadata read (it is in the envelope but not surfaced today).
- Per-group average bars per file. Derived from total bars / dataset count.
- Row-level Origin column so the user can see RawTick vs TimeframeFetch at a glance (redundant with subtab, but useful during transition).

Not adding: any network probe, any file rewrite, any interactive control.

## Ordered steps

Step A - Wait for DataOrigin on envelope (blocked by Data split Step 1)
- Depends on CacheMetadata exposing Origin. If CacheMetadata does not carry Origin today, extend ParseCacheMetadata to read the new field (with Legacy fallback).
- Extend CacheMetadata with FetchedUtc and BarsCount if needed for new stats.

Step B - Split the tab strip
- Wrap existing ProvidersView content in TabControl with two TabItems
- Raw Ticks: filter Groups to Source == "MT5 Parsed"
- Timeframes: filter Groups to Source != "MT5 Parsed"
- Both subtabs share one ProvidersViewModel with an OriginFilter property

Step C - Add the new stats
- Per-group total bars, per-group FetchedUtc, average bars per file
- Add Origin column to ProviderDatasetRow
- Update column definitions in ProvidersView.axaml (both header grid and dataset grid must stay aligned; current columns are 220,220,100,110,110,110,*)

Step D - Tab rename (TODO, not now)
- Rename "Providers" to "Cache" once both subtabs render correctly
- Rationale: half the tab will show MT5 Parsed data that did not come from any provider
- Deferred per user: adjust after build, tracked here so it is not forgotten

## Risks

- CacheMetadata does not currently expose Origin or FetchedUtc; extending it touches CacheManager.ParseCacheMetadata which is shared with Data and DataManager refresh paths. Verify no downstream caller assumes a fixed CacheMetadata shape.
- Column alignment in ProvidersView.axaml uses fixed widths in two places (header grid + dataset grid). Adding columns means updating both or they drift.
- Providers tab currently fires RefreshAsync in the constructor; adding TabControl should not change that but verify refresh still populates both subtabs.

## Out of scope

- Rename to Cache (Step D, deferred until built)
- Any interaction beyond expand/collapse and Refresh
- Per-file operations (delete, refetch)