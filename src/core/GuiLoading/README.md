# GuiLoading

Reusable streaming loader for ObservableCollection fill paths.

See ROADMAP Phase 38A. The pattern is lifted from LogsViewModel bounded-queue + coalesced-flush.

## Contract

- Bounded batch size (default 100).
- Sink runs on the UI thread; producer runs on the worker.
- Latest-wins: a new StartAsync cancels the in-flight stream.
- Cancellation: pass a CancellationToken; a tab switch or refresh click aborts the stream cleanly.

## Not yet wired

Phase 38A ships the module only. Phases 38B-38E route the Data, Accounts, Providers, and remaining tabs through it.
