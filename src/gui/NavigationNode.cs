using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace backs2.ViewModels;

/// <summary>
/// One node in the shell navigation tree. Root nodes map to a top-level tab
/// ViewModel via <see cref="Factory"/>. Child nodes are subtabs of their
/// parent — they resolve the same ViewModel and additionally carry a
/// <see cref="SubtabIndex"/> that the parent's INavigationTarget.Navigate
/// consumes on selection.
///
/// IsExpanded drives the accordion: only one root is expanded at a time,
/// enforced by MainViewModel.OnSelectedNodeChanged.
/// </summary>
public partial class NavigationNode : ObservableObject
{
    public string Header { get; init; } = "";

    /// <summary>Subtab to activate on the parent VM. -1 = no subtab action.</summary>
    public int SubtabIndex { get; init; } = -1;

    /// <summary>Resolves (or returns the cached) tab ViewModel for this node.</summary>
    public Func<object> Factory { get; init; } = null!;

    public ObservableCollection<NavigationNode> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;
}

/// <summary>
/// Implemented by tab ViewModels that own a subtab strip. The shell calls
/// <see cref="Navigate"/> when a child node is selected in the navigation tree.
/// </summary>
public interface INavigationTarget
{
    void Navigate(int subtabIndex);
}