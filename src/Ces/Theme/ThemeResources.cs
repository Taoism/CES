using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ces;

/// <summary>Theme colours for controls built in code, following Light / Dark / Follow system live.</summary>
internal static class ThemeResources
{
    /// <summary>Binds <paramref name="property"/> to the theme resource <paramref name="key"/> (like DynamicResource).</summary>
    public static T Themed<T>(this T target, AvaloniaProperty property, string key) where T : Control
    {
        target.Bind(property, target.GetResourceObservable(key));
        return target;
    }

    /// <summary>The current value of a theme brush, for drawing code (re-read on every render).</summary>
    public static IBrush ThemeBrush(this Control control, string key, IBrush fallback) =>
        control.TryFindResource(key, control.ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;
}
