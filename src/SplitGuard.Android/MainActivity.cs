using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using SplitGuard.ViewModels;
using SplitGuard.Views;

namespace SplitGuard.Droid;

[Activity(
    Label = "SplitGuard",
    Theme = "@style/SplitGuardTheme",
    Icon = "@drawable/icon",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // Phase 2 bring-up: the Android head runs the UI review harness (canned config,
        // no side effects) until the VpnService engine lands in Phase 3.
        SplitGuard.Services.RuleStore.DemoMode = true;

        App.Platform = new AndroidPlatform();
        App.BuildHead = app =>
        {
            if (app.ApplicationLifetime is not ISingleViewApplicationLifetime single) return;
            var view = new MainView();
            var vm = new MainViewModel(new AndroidDialogs(view), App.Platform!);
            view.DataContext = vm;
            view.ApplyUiPrefs(vm.Prefs);
            single.MainView = view;
            _ = vm.InitializeAsync();
        };
        return base.CustomizeAppBuilder(builder);
    }
}

// Clipboard via the Avalonia TopLevel; notifications as a simple Android toast for now
// (the VPN foreground notification arrives with the Phase 3 service).
public class AndroidDialogs : IDialogs
{
    readonly MainView _view;
    public AndroidDialogs(MainView view) => _view = view;

    public async Task CopyToClipboardAsync(string text)
    {
        var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(_view)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(text);
    }

    public void Notify(string title, string message, bool isError) =>
        Dispatcher.UIThread.Post(() =>
            Android.Widget.Toast.MakeText(Android.App.Application.Context,
                $"{title}: {message}", Android.Widget.ToastLength.Short)?.Show());
}
