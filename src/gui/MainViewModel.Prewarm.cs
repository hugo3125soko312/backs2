using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Threading.Tasks;

namespace backs2.ViewModels;

public partial class MainViewModel
{
    private CancellationTokenSource? _prewarmCts;
    private bool _prewarmStarted;

    /// <summary>
    /// Kicked off from the constructor via Post so the initial render can
    /// settle first. Materializes each tab's ViewModel + View in the
    /// background so a first click on any tab is instant. Cancelled by the
    /// first user selection change after startup — the user's navigation
    /// always wins over background warming.
    /// </summary>
    private void StartPrewarm()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _prewarmStarted = true;
            _prewarmCts = new CancellationTokenSource();
            _ = PrewarmTabsAsync(_prewarmCts.Token);
        });
    }

    private void CancelPrewarmIfStarted()
    {
        if (!_prewarmStarted) return;
        _prewarmCts?.Cancel();
    }

    private async Task PrewarmTabsAsync(CancellationToken ct)
    {
        // Let the initial render settle.
        try { await Task.Delay(500, ct); }
        catch (OperationCanceledException) { return; }

        // Heavy-first ordering — the tabs whose first-click cost is biggest
        // warm up first, so the user hits them warm if they navigate early.
        var order = new[]
        {
            _chartNode,
            _runnerNode,             // covers both Runner + Optimization Results
            _mt5TicksNode,
            _dataAutomationNode,
            _dataNode,
            _providersNode,
            _logsNode,
            _monteCarloNode,
            _metricsNode,
            _accountsNode,
            _configNode
        };

        foreach (var node in order)
        {
            if (ct.IsCancellationRequested) return;
            if (node.Content != null) continue;

            try
            {
                node.Content = node.Factory();
            }
            catch
            {
                // Non-fatal. The user's first click on this node will surface
                // the real error through the normal path.
            }

            // Hand the UI thread back so the app stays interactive. Yield
            // alone isn't enough — the template application + measure pass
            // needs a real idle window.
            await Task.Yield();
            try { await Task.Delay(250, ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
