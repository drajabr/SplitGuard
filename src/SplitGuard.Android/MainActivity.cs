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
    const int VpnConsentRequest = 71;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        App.Platform = new AndroidPlatform();
        App.BuildHead = app =>
        {
            if (app.ApplicationLifetime is not ISingleViewApplicationLifetime single) return;
            // Segoe icon fonts don't exist on Android — swap in the bundled symbols font
            // (same codepoints, open Fluent System Icons outlines).
            app.Resources["GlyphFontFamily"] =
                new Avalonia.Media.FontFamily("avares://SplitGuard.Core/Assets/Fonts#SplitGuard Symbols");
            var view = new MainView();
            var vm = new MainViewModel(new AndroidDialogs(view), App.Platform!);
            view.DataContext = vm;
            view.ApplyUiPrefs(vm.Prefs);
            single.MainView = view;
            _ = vm.InitializeAsync();
        };
        return base.CustomizeAppBuilder(builder);
    }

    protected override void OnResume()
    {
        base.OnResume();
        // The VPN consent dialog (one-time, or again after revocation) must come from an
        // Activity. Ask up front so the connect toggle just works.
        var consent = Android.Net.VpnService.Prepare(this);
        if (consent is not null)
            StartActivityForResult(consent, VpnConsentRequest);
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
