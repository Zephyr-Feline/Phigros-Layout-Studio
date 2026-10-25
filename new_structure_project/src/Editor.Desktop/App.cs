using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace Editor.Desktop;

public sealed class App : Avalonia.Application
{
    public const string ProductName = "Phigros Layout Studio";
    public const string ProductDisplayName = ProductName + " 1.0";
    public const string ProductVersion = "1.0.0";

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
