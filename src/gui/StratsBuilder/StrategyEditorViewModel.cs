using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;

namespace backs2.ViewModels;

public partial class StrategyEditorViewModel : ObservableObject
{
    private readonly StrategyManager _strategyManager;
    private string _code = "public class MyStrategy { }";

    // Manually defined property to avoid Source Generator issues
    public string Code
    {
        get => _code;
        set => SetProperty(ref _code, value);
    }

    public StrategyEditorViewModel(StrategyManager strategyManager)
    {
        _strategyManager = strategyManager;
    }

    [RelayCommand]
    public async Task CompileCurrentStrategy()
    {
        string filePath = $"strats/temp_{Guid.NewGuid()}.cs";
        await File.WriteAllTextAsync(filePath, Code);
        await _strategyManager.CompileStrategy(Code);
    }
}