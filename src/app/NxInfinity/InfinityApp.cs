using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace NxInfinity;

public sealed class InfinityApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(VisualTheme.Create());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime classicDesktopStyleApplicationLifetime)
        {
            InfinityPaths paths = InfinityPaths.FromArguments(Program.Arguments);
            classicDesktopStyleApplicationLifetime.MainWindow = new MainWindow(new InfinitySession(paths))
            {
                ShowStartupSequence = true
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
