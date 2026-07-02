using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Management.Infrastructure;
using Microsoft.Win32;

namespace WgSplitDns.Services;

public record NrptRule(string Id, string[] Namespaces, string[] Servers);

// Sole owner of NRPT state. Only rules tagged WG-SPLIT-DNS are ever touched.
// CIM is the primary backend; PowerShell cmdlets are the fallback.
public class NrptService
{
    public const string Tag = "WG-SPLIT-DNS";
    const string CatchAllId = "WGSDNS|catchall";
    const string CimNamespace = @"root\StandardCimv2";
    const string CimClass = "MSFT_DNSClientNrptRule";

    [DllImport("dnsapi")] static extern bool DnsFlushResolverCache();

    INrptBackend? _backend;

    INrptBackend Backend => _backend ??= SelectBackend();

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

    public static string DomainToNamespace(string domain) =>
        domain.StartsWith("*.") ? domain[1..] : domain; // "*.x" → ".x" (subdomain-inclusive), bare stays exact

    public static string RuleId(string tunnelName, string peerPublicKey, string domain) =>
        $"WGSDNS|{tunnelName}|{Short(peerPublicKey)}|{domain}";

    static string Short(string key) => key.Length > 8 ? key[..8] : key;

    public void ApplyDomain(string tunnelName, string peerPublicKey, string domain, string dnsServer)
    {
        Backend.Add(RuleId(tunnelName, peerPublicKey, domain), new[] { DomainToNamespace(domain) }, new[] { dnsServer });
        Flush();
    }

    public void RemoveDomain(string tunnelName, string peerPublicKey, string domain)
    {
        Backend.Remove(RuleId(tunnelName, peerPublicKey, domain));
        Flush();
    }

    public void ApplyPeerRules(string tunnelName, string peerPublicKey, IEnumerable<string> domains, string dnsServer)
    {
        foreach (var d in domains)
            Backend.Add(RuleId(tunnelName, peerPublicKey, d), new[] { DomainToNamespace(d) }, new[] { dnsServer });
        Flush();
    }

    public void RemovePeerRules(string tunnelName, string peerPublicKey)
    {
        var prefix = $"WGSDNS|{tunnelName}|{Short(peerPublicKey)}|";
        foreach (var rule in Backend.GetTagged().Where(r => r.Id.StartsWith(prefix)))
            Backend.Remove(rule.Id);
        Flush();
    }

    public void SetCatchAll(string[] orderedServers)
    {
        Backend.Remove(CatchAllId);
        if (orderedServers.Length > 0)
            Backend.Add(CatchAllId, new[] { "." }, orderedServers);
        Flush();
    }

    public void RemoveCatchAll()
    {
        Backend.Remove(CatchAllId);
        Flush();
    }

    public List<NrptRule> GetTaggedRules() => Backend.GetTagged();

    public void RemoveTagged(IEnumerable<string> ids)
    {
        foreach (var id in ids) Backend.Remove(id);
        Flush();
    }

    public void RemoveAllTagged()
    {
        foreach (var rule in Backend.GetTagged()) Backend.Remove(rule.Id);
        Flush();
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
        List<NrptRule> GetTagged();
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
            _session.InvokeMethod(CimNamespace, CimClass, "Add", parameters);
        }

        public void Remove(string id)
        {
            foreach (var instance in Query().Where(i => Prop<string>(i, "DisplayName") == id))
                _session.DeleteInstance(instance);
        }

        public List<NrptRule> GetTagged() =>
            Query()
                .Where(i => Prop<string>(i, "Comment") == NrptService.Tag)
                .Select(i => new NrptRule(
                    Prop<string>(i, "DisplayName") ?? "",
                    Prop<string[]>(i, "Namespace") ?? Array.Empty<string>(),
                    Prop<string[]>(i, "NameServers") ?? Array.Empty<string>()))
                .ToList();

        List<CimInstance> Query() =>
            _session.QueryInstances(CimNamespace, "WQL", $"SELECT * FROM {CimClass}").ToList();

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
            Run($"Get-DnsClientNrptRule | Where-Object {{ $_.Comment -eq {Quote(NrptService.Tag)} -and $_.DisplayName -eq {Quote(id)} }} | Remove-DnsClientNrptRule -Force");
        }

        public List<NrptRule> GetTagged()
        {
            var json = Run($"Get-DnsClientNrptRule | Where-Object Comment -eq {Quote(NrptService.Tag)} | Select-Object DisplayName,Namespace,NameServers | ConvertTo-Json -Depth 3");
            if (string.IsNullOrWhiteSpace(json)) return new List<NrptRule>();
            if (!json.TrimStart().StartsWith('[')) json = $"[{json}]";
            var rows = JsonSerializer.Deserialize<List<PsRule>>(json) ?? new List<PsRule>();
            return rows.Select(r => new NrptRule(r.DisplayName ?? "", r.Namespace ?? Array.Empty<string>(), r.NameServers ?? Array.Empty<string>())).ToList();
        }

        class PsRule
        {
            public string? DisplayName { get; set; }
            public string[]? Namespace { get; set; }
            public string[]? NameServers { get; set; }
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
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"NRPT command failed: {error}");
            return output;
        }
    }
}
