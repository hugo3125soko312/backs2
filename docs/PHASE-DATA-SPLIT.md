# Plan - Data Tab Split (Raw Ticks vs Timeframes)

Status: DRAFT, awaiting approval. No code until approved.

## Goal

Split the Data tab into two subtabs:
- Raw Ticks - tick CSVs under data/ticks, plus any cache envelope whose Origin is RawTick (i.e. bars compiled from those CSVs)
- Timeframes - cache envelopes whose Origin is TimeframeFetch (network provider at the requested timeframe)

Both sides remain queuable. Consuming a raw-tick entry in Runner or Chart shows a warning popup until the future compile-on-demand core module lands.

## Design decisions (locked)

1. Schema discriminator: add DataOrigin enum to CacheEnvelope. Values Legacy=0, RawTick=1, TimeframeFetch=2. Legacy resolves at read time from source string: "MT5 Parsed" to RawTick, everything else to TimeframeFetch. Old files load unchanged, upgraded lazily on next write.
2. Placement: compiled MT5 bars go to Raw Ticks (lineage-based rule). Timeframes holds only network-fetched data.
3. Subtab names: "Raw Ticks" and "Timeframes".
4. Fetch flow on Raw Ticks: two action groups. Group 1 Fetch ticks from MT5 with optional "Then compile to" combo. Group 2 Compile from selected tick CSV row. Both call one shared ITickCompiler service.
5. Parsing subtab in MT5 Ticks tab: deprecate (hide from strip, keep code for one release) after parity checklist passes.
6. Queue origin: store DataOrigin alongside symbol@timeframe in SelectionQueue. Gate re-resolves from cache file at launch.

## Ordered steps

Step 1 - DataOrigin enum + envelope field
- Add src/core/Data/DataOrigin.cs (enum)
- Add Origin property to CacheEnvelope with default Legacy
- Add IDataOriginResolver (FromSource, Resolve) registered as singleton
- Back-compat SaveCacheAsync overload keeps existing callers compiling
- Unit test: legacy envelope without Origin loads and resolves correctly
- User-visible: none. Independently revertable.

Step 2 - SelectionQueue origin tracking
- Add overload Add(symbol, timeframe, provider, origin)
- Add GetOrigin(symbol, timeframe)
- Add QueuedDatasetOrigins dictionary to AppSettings
- Persist alongside QueuedDatasets (unchanged), backwards compatible
- User-visible: none. Independently revertable.

Step 3 - Queue origin gate
- Add IQueueOriginGate (CheckAsync) used by Runner and Chart before loading data
- Tick-only entries (raw CSV, no bars): block with Remove / Cancel
- Compiled-bars entries: warn with Continue anyway / Remove
- Future compile module deletes the RawTick branch
- User-visible: warnings appear only on raw-tick entries.

Step 4 - Data tab split
- Wrap existing DataView content in TabControl with two TabItems
- Raw Ticks item: reuse DataGrid, filter Origin=RawTick, include tick CSV rows (Kind column distinguishes Ticks CSV vs Compiled bars)
- Timeframes item: existing content minus RawTick rows, fetch form stays
- Share DataGridViewModelBase between both subtabs (filter, sort, group logic)
- No fetch form on Raw Ticks yet (Step 5 adds it)

Step 5 - Raw Ticks fetch and compile forms
- Extract compile logic from MT5 Parsing subtab into ITickCompiler DI service
- Raw Ticks toolbar: Fetch group (Symbol, From UTC, To UTC, Then compile to, Fetch Ticks, Fetch Ticks and Queue)
- Raw Ticks toolbar: Compile group (Tick set combo, Timeframe combo, Compile, Compile and Queue)
- Fetch streams to data/ticks via existing writer, optional compile-through
- Compile saves with Origin=RawTick, source="MT5 Parsed"

Step 6 - MT5 Parsing subtab deprecation
- Hide Parsing from tab strip or show one-line "Moved to Data > Raw Ticks" notice
- Parity checklist before full removal in a later release
- Keep ITickCompiler, keep the code

## Risks

- One file per symbol_timeframe: provider fetch overwrites MT5 Parsed file (or reverse). Row jumps subtabs. Mitigation: overwrite confirmation when incoming Origin differs from existing.
- Tick CSV listing slow on large files: read only first and last line for grid display, tick count lazy on demand.
- Stale queue origin after cache overwrite: gate re-resolves from disk.
- Subtab names may need polish once built; rename deferred to post-build.

## Out of scope

- Compile-on-demand core module (future, replaces the warning gate)
- Providers tab split (separate doc)
- Tick CSV write path changes (existing MT5 writer reused)