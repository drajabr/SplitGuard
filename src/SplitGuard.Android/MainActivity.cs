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
    const int CameraPermRequest = 72;
    static Action? _cameraGranted;

    // Called by the QR scanner when CAMERA isn't granted yet: prompt, then run the callback
    // once (whether granted or denied — the scanner re-checks the permission).
    public static void RequestCameraThen(Action then)
    {
        var act = Current;
        if (act is null) { then(); return; }
        _cameraGranted = then;
        act.RequestPermissions(new[] { Android.Manifest.Permission.Camera }, CameraPermRequest);
    }

    public static MainActivity? Current { get; private set; }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        App.Platform = new AndroidPlatform();
        // Mobile reflow of the collapsed card detail (2-column stat grid).
        Views.TunnelCard.Compact = true;
        App.BuildHead = app =>
        {
            if (app.ApplicationLifetime is not ISingleViewApplicationLifetime single) return;
            // Segoe icon fonts don't exist on Android — swap in the bundled symbols font
            // (same codepoints, open Fluent System Icons outlines).
            app.Resources["GlyphFontFamily"] =
                new Avalonia.Media.FontFamily("avares://SplitGuard.Core/Assets/Fonts#SplitGuard Symbols");
            var view = new MainView();
            var vm = new MainViewModel(new AndroidDialogs(view), App.Platform!);
            SgVpnService.SplitDnsEnabled = vm.Prefs.AndroidSplitDns;
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
        Current = this;
        // The VPN consent dialog (one-time, or again after revocation) must come from an
        // Activity. Ask up front so the connect toggle just works.
        var consent = Android.Net.VpnService.Prepare(this);
        if (consent is not null)
            StartActivityForResult(consent, VpnConsentRequest);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == CameraPermRequest)
        {
            var cb = _cameraGranted;
            _cameraGranted = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => cb?.Invoke());
        }
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
