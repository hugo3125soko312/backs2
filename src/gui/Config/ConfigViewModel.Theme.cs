// src\gui\Config\ConfigViewModel.Theme.cs
using backs2.Theme;

namespace backs2.ViewModels;

public partial class ConfigViewModel
{
    /// <summary>
    /// Resets every Theme*Color property on AppSettings back to the
    /// default palette shipped in Tokens.axaml. Does not save — the user
    /// still clicks Save Settings to persist the reset.
    /// </summary>
    public void ResetThemeToDefaults()
    {
        _config.Settings.ThemeAppBgColor          = "#FF0D1117";
        _config.Settings.ThemePanelBgColor        = "#FF161B22";
        _config.Settings.ThemeElevatedColor       = "#FF21262D";
        _config.Settings.ThemeSunkenColor         = "#FF000000";
        _config.Settings.ThemeTextPrimaryColor    = "#FFE6EDF3";
        _config.Settings.ThemeTextSecondaryColor  = "#FF848D97";
        _config.Settings.ThemeTextDisabledColor   = "#FF484F58";
        _config.Settings.ThemeBorderColor         = "#FF30363D";
        _config.Settings.ThemeAccentColor         = "#FF2F81F7";
        _config.Settings.ThemeAccentHoverColor    = "#FF458EF7";
        _config.Settings.ThemeAccentPressedColor  = "#FF176BE0";
        _config.Settings.ThemeSemanticSuccessColor = "#FF238636";
        _config.Settings.ThemeSemanticWarnColor   = "#FFD29922";
        _config.Settings.ThemeSemanticDangerColor = "#FFF85149";

        // Reload the pickers from the reset AppSettings values.
        foreach (var item in ThemeSettings)
        {
            var val = item.Property.GetValue(_config.Settings) as string ?? "";
            if (Avalonia.Media.Color.TryParse(val, out var c))
                item.ColorValue = c;
        }
    }

    /// <summary>
    /// Applies a single SettingItem's current value to AppSettings and
    /// re-runs ThemeApplier so the shell recolors live. Called when the
    /// user clicks OK in the color picker dialog. Does NOT write to disk
    /// — the user still clicks Save Settings to persist.
    /// </summary>
    public void ApplyThemePreview(SettingItem item)
    {
        item.Apply(_config.Settings);
        ThemeApplier.Apply(_config.Settings);
    }
}