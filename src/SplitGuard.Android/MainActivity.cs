using Android.App;
using Android.Content.PM;
using Android.OS;
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
    const int NotifPermRequest = 73;
    static Action? _cameraGranted;
    static bool _askedNotif;
    static MainView? _view; // for pause-time drawer close (releases the camera)

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

    // Tint the status + navigation bars to the app theme's page color, with dark glyphs on a light
    // background — so the system bars blend into the light/graphite page instead of sitting as a
    // hard white/black band. Called from the shared ApplyTheme via IPlatform.SetSystemBarColor.
    // Remembered so OnResume can re-apply it — Android resets the bars to the activity theme's
    // default when the app returns to the foreground, which otherwise reverts them to black/white.
    int? _barColor;
    bool _barLight;

    public void SetSystemBars(int color, bool lightBackground)
    {
        _barColor = color; _barLight = lightBackground;
        ApplySystemBars();
    }

    void ApplySystemBars() => RunOnUiThread(() =>
    {
        if (_barColor is not { } color) return;
        var window = Window;
        if (window?.DecorView is not { } decor) return;
        var c = new Android.Graphics.Color(color);
        window.SetStatusBarColor(c);
        window.SetNavigationBarColor(c);
        int flags = (int)(Android.Views.SystemUiFlags.LightStatusBar | Android.Views.SystemUiFlags.LightNavigationBar);
        int v = (int)decor.SystemUiVisibility;
        decor.SystemUiVisibility = (Android.Views.StatusBarVisibility)(_barLight ? (v | flags) : (v & ~flags));
    });

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
            _view = view;
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
        ApplySystemBars(); // Android resets the bars on foreground return — restore the theme tint.
        // The VPN consent dialog (one-time, or again after revocation) must come from an
        // Activity. Ask up front so the connect toggle just works.
        var consent = Android.Net.VpnService.Prepare(this);
        if (consent is not null)
            StartActivityForResult(consent, VpnConsentRequest);
        // Android 13+ needs a runtime grant for the VPN foreground-service notification to
        // show; ask once (Android suppresses repeat prompts after the user responds).
        if (!_askedNotif && Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
            && CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            _askedNotif = true;
            RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, NotifPermRequest);
        }
    }

    protected override void OnPause()
    {
        base.OnPause();
        // Backgrounding mid-scan must release the camera — close any open drawer (the QR
        // drawer's close path disposes the scanner).
        _view?.CloseDrawers();
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

    public Task CopyToClipboardAsync(string text)
    {
        // Use the native clipboard (not Avalonia's) so the clip can be flagged sensitive: an
        // exported peer config carries the interface private key, and everything this app copies
        // is crypto material. On Android 13+ the sensitive flag keeps it out of the clipboard
        // preview/history so a background app or the paste UI can't skim the key.
        var ctx = Android.App.Application.Context;
        if (ctx?.GetSystemService(Android.Content.Context.ClipboardService) is Android.Content.ClipboardManager cm)
        {
            var clip = Android.Content.ClipData.NewPlainText("SplitGuard", text);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu && clip?.Description is not null)
            {
                var extras = new PersistableBundle();
                extras.PutBoolean(Android.Content.ClipDescription.ExtraIsSensitive, true);
                clip.Description.Extras = extras;
            }
            cm.PrimaryClip = clip;
        }
        return Task.CompletedTask;
    }

    public void Notify(string title, string message, bool isError) =>
        Dispatcher.UIThread.Post(() =>
            Android.Widget.Toast.MakeText(Android.App.Application.Context,
                $"{title}: {message}", Android.Widget.ToastLength.Short)?.Show());

    public async Task ExportTextAsync(string suggestedName, string text)
    {
        var storage = Avalonia.Controls.TopLevel.GetTopLevel(_view)?.StorageProvider;
        if (storage is null) return;
        // DefaultExtension appends ".conf"; strip it from the suggested name so a
        // ".conf" filename doesn't become "name.conf.conf" in the save sheet.
        var baseName = suggestedName.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)
            ? suggestedName[..^5] : suggestedName;
        var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export WireGuard configuration",
            SuggestedFileName = baseName,
            DefaultExtension = "conf",
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        // Overwriting an existing (possibly longer) .conf: truncate first so no stale tail
        // survives — SAF 'w' mode isn't guaranteed to truncate on every DocumentsProvider.
        if (stream.CanSeek) stream.SetLength(0);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(text);
    }
}
