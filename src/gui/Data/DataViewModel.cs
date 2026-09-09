using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using backs2.Core;

namespace backs2.ViewModels;

public partial class DataViewModel : ObservableObject
{
    private readonly DataManager _dataManager;
    
    public ObservableCollection<DataEntry> Datasets { get; } = new();

    public DataViewModel(DataManager dataManager)
    {
        _dataManager = dataManager;
        RefreshData();
    }

    public void RefreshData()
    {
        Datasets.Clear();
        foreach (var entry in _dataManager.GetAvailableData())
        {
            Datasets.Add(entry);
        }
    }
}