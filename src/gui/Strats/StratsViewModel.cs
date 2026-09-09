using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using backs2.Core;

namespace backs2.ViewModels;

public partial class StratsViewModel : ObservableObject
{
    private readonly StrategyManager _strat;

    [ObservableProperty]
    private double _sharpe;

    [ObservableProperty]
    private double _winRate;

    public StratsViewModel(StrategyManager strat)
    {
        _strat = strat;
    }

    public async Task RunAndDisplay(string stratId, string symbol)
    {
        await _strat.RunBacktestAsync(stratId, symbol);
    }
}