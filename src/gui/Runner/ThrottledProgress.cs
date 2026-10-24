using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace backs2.ViewModels;

/// <summary>
/// An <see cref="IProgress{T}"/> that rate-limits its UI dispatch (ROADMAP
/// Phase 22E).
///
/// <para><b>The problem.</b> <c>Progress<T></c> posts to the captured
/// synchronization context for <em>every</em> report. The optimizer emits one
/// report per version per stage, which on a 500-combination grid is thousands
/// of dispatcher posts per run. Each post lands on the UI thread and writes
/// three observable properties (progress bar, ETA text, status line), so each
/// one costs a property-changed notification and a layout pass. The UI thread
/// spends the whole run servicing progress instead of remaining responsive.</para>
///
/// <para><b>The fix.</b> The first report of a burst applies immediately. While
/// the interval is still open, later reports overwrite a single pending slot
/// instead of each scheduling their own post — a burst of N collapses to one
/// UI update per interval. Terminal reports (phase change, or the last item of
/// a stage) always apply immediately so the display never stalls on a stale
/// value.</para>
///
/// <para><b>Threading.</b> <see cref="Report"/> may be called from any thread.
/// The pending slot and the scheduled flag are guarded by a private lock; the
/// applied action always runs on the UI thread.</para>
/// </summary>
internal sealed class ThrottledProgress<T> : IProgress<T>
{
private readonly Action<T> _apply;
private readonly Func<T, bool>? _flushImmediately;
private readonly long _minIntervalTicks;
private readonly object _gate = new();

private long _lastApplyTicks;
private T? _pending;
private bool _hasPending;
private bool _flushScheduled;

/// <param name="apply">Runs on the UI thread with the value to display.</param>
/// <param name="minIntervalMs">Minimum spacing between UI updates.</param>
/// <param name="flushImmediately">
/// Optional predicate: a report that satisfies it bypasses the throttle.
/// Used for phase transitions and the terminal report.
/// </param>
public ThrottledProgress(Action<T> apply, int minIntervalMs = 100, Func<T, bool>? flushImmediately = null)
{
_apply = apply ?? throw new ArgumentNullException(nameof(apply));
_flushImmediately = flushImmediately;
_minIntervalTicks = Stopwatch.Frequency * Math.Max(1, minIntervalMs) / 1000L;
_lastApplyTicks = Stopwatch.GetTimestamp() - _minIntervalTicks;
}

/// <inheritdoc/>
public void Report(T value)
{
bool immediate = _flushImmediately != null && _flushImmediately(value);

lock (_gate)
{
long now = Stopwatch.GetTimestamp();
bool intervalOpen = now - _lastApplyTicks >= _minIntervalTicks;

if (immediate || (intervalOpen && !_flushScheduled))
{
_lastApplyTicks = now;
_hasPending = false;
_pending = default;
Apply(value);
return;
}

// Coalesce: overwrite the pending slot. The scheduled flush will
// deliver whatever the newest value is when it fires.
_pending = value;
_hasPending = true;

if (_flushScheduled) return;
_flushScheduled = true;
}

ScheduleFlush();
}

/// <summary>
/// Forces the pending value out now. Call once when the run ends so the
/// final state is never left sitting in the slot.
/// </summary>
public void Flush()
{
T? value;
lock (_gate)
{
if (!_hasPending) return;
value = _pending;
_pending = default;
_hasPending = false;
_lastApplyTicks = Stopwatch.GetTimestamp();
}
Apply(value!);
}

private void ScheduleFlush()
{
int delayMs;
lock (_gate)
{
long elapsed = Stopwatch.GetTimestamp() - _lastApplyTicks;
long remaining = Math.Max(1L, _minIntervalTicks - elapsed);
delayMs = (int)Math.Max(1L, remaining * 1000L / Stopwatch.Frequency);
}

_ = Task.Delay(delayMs).ContinueWith(
_ =>
{
lock (_gate)
{
_flushScheduled = false;
if (!_hasPending) return;
}
Flush();
},
CancellationToken.None,
TaskContinuationOptions.ExecuteSynchronously,
TaskScheduler.Default);
}

private void Apply(T value)
{
if (Dispatcher.UIThread.CheckAccess())
{
_apply(value);
return;
}

Dispatcher.UIThread.Post(() => _apply(value));
}
}