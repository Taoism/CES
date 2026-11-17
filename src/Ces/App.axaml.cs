using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Ces;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    private static string? _themeMode;
    private static bool _watchingSystem;

    /// <summary>"Light", "Dark", or anything else to follow the operating system.</summary>
    public static void ApplyTheme(string? theme)
    {
        if (Current == null)
            return;
        _themeMode = theme;
        var platform = Current.PlatformSettings;
        if (platform != null && !_watchingSystem)
        {
            _watchingSystem = true;
            platform.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (_themeMode is not ("Light" or "Dark"))
                    ApplyTheme(_themeMode);
            });
        }
        Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => platform?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light ? ThemeVariant.Light : ThemeVariant.Dark,
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load();
            ApplyTheme(settings.Theme);
            desktop.MainWindow = new MainWindow(settings, desktop.Args ?? []);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
