using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using backs2.Core;

namespace backs2.Theme;

/// <summary>
/// Writes the theme color settings from AppSettings into the live
/// Application resource dictionary. Called once at startup (after
/// ConfigManager resolves, before MainWindow constructs) and again
/// from ConfigViewModel.Save() so an edit takes effect immediately.
///
/// Lives under src/gui/ because it references Avalonia — the src/core/
/// layer is UI-free by design.
/// </summary>
public static class ThemeApplier
{
    public static void Apply(AppSettings settings)
    {
        var app = Application.Current;
        if (app == null) return;

        var res = app.Resources;

        SetBrush(res, "SurfaceApp",            settings.ThemeAppBgColor);
        SetBrush(res, "SurfacePanel",          settings.ThemePanelBgColor);
        SetBrush(res, "SurfaceElevated",       settings.ThemeElevatedColor);
        SetBrush(res, "SurfaceSunken",         settings.ThemeSunkenColor);
        SetBrush(res, "TextPrimary",           settings.ThemeTextPrimaryColor);
        SetBrush(res, "TextSecondary",         settings.ThemeTextSecondaryColor);
        SetBrush(res, "TextDisabled",          settings.ThemeTextDisabledColor);
        SetBrush(res, "BorderDefault",         settings.ThemeBorderColor);
        SetBrush(res, "AccentPrimary",         settings.ThemeAccentColor);
        SetBrush(res, "AccentHover",           settings.ThemeAccentHoverColor);
        SetBrush(res, "AccentPressed",         settings.ThemeAccentPressedColor);
        SetBrush(res, "AccentFocus",           settings.ThemeAccentColor);
        SetBrush(res, "SemanticSuccess",       settings.ThemeSemanticSuccessColor);
        SetBrush(res, "SemanticWarn",          settings.ThemeSemanticWarnColor);
        SetBrush(res, "SemanticDanger",        settings.ThemeSemanticDangerColor);
    }

    private static void SetBrush(IResourceDictionary res, string key, string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        if (!Color.TryParse(hex, out var color)) return;

        res[key] = new SolidColorBrush(color);
    }
}