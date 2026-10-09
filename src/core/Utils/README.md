# src/core/Utils/

Small stateless helpers that do not justify their own feature folder.

## What belongs here

A helper belongs in `src/core/Utils/` when **all** of the following hold:

- **Pure function.** Same input → same output. No hidden state, no caching.
- **No DI.** Not registered in `App.axaml.cs → ConfigureServices`, not injected anywhere. Static entry points only.
- **No logging.** Does not call `LogManager`. Callers log; the helper computes.
- **Cross-cutting.** Not owned by a single feature. Used by two or more unrelated areas, or designed to be.
- **Small.** Fits in one file under ~150 lines. If it grows past that, gains configuration, or needs a constructor, it graduates to its own `src/core/<Feature>/` folder.

## What does not belong here

- Anything registered in `App.axaml.cs`. That is a service, not a util.
- Formatting for a specific tab. That lives with the ViewModel that owns it.
- Conversions that only one provider or one metric needs. Those go inside that provider or metric.
- File I/O. `CacheManager` owns cache I/O, `ConfigManager` owns config I/O. Utils are in-memory.

## Current residents

- **`EasternTime.cs`** — DST-aware Eastern Time conversion for Unix-ms UTC timestamps. Used by strategies that act on a specific ET wall-clock hour (e.g. the 09:30 ET RTH-open studies; 09:00 ET is pre-market and not the open).