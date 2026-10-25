
using System;

namespace backs2.Core.Diagnostics;

/// <summary>
/// One sample of the runtime performance monitor (ROADMAP Phase 36).
///
/// Nullability is meaningful: a null field means "not available on this
/// machine", never zero. GPU utilization is NVIDIA-only; VRAM needs a DXGI
/// adapter. Consumers render missing metrics as a dash and skip their
/// thresholds.
/// </summary>
public sealed record PerfSnapshot(
    DateTime Utc,
    double? Fps,
    double WorkingSetMb,
    double AvailableRamMb,
    double CpuPercent,
    double? VramUsedMb,
    double? VramBudgetMb,
    double? GpuPercent);