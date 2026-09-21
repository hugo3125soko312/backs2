using CommunityToolkit.Mvvm.ComponentModel;
using backs2.Core;

namespace backs2.ViewModels;

public partial class ChartViewModel : ObservableObject
{
    private readonly DataManager _data;

    [ObservableProperty]
    private string _statusText = "Chart ready.";

    public ChartViewModel(DataManager data)
    {
        _data = data;
    }
}