// src/gui/Config/ConfigViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Providers;
using backs2.Theme;
using Microsoft.Extensions.DependencyInjection;

namespace backs2.ViewModels;

public partial class SettingItem : ObservableObject
{
    public string DisplayName { get; }
    public PropertyInfo Property { get; }

    [ObservableProperty] private int _intValue;
    [ObservableProperty] private double _doubleValue;
    [ObservableProperty] private string _stringValue = "";
    [ObservableProperty] private bool _boolValue;
    [ObservableProperty] private System.Windows.Media.Color _colorValue;

    public bool IsInt => Property.PropertyType == typeof(int);
    public bool IsDouble => Property.PropertyType == typeof(double);
    public bool IsBool => Property.PropertyType == typeof(bool);
    public bool IsColor => Property.PropertyType == typeof(string) && Property.Name.Contains("Color", StringComparison.OrdinalIgnoreCase);
    public bool IsString => Property.PropertyType == typeof(string) && !IsColor;

    public SettingItem(PropertyInfo prop, object instance)
    {
        Property = prop;
        DisplayName = Regex.Replace(prop.Name, "([A-Z])", " $1").Trim();

        var val = prop.GetValue(instance);

        if (IsInt) IntValue = (int)(val ?? 0);
        else if (IsDouble) DoubleValue = (double)(val ?? 0.0);
        else if (IsBool) BoolValue = (bool)(val ?? false);
        else if (IsColor)
        {
            var s = (string)(val ?? "#FF0D1117");
            if (System.Windows.Media.ColorConverter.ConvertFromString(s) is System.Windows.Media.Color c)
                ColorValue = c;
        }
        else if (IsString) StringValue = (string)(val ?? "");
    }

    public void Apply(object instance)
    {
        if (IsInt) Property.SetValue(instance, IntValue);
        else if (IsDouble) Property.SetValue(instance, DoubleValue);
        else if (IsBool) Property.SetValue(instance, BoolValue);
        else if (IsColor) Property.SetValue(instance, ColorValue.ToString());
        else if (IsString) Property.SetValue(instance, StringValue);
    }
}

public partial class ConfigViewModel : ObservableObject
{
    private readonly ConfigManager _config;

    public ObservableCollection<SettingItem> DynamicSettings { get; } = new();
    public ObservableCollection<SettingItem> ThemeSettings { get; } = new();
    public ObservableCollection<string> ProviderOrder { get; } = new();
    public ObservableCollection<string> AvailableProviders { get; } = new();

    [ObservableProperty] private string _selectedAvailable = "";
    [ObservableProperty] private string? _selectedInOrder;

    public ConfigViewModel(ConfigManager config, IServiceProvider serviceProvider)
    {
        _config = config;

        foreach (var prop in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.Name == nameof(AppSettings.ProviderOrder)) continue;

            var item = new SettingItem(prop, _config.Settings);
            if (item.IsColor) ThemeSettings.Add(item);
            else DynamicSettings.Add(item);
        }

        DiscoverProviders(serviceProvider);

        foreach (var p in _config.Settings.ProviderOrder)
            ProviderOrder.Add(p);
    }

    private void DiscoverProviders(IServiceProvider serviceProvider)
    {
        var providerTypes = typeof(IDataProvider).Assembly.GetTypes()
            .Where(t => typeof(IDataProvider).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        foreach (var type in providerTypes)
        {
            try
            {
                if (ActivatorUtilities.CreateInstance(serviceProvider, type) is IDataProvider provider)
                {
                    if (!string.IsNullOrEmpty(provider.Name) && !AvailableProviders.Contains(provider.Name))
                    {
                        AvailableProviders.Add(provider.Name);
                        LogManager.Debug(LogTag.Config, $"discovered provider: {provider.Name}");
                    }
                }
            }
            catch
            {
                // Fallback for unresolvable provider types
            }
        }
    }

    [RelayCommand]
    private void AddProvider()
    {
        if (string.IsNullOrWhiteSpace(SelectedAvailable)) return;
        if (ProviderOrder.Contains(SelectedAvailable)) return;

        ProviderOrder.Add(SelectedAvailable);
        SelectedAvailable = "";
    }

    [RelayCommand]
    private void RemoveProvider()
    {
        if (SelectedInOrder == null) return;
        ProviderOrder.Remove(SelectedInOrder);
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (SelectedInOrder == null) return;
        var i = ProviderOrder.IndexOf(SelectedInOrder);
        if (i <= 0) return;
        ProviderOrder.Move(i, i - 1);
        SelectedInOrder = ProviderOrder[i - 1];
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (SelectedInOrder == null) return;
        var i = ProviderOrder.IndexOf(SelectedInOrder);
        if (i < 0 || i >= ProviderOrder.Count - 1) return;
        ProviderOrder.Move(i, i + 1);
        SelectedInOrder = ProviderOrder[i + 1];
    }

    [RelayCommand]
    private void Save()
    {
        foreach (var setting in DynamicSettings)
        {
            setting.Apply(_config.Settings);
        }

        foreach (var setting in ThemeSettings)
        {
            setting.Apply(_config.Settings);
        }

        _config.Settings.ProviderOrder = new List<string>(ProviderOrder);

        _config.SaveConfig();
        ThemeApplier.Apply(_config.Settings);
        LogManager.Info(LogTag.Config, "settings saved");
    }
}
