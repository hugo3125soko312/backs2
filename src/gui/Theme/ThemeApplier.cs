namespace backs2.Theme;

/// <summary>
/// In WPF, theme is applied declaratively in App.xaml via ResourceDictionary.
/// This class exists for backward compatibility with code that calls ThemeApplier.Apply().
/// </summary>
public static class ThemeApplier
{
    public static void Apply(object settings)
    {
        // WPF: theme is applied declaratively in App.xaml.
        // This method is kept for backward compatibility with existing code calls.
    }
}
