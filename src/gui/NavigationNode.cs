using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace backs2.ViewModels;

/// <summary>
/// One node in the shell navigation tree.
///
/// Root nodes own a <see cref="Content"/> (the tab ViewModel) that the shell
/// host renders — materialized lazily on first visit, then kept alive for the
/// lifetime of the app so subsequent switches are visibility flips, not view
/// rebuilds. <see cref="IsActive"/> marks which root is currently displayed.
///
/// Child nodes are subtabs of a root — they reuse the same Content and drive
/// the parent VM's subtab through <see cref="SubtabIndex"/>.
/// </summary>
public partial class NavigationNode : ObservableObject
{
    public string Header { get; init; } = "";

    /// <summary>Subtab to activate on the parent VM. -1 = no subtab action.</summary>
    public int SubtabIndex { get; init; } = -1;

    /// <summary>Resolves (or returns the cached) tab ViewModel for this node.</summary>
    public Func<object> Factory { get; init; } = null!;

    public ObservableCollection<NavigationNode> Children { get; } = new();

    /// <summary>Accordion state — only one root is expanded at a time.</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>
    /// The tab ViewModel. Null until the node is first selected, then never
    /// cleared — this is what keeps revisits cheap (no view rebuild).
    /// </summary>
    [ObservableProperty] private object? _content;

    /// <summary>
    /// True for the single root whose Content is currently displayed.
    /// Bound to ContentControl.IsVisible in the shell host.
    /// </summary>
    [ObservableProperty] private bool _isActive;
}

/// <summary>
/// Implemented by tab ViewModels that own a subtab strip. The shell calls
/// <see cref="Navigate"/> when a child node is selected in the navigation tree.
/// </summary>
public interface INavigationTarget
{
    void Navigate(int subtabIndex);
}