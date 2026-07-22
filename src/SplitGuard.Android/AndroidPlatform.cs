using SplitGuard.Services;

namespace SplitGuard.Droid;

// Android head platform. Phase 2 (UI bring-up): the engine and split-DNS backends are
// inert placeholders — the app runs in DemoMode. Phase 3 swaps in the VpnService-backed
// engine; Phase 4 the DNS forwarder.
public class AndroidPlatform : IPlatform
{
    public string ConfigDirectory { get; } =
        Android.App.Application.Context.FilesDir!.AbsolutePath;

    // App-private storage + Android file-based encryption is the at-rest story; a
    // Keystore-backed protector is a post-0.5.0 hardening item.
    public IKeyProtector KeyProtector { get; } = new PassThroughProtector();

    public IQrScanner? CreateQrScanner() => new AndroidQrScanner();
    public ITunnelEngine CreateEngine() => new AndroidTunnelEngine();
    public ISplitDnsService CreateSplitDns() => AndroidSplitDnsService.Instance;
    public IExternalTunnels? CreateExternalTunnels() => null; // no external-client concept

    public bool SupportsStartup => false;          // no logon task / UAC on Android
    public bool SupportsInstallerUpdate => false;  // updates surface the GitHub release page
    public bool SupportsQrScan => true;
    public bool SupportsBootStart => true;   // BootReceiver reconnects the last-on tunnel

    public void SetStartOnBoot(bool on) { }
    public void SetSkipUacLaunch(bool on) { }

    public void OpenUrl(string url)
    {
        var intent = new Android.Content.Intent(Android.Content.Intent.ActionView,
            Android.Net.Uri.Parse(url));
        intent.AddFlags(Android.Content.ActivityFlags.NewTask);
        Android.App.Application.Context.StartActivity(intent);
    }

    public void SetSystemBarColor(uint argb, bool lightBackground) =>
        MainActivity.Current?.SetSystemBars(unchecked((int)argb), lightBackground);

    sealed class NullSplitDns : ISplitDnsService
    {
        public bool IsPolicyManaged => false;
        public void ApplyDomain(string t, string p, string d, string s) { }
        public void ApplyPeerRules(string t, string p, IEnumerable<string> d, string s) { }
        public void RemovePeerRules(string t, string p) { }
        public void RemoveByTunnel(string t) { }
        public void SetCatchAll(string[] servers) { }
        public void RemoveCatchAll() { }
        public List<NrptRule> GetTaggedRules() => new();
        public void RemoveTagged(IEnumerable<string> ids) { }
        public void RemoveAllTagged() { }
    }
}
