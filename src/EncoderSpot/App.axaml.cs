using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace EncoderSpot;

public sealed class App : Application
{
    private static string? _themeMode;
    private static bool _watchingSystem;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>"Light", "Dark", or anything else to follow the operating system (live).</summary>
    public static void ApplyTheme(string? theme)
    {
        if (Current is null)
        {
            return;
        }
        _themeMode = theme;
        var platform = Current.PlatformSettings;
        if (platform is not null && !_watchingSystem)
        {
            _watchingSystem = true;
            platform.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (_themeMode is not ("Light" or "Dark"))
                {
                    ApplyTheme(_themeMode);
                }
            });
        }
        // resolved explicitly: going back to ThemeVariant.Default does not re-read the OS theme
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
            desktop.MainWindow = new MainWindow(desktop.Args ?? []);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
