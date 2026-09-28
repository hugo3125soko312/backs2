<!-- docs\COMMANDS.md -->
# Logs Tab Command Line

The Logs tab exposes a single-line command input at the bottom of the view. It executes against the running engine — no restart, no rebuild, no external tooling. Output is appended to the live log view and mirrored in the status text next to the input.

## Input

A TextBox preceded by a `>` prompt. Press **Enter** or click **Run** to execute. The input clears on submit and stays focused for the next command. Commands are disabled while one is in flight (`IsExecuting`).

## Multiline input

Pasting multiple commands executes them sequentially, one after another, in the order they appear. Lines are split on `\n`, `\r\n`, and `;`. Empty lines are skipped. Each command is echoed with a `>` prefix before it runs, so the full batch is visible in the log view.

If one command in the batch throws, the error is captured and displayed, and execution continues with the next command. The batch is not aborted.

Example paste:

    test Binance BTCUSDT 1h
    test Binance ETHUSDT 1h
    test Yahoo XAUUSD 1D

produces three sequential runs, each with its own `>` echo, `[CMD]` output, and volume report.

Semicolons work as separators too:

    test Binance BTCUSDT 1h; test Yahoo XAUUSD 1D

## Syntax

    <verb> [provider] <symbol> <timeframe>

Arguments are space-separated. Provider names may contain spaces (`Twelve Data`, `FCS API`) — the parser reads symbol and timeframe from the last two tokens and treats everything between the verb and those as the provider. No quoting, no escaping.

## Commands

### `help`
Prints the command list into the log view, including example invocations. No arguments.

### `providers`
Prints every key in `AppSettings.ProviderOrder`, one per line. No arguments. Use this to confirm which providers are wired for the current session before running `fetch` or `test`.

### `fetch [provider] <symbol> <timeframe>`
Runs the full `DataManager.GetSymbolDataAsync` pipeline — memory tier, disk tier, network walk. Respects the standard three-tier fallback. Writes to cache on a successful network fetch.

- `[provider]` — optional. `Auto` or omitted means the standard walk over `ProviderOrder`. A specific name means try that provider first, then fall through to the rest.
- `<symbol>` — required. Case-sensitive as it appears in the cache.
- `<timeframe>` — required. Must be one of `TimeframeMap.All`.

Cache hits whose `source` differs from the requested provider are bypassed and refetched. Progress messages stream into `CommandStatus`.

Examples:

    fetch XAUUSD 1D
    fetch Yahoo XAUUSD 1D
    fetch FCS API EURUSD 1h

On success, prints the bar count and the provider that actually landed (resolved from cache metadata). If the preferred provider failed and a fallback supplied the data, the output notes it:

    [CMD] fetch XAUUSD 1D → 620 bars from Massive (fell back from Yahoo)

### `test <provider> <symbol> <timeframe>`
Calls a specific provider's `FetchAsync` directly, bypassing `DataManager` entirely. No cache read, no cache write, no fallback walk.

- `<provider>` — required. Must match a keyed provider registration exactly (e.g. `Binance`, `FCS API`, `Twelve Data`). `Auto` is rejected — test requires an explicit target.
- `<symbol>` — required.
- `<timeframe>` — required.

Reports the bar count, elapsed milliseconds, and volume statistics:

    [CMD] test Massive XAUUSD@1D → 620 bars in 526 ms
    [CMD]   volume: full volume — nonzero 620/620, max 289997, total 52609229

Volume tags:

- `full volume` — every bar has a nonzero `Volume`
- `partial N/M` — some bars have nonzero volume
- `NO VOLUME` — every bar has zero volume; the series will feed zeros into VWAP/CVD

Use this command to isolate a single provider's behavior when a multi-provider walk is producing ambiguous results — it removes `DataManager` from the picture entirely.

Examples:

    test Binance BTCUSDT 1h
    test Yahoo XAUUSD 1D
    test FCS API EURUSD 1h

A `0 ms` timing on a rejected symbol is the diagnostic: the provider short-circuits in-process before any HTTP call, per its own symbol or timeframe filter. A nonzero elapsed time means a network request was attempted.

### `clear`
Clears the log view's in-memory buffer. Does not delete the log file on disk, and does not affect `LogManager`'s ring buffer. Equivalent to the **Clear View** button.

## Behavior notes

- Commands are executed on the UI thread but `await` their work. The input stays responsive during long fetches.
- Multiline batches run sequentially, not in parallel. A slow `fetch` delays every command after it in the same paste.
- Every command is echoed into the log view with a `>` prefix before execution, so the command history survives **Refresh from File** and **Copy All**.
- Output lines emitted by commands use the `[CMD]` tag to distinguish them from engine log lines. Errors use `[ERR] [CMD]`.
- Exception details surface inline in the log view as `Type: message → InnerType: inner message`. Full stack traces remain in `logs/runtime-*.log`.
- The command line has no access to the Runner, Chart, or Monte Carlo tabs. It is scoped to `DataManager` and `ConfigManager` operations only.
- Unknown verbs produce `Unknown command: <verb>. Type 'help'.` in the status line and no log output.

## Reference

| Verb | Args | Calls |
|---|---|---|
| `help` | — | — |
| `providers` | — | reads `ConfigManager.Settings.ProviderOrder` |
| `fetch` | `[provider] <symbol> <timeframe>` | `DataManager.GetSymbolDataAsync` |
| `test` | `<provider> <symbol> <timeframe>` | `DataManager.GetProvider` then `IDataProvider.FetchAsync` |
| `clear` | — | clears the in-memory view buffer |