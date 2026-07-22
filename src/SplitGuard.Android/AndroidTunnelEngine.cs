using Android.Content;
using Com.Wireguard.Android.Backend; // WgGo
using SplitGuard.Models;
using SplitGuard.Services;

namespace SplitGuard.Droid;

// ITunnelEngine over SgVpnService + wireguard-go. Single tunnel: connecting a second
// tunnel replaces the first (the service fires Stopped for the old one, which the UI
// flips off). Stats poll wgGetConfig every 2s while a tunnel is up.
public class AndroidTunnelEngine : ITunnelEngine
{
    public event Action<string, TunnelStats>? StatsUpdated;
    public event Action<string>? FailoverChanged { add { } remove { } } // no failover on Android
    public event Action<string>? Disconnected;

    readonly System.Threading.Timer _timer;
    readonly Dictionary<string, (ulong Tx, ulong Rx, DateTime At)> _prev = new();
    readonly object _prevGate = new();
    readonly object _connectGate = new();       // serialize connect requests
    readonly Action<string, string?> _onState;  // captured so Dispose can unsubscribe
    readonly Action<string> _onStopped;

    volatile string? _active;   // the tunnel we've confirmed up
    volatile string? _desired;  // the tunnel the user last asked to connect
    DateTime _connectedAt;

    public AndroidTunnelEngine()
    {
        _timer = new System.Threading.Timer(_ => Poll(), null, 2000, 2000);
        _onState = (name, error) =>
        {
            // Only accept an establish for the tunnel the user still wants — a slow establish
            // that lands after a timeout/disconnect must not mark us connected.
            if (error is null && name == _desired) { _active = name; _connectedAt = DateTime.UtcNow; }
        };
        _onStopped = name =>
        {
            if (_active == name) _active = null;
            lock (_prevGate) _prev.Clear();
            Disconnected?.Invoke(name);
        };
        SgVpnService.StateChanged += _onState;
        SgVpnService.Stopped += _onStopped;
    }

    public bool IsConnected(string name) => _active == name;

    // Peer display names by public key, for the live notification lines.
    volatile Dictionary<string, string> _peerNames = new();

    public void Connect(TunnelConfig config)
    {
        var names = new Dictionary<string, string>();
        for (int i = 0; i < config.Peers.Count; i++)
        {
            var p = config.Peers[i];
            if (!string.IsNullOrWhiteSpace(p.PublicKey))
                names[p.PublicKey] = string.IsNullOrWhiteSpace(p.Name) ? $"peer {i + 1}" : p.Name!.Trim();
        }
        _peerNames = names;
        var ctx = Android.App.Application.Context;
        // Consent must already be granted (MainActivity runs the prepare flow before
        // flipping the toggle); a null prepare intent means we're clear.
        if (Android.Net.VpnService.Prepare(ctx) is not null)
            throw new InvalidOperationException("VPN permission not granted — toggle again after accepting the consent dialog");

        // One connect in flight at a time: a second toggle waits rather than racing the
        // static PendingConfig handoff (which could null it out under the first).
        lock (_connectGate)
        {
            _desired = config.Name;
            string? error = null;
            using var done = new ManualResetEventSlim();
            void OnState(string name, string? err)
            {
                if (name != config.Name) return;
                error = err;
                done.Set();
            }
            SgVpnService.StateChanged += OnState;
            try
            {
                SgVpnService.PendingConfig = config;
                var intent = new Intent(ctx, typeof(SgVpnService)).SetAction(SgVpnService.ActionConnect);
                if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
                    ctx.StartForegroundService(intent);
                else
                    ctx.StartService(intent);
                if (!done.Wait(TimeSpan.FromSeconds(15)))
                {
                    // Give up: a late establish must not linger. Stop wanting it, then tear
                    // down anything that comes up.
                    if (_desired == config.Name) _desired = null;
                    Dispatch(SgVpnService.ActionDisconnect);
                    throw new InvalidOperationException("VPN service did not start in time");
                }
                if (error is not null) throw new InvalidOperationException(error);
            }
            finally { SgVpnService.StateChanged -= OnState; }
        }
        // The replaced tunnel (if any) is reported via the service's Stopped -> _onStopped,
        // so there's no separate re-fire here.
    }

    public void Disconnect(string name)
    {
        // Always dispatch: _active isn't set until establish completes, so gating on it would
        // drop a disconnect issued during the connect window and orphan a live tunnel. The
        // service no-ops if nothing is running.
        if (_desired == name) _desired = null;
        Dispatch(SgVpnService.ActionDisconnect);
    }

    public void DisconnectAll()
    {
        _desired = null;
        Dispatch(SgVpnService.ActionDisconnect);
    }

    static void Dispatch(string action)
    {
        var ctx = Android.App.Application.Context;
        ctx.StartService(new Intent(ctx, typeof(SgVpnService)).SetAction(action));
    }

    void Poll()
    {
        var name = _active;
        var handle = SgVpnService.Handle;
        if (name is null || handle < 0) return;
        string uapi;
        try { uapi = WgGo.GetConfig(handle) ?? ""; }
        catch { return; }
        var now = DateTime.UtcNow;
        var per = new Dictionary<string, PeerLive>();
        lock (_prevGate)
        {
            foreach (var (key, s) in Uapi.ParseStats(uapi))
            {
                double up = 0, down = 0;
                if (_prev.TryGetValue(key, out var prev))
                {
                    var dt = Math.Max(0.5, (now - prev.At).TotalSeconds);
                    up = Math.Max(0, (double)(s.Tx - prev.Tx)) / dt;
                    down = Math.Max(0, (double)(s.Rx - prev.Rx)) / dt;
                }
                _prev[key] = (s.Tx, s.Rx, now);
                // Health = handshake freshness (same thresholds as the desktop engine),
                // with a grace window for the first handshake after connect.
                var fresh = s.Handshake is not null && now - s.Handshake.Value < TimeSpan.FromSeconds(180);
                var inGrace = now - _connectedAt < TimeSpan.FromSeconds(60);
                per[key] = new PeerLive(up, down, s.Handshake,
                    null, null, fresh || inGrace, null, null, null, s.Tx, s.Rx);
            }
        }
        if (per.Count > 0) StatsUpdated?.Invoke(name, new TunnelStats(per));

        // Keep the foreground notification's live line + expanded per-peer detail fresh
        // (UpdateNotification de-duplicates, so unchanged text costs nothing).
        if (per.Count > 0 && SgVpnService.Instance is { } svc)
        {
            double up = 0, down = 0;
            var lines = new List<string>();
            var names = _peerNames;
            foreach (var (key, s) in per)
            {
                up += s.UpBps; down += s.DownBps;
                var label = names.TryGetValue(key, out var n) ? n : key[..Math.Min(8, key.Length)];
                var hs = s.Handshake is null ? "no handshake" : ViewModels.Format.Ago(s.Handshake);
                lines.Add($"{label} — {hs} · ↑ {ViewModels.Format.Bytes(s.TotalTx)} ↓ {ViewModels.Format.Bytes(s.TotalRx)}");
            }
            svc.UpdateNotification(
                $"↑ {ViewModels.Format.Rate(up)}   ↓ {ViewModels.Format.Rate(down)}",
                string.Join("\n", lines));
        }
    }

    public void Dispose()
    {
        SgVpnService.StateChanged -= _onState;
        SgVpnService.Stopped -= _onStopped;
        _timer.Dispose();
    }
}
