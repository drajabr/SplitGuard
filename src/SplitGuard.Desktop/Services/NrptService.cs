using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Management.Infrastructure;
using Microsoft.Win32;

namespace SplitGuard.Services;

// Sole owner of NRPT state. Only rules tagged WG-SPLIT-DNS are ever touched.
// CIM is the primary backend; PowerShell cmdlets are the fallback.
public class NrptService : ISplitDnsService
{
    public const string Tag = "WG-SPLIT-DNS";
    const string CatchAllId = "WGSDNS|catchall";
    // The NRPT CIM model lives in the DNS-client namespace. PS_DnsClientNrptRule is the
    // cmdlet-method class (Add/Get/Remove/Set) that mirrors the *-DnsClientNrptRule cmdlets.
    // NRPT rules are NOT queryable as plain instances of DnsClientNrptRule — "SELECT * FROM
    // DnsClientNrptRule" returns nothing on Windows, which silently turned Get/Remove into
    // no-ops while Add kept working (rules then piled up unbounded — the 7550-rule incident).
    // So ALL operations go through PS_DnsClientNrptRule's methods: Get enumerates and returns
    // each rule's real Name GUID; Remove deletes by that GUID.
    const string CimNamespace = @"root\Microsoft\Windows\DNS";
    const string CimCmdletClass = "PS_DnsClientNrptRule";

    [DllImport("dnsapi")] static extern bool DnsFlushResolverCache();

    // Mutations arrive from background threads (connect/disconnect/save); serialize them
    // so concurrent CIM/PowerShell calls never interleave.
    readonly object _gate = new();
    INrptBackend? _backend;

    // The demo harness must leave system DNS policy untouched, whatever code path fires.
    INrptBackend Backend => _backend ??= RuleStore.DemoMode ? new NullBackend() : SelectBackend();

    INrptBackend SelectBackend()
    {
        try
        {
            var cim = new CimBackend();
            cim.GetTagged(); // probe
            return cim;
        }
        catch
        {
            return new PowerShellBackend();
        }
    }

    // Rule identity/namespace forms live in the shared SplitDnsRules so every backend
    // (and the reconcile pass) agrees on ids.
    public static string DomainToNamespace(string domain) => SplitDnsRules.DomainToNamespace(domain);
    public static string RuleId(string tunnelName, string peerPublicKey, string domain) =>
        SplitDnsRules.RuleId(tunnelName, peerPublicKey, domain);
    static string Short(string key) => SplitDnsRules.Short(key);

    public bool IsPolicyManaged => IsGpoNrptActive();

    public void ApplyDomain(string tunnelName, string peerPublicKey, string domain, string dnsServer)
    {
        lock (_gate)
        {
            Backend.Add(RuleId(tunnelName, peerPublicKey, domain), new[] { DomainToNamespace(domain) }, new[] { dnsServer });
            Flush();
        }
    }

    public void ApplyPeerRules(string tunnelName, string peerPublicKey, IEnumerable<string> domains, string dnsServer)
    {
        lock (_gate)
        {
            foreach (var d in domains)
                Backend.Add(RuleId(tunnelName, peerPublicKey, d), new[] { DomainToNamespace(d) }, new[] { dnsServer });
            Flush();
        }
    }

    public void RemovePeerRules(string tunnelName, string peerPublicKey)
    {
        lock (_gate)
        {
            var prefix = $"WGSDNS|{tunnelName}|{Short(peerPublicKey)}|";
            foreach (var rule in Backend.GetTagged().Where(r => r.Id.StartsWith(prefix)))
                Backend.Remove(rule.Id);
            Flush();
        }
    }

    // Remove every tagged rule belonging to a card (all peers/roles under one tunnel key).
    public void RemoveByTunnel(string tunnelName)
    {
        lock (_gate)
        {
            var prefix = $"WGSDNS|{tunnelName}|";
            foreach (var rule in Backend.GetTagged().Where(r => r.Id.StartsWith(prefix)))
                Backend.Remove(rule.Id);
            Flush();
        }
    }

    public void SetCatchAll(string[] orderedServers)
    {
        lock (_gate)
        {
            Backend.Remove(CatchAllId);
            if (orderedServers.Length > 0)
                Backend.Add(CatchAllId, new[] { "." }, orderedServers);
            Flush();
        }
    }

    public void RemoveCatchAll()
    {
        lock (_gate)
        {
            Backend.Remove(CatchAllId);
            Flush();
        }
    }

    public List<NrptRule> GetTaggedRules() { lock (_gate) return Backend.GetTagged(); }

