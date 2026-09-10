using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using System.Threading.Tasks;

namespace backs2.ViewModels;

public partial class StrategyEditorViewModel : ObservableObject
{
    private readonly StrategyManager _strategyManager;

    [ObservableProperty]
    private string _scriptSource = "// Use Engine.GetMetric(\"VWAP\").Calculate(Data) here";

    public StrategyEditorViewModel(StrategyManager strategyManager)
    {
        _strategyManager = strategyManager;
    }

    [RelayCommand]
    public async Task RunStrategy()
    {
        // In a real flow, we'd pull the actual data from DataManager here
        await _strategyManager.RunBacktestAsync(ScriptSource, new System.Collections.Generic.List<Core.Models.Kline>());
    }
}