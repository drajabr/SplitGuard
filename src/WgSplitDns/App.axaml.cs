using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WgSplitDns.ViewModels;
using WgSplitDns.Views;

namespace WgSplitDns;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var vm = new MainViewModel(window);
            window.DataContext = vm;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => vm.OnExit();
            _ = vm.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