    public void RemoveTagged(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            foreach (var id in ids) Backend.Remove(id);
            Flush();
        }
    }

    // Bulk purge of every WG-SPLIT-DNS rule — the upgrade/crash-recovery path that must clear a
    // runaway backlog (thousands of strays) quickly and completely. Delegated to the backend so it
    // is one enumeration + remove-each (O(n)), per-rule isolated and best-effort: unlike the
    // targeted Remove(id), a single stubborn rule must NOT abort the sweep or the backlog persists.
    public void RemoveAllTagged()
    {
        lock (_gate)
        {
            Backend.RemoveAllTagged();
            Flush();
        }
    }

    public static bool IsGpoNrptActive()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\DnsPolicyConfig");
            return key is not null && key.GetSubKeyNames().Length > 0;
        }
        catch
        {
            return false;
        }
    }

    static void Flush()
    {
        try { DnsFlushResolverCache(); } catch { }
    }

    interface INrptBackend
    {
        void Add(string id, string[] namespaces, string[] servers);
        void Remove(string id);
        void RemoveAllTagged();
        List<NrptRule> GetTagged();
    }

    class NullBackend : INrptBackend
    {
        public void Add(string id, string[] namespaces, string[] servers) { }
        public void Remove(string id) { }
        public void RemoveAllTagged() { }
        public List<NrptRule> GetTagged() => new();
    }

    class CimBackend : INrptBackend
    {
        readonly CimSession _session = CimSession.Create(null);

        public void Add(string id, string[] namespaces, string[] servers)
        {
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("Namespace", namespaces, CimType.StringArray, CimFlags.None),
                CimMethodParameter.Create("NameServers", servers, CimType.StringArray, CimFlags.None),
                CimMethodParameter.Create("Comment", NrptService.Tag, CimType.String, CimFlags.None),
                CimMethodParameter.Create("DisplayName", id, CimType.String, CimFlags.None),
            };
            var result = _session.InvokeMethod(CimNamespace, CimCmdletClass, "Add", parameters);
            // A non-zero ReturnValue is a SILENT failure (no exception): surface it so the
            // caller un-tracks the rule and the self-heal pass retries it.
            if (result?.ReturnValue?.Value is uint rv && rv != 0)
                throw new InvalidOperationException($"NRPT Add returned {rv} for {id}");
        }

        public void Remove(string id)
        {
            // Rules that share a DisplayName pile up as separate instances, each with its own
            // Name GUID — the store's real primary key. Delete every match by GUID.
            foreach (var inst in Enumerate().Where(i => Prop<string>(i, "DisplayName") == id))
            {
                var name = Prop<string>(inst, "Name");
                if (string.IsNullOrEmpty(name)) continue;
                var p = new CimMethodParametersCollection
                {
                    CimMethodParameter.Create("Name", name, CimType.String, CimFlags.None),
                    CimMethodParameter.Create("Force", true, CimType.Boolean, CimFlags.None),
                };
                var r = _session.InvokeMethod(CimNamespace, CimCmdletClass, "Remove", p);
                if (r?.ReturnValue?.Value is uint rv && rv != 0)
                    throw new InvalidOperationException($"NRPT Remove returned {rv} for {id} ({name})");
            }
            // A removal that silently leaves instances behind turns every retry loop above us
            // into an ADD loop — rules then accumulate without bound (the 7550-rule incident).
            // Verify the id is actually gone and fail loudly if not.
            if (Enumerate().Any(i => Prop<string>(i, "DisplayName") == id))
                throw new InvalidOperationException($"NRPT rule {id} survived removal");
        }

        // One enumeration, then delete every tagged rule by its GUID — O(n), not the O(n²) of
        // looping Remove(id) (which re-enumerates twice per call). Per-rule isolated and does NOT
        // verify/throw: this is the runaway-backlog purge, so it must drain as much as it can even
        // if a stray refuses to go, rather than abort on the first one.
        public void RemoveAllTagged()
        {
            foreach (var inst in Enumerate().Where(i => Prop<string>(i, "Comment") == NrptService.Tag))
            {
                var name = Prop<string>(inst, "Name");
                if (string.IsNullOrEmpty(name)) continue;
                try
                {
                    var p = new CimMethodParametersCollection
                    {
                        CimMethodParameter.Create("Name", name, CimType.String, CimFlags.None),
                        CimMethodParameter.Create("Force", true, CimType.Boolean, CimFlags.None),
                    };
                    _session.InvokeMethod(CimNamespace, CimCmdletClass, "Remove", p);
                }
                catch { }
            }
        }

        public List<NrptRule> GetTagged() =>
            Enumerate()
                .Where(i => Prop<string>(i, "Comment") == NrptService.Tag)
                .Select(i => new NrptRule(
                    Prop<string>(i, "DisplayName") ?? "",
                    Prop<string[]>(i, "Namespace") ?? Array.Empty<string>(),
                    Prop<string[]>(i, "NameServers") ?? Array.Empty<string>()))
                .ToList();

        // Enumerate via the cmdlet-method Get (mirrors Get-DnsClientNrptRule): a plain WQL
        // "SELECT * FROM DnsClientNrptRule" returns nothing on Windows. Each returned instance
        // carries Name (GUID), DisplayName, Comment, Namespace, NameServers.
        List<CimInstance> Enumerate()
        {
            var result = _session.InvokeMethod(CimNamespace, CimCmdletClass, "Get", new CimMethodParametersCollection());
            if (result is null) return new();
            object? output = null;
            try { output = result.OutParameters["cmdletOutput"]?.Value; } catch { }
            return output switch
            {
                IEnumerable<CimInstance> many => many.ToList(),
                CimInstance one => new List<CimInstance> { one },
                _ => new List<CimInstance>(),
            };
        }

        static T? Prop<T>(CimInstance instance, string name)
        {
            var value = instance.CimInstanceProperties[name]?.Value;
            return value is T t ? t : default;
        }
    }

    class PowerShellBackend : INrptBackend
    {
        public void Add(string id, string[] namespaces, string[] servers)
        {
            var ns = string.Join(",", namespaces.Select(Quote));
            var srv = string.Join(",", servers.Select(Quote));
            Run($"Add-DnsClientNrptRule -Namespace {ns} -NameServers {srv} -Comment {Quote(NrptService.Tag)} -DisplayName {Quote(id)}");
        }

        public void Remove(string id)
        {
            // The trailing check makes a silent no-op removal (pipeline mismatch, access)
            // exit non-zero, which Run turns into an exception — see CimBackend.Remove.
            Run($"Get-DnsClientNrptRule | Where-Object {{ $_.Comment -eq {Quote(NrptService.Tag)} -and $_.DisplayName -eq {Quote(id)} }} | Remove-DnsClientNrptRule -Force; " +
                $"if (Get-DnsClientNrptRule | Where-Object {{ $_.Comment -eq {Quote(NrptService.Tag)} -and $_.DisplayName -eq {Quote(id)} }}) {{ exit 1 }}");
        }

        // Bulk purge in a single pipeline — one powershell.exe drains the whole backlog at once,
        // best-effort (no survivor check): the upgrade/recovery sweep must not abort on a stray.
        public void RemoveAllTagged() =>
            Run($"Get-DnsClientNrptRule | Where-Object Comment -eq {Quote(NrptService.Tag)} | Remove-DnsClientNrptRule -Force");

        public List<NrptRule> GetTagged()
        {
            var json = Run($"Get-DnsClientNrptRule | Where-Object Comment -eq {Quote(NrptService.Tag)} | Select-Object DisplayName,Namespace,NameServers | ConvertTo-Json -Depth 3");
            if (string.IsNullOrWhiteSpace(json)) return new List<NrptRule>();
            if (!json.TrimStart().StartsWith('[')) json = $"[{json}]";
            var result = new List<NrptRule>();
            using var doc = JsonDocument.Parse(json);
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                result.Add(new NrptRule(
                    StringOf(row, "DisplayName"),
                    ArrayOf(row, "Namespace"),
                    ArrayOf(row, "NameServers")));
            }
            return result;
        }

        static string StringOf(JsonElement row, string name) =>
            row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

        // ConvertTo-Json collapses single-element arrays to a bare string — accept both.
        static string[] ArrayOf(JsonElement row, string name)
        {
            if (!row.TryGetProperty(name, out var v)) return Array.Empty<string>();
            return v.ValueKind switch
            {
                JsonValueKind.String => new[] { v.GetString()! },
                JsonValueKind.Array => v.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!).ToArray(),
                _ => Array.Empty<string>(),
            };
        }

        static string Quote(string s) => $"'{s.Replace("'", "''")}'";

        static string Run(string command)
        {
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command {command}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi)!;
            // Drain stderr concurrently: reading the two pipes sequentially deadlocks
            // when the child fills the un-read one first.
            var errorTask = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(30000))
            {
                // A hung powershell.exe would otherwise leak holding the pipeline and the
                // ExitCode read below would throw a misleading InvalidOperationException.
                try { process.Kill(); } catch { }
                throw new TimeoutException("NRPT command timed out after 30s");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"NRPT command failed: {errorTask.Result}");
            return output;
        }
    }
}
