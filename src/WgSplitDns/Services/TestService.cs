using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WgSplitDns.Services;

public record TestResult(bool Success, string Message);

public static class TestService
{
    // Auto: resolve through the OS resolver path — this honors NRPT, so it shows what apps get.
    public static async Task<TestResult> ResolveAutoAsync(string host)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var ips = await Task.Run(() => QueryViaOsResolver(host));
            sw.Stop();
            if (ips.Count == 0) return new TestResult(false, "No A records returned");
            return new TestResult(true, $"{string.Join(", ", ips)} · via effective rules in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            return new TestResult(false, $"Resolution failed: {ex.Message}");
        }
    }

    // Direct: raw UDP query to a specific server, bypassing NRPT.
    public static async Task<TestResult> ResolveDirectAsync(string host, string server, string serverLabel)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var ips = await QueryUdpAsync(host, IPAddress.Parse(server));
            sw.Stop();
            if (ips.Count == 0) return new TestResult(false, $"No answer from {server} ({serverLabel})");
            return new TestResult(true, $"{string.Join(", ", ips)} · answered by {server} ({serverLabel}) in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            return new TestResult(false, $"Query to {server} failed: {ex.Message}");
        }
    }

    [DllImport("dnsapi", CharSet = CharSet.Unicode)]
    static extern int DnsQuery_W(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);

    [DllImport("dnsapi")]
    static extern void DnsRecordListFree(IntPtr records, int freeType);

    static List<string> QueryViaOsResolver(string host)
    {
        const ushort DnsTypeA = 1;
        var status = DnsQuery_W(host, DnsTypeA, 0, IntPtr.Zero, out var records, IntPtr.Zero);
        if (status != 0) throw new InvalidOperationException($"DNS status {status}");
        var result = new List<string>();
        try
        {
            // DNS_RECORD (x64): Next@0, Name@8, Type@16, DataLength@18, Flags@20, Ttl@24, Reserved@28, Data@32
            for (var p = records; p != IntPtr.Zero; p = Marshal.ReadIntPtr(p))
            {
                if ((ushort)Marshal.ReadInt16(p, 16) != DnsTypeA) continue;
                var ip = (uint)Marshal.ReadInt32(p, 32);
                result.Add(new IPAddress(ip).ToString());
            }
        }
        finally
        {
            DnsRecordListFree(records, 1);
        }
        return result;
    }

    static async Task<List<string>> QueryUdpAsync(string host, IPAddress server)
    {
        var id = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue);
        var query = BuildQuery(id, host);
        using var udp = new UdpClient(server.AddressFamily);
        udp.Connect(server, 53);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await udp.SendAsync(query);
            var receive = udp.ReceiveAsync();
            var done = await Task.WhenAny(receive, Task.Delay(2000));
            if (done != receive) continue;
            var response = (await receive).Buffer;
            var ips = ParseAnswers(response, id);
            if (ips is not null) return ips;
        }
        throw new TimeoutException("no response within 2 s");
    }

    static byte[] BuildQuery(ushort id, string host)
    {
        var ms = new MemoryStream();
        void U16(ushort v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        U16(id);
        U16(0x0100); // recursion desired
        U16(1); U16(0); U16(0); U16(0);
        foreach (var label in host.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0);
        U16(1); // A
        U16(1); // IN
        return ms.ToArray();
    }

    static List<string>? ParseAnswers(byte[] r, ushort expectedId)
    {
        if (r.Length < 12) return null;
        if ((ushort)((r[0] << 8) | r[1]) != expectedId) return null;
        int qd = (r[4] << 8) | r[5];
        int an = (r[6] << 8) | r[7];
        int pos = 12;
        for (int i = 0; i < qd; i++)
        {
            while (pos < r.Length && r[pos] != 0) pos += r[pos] + 1;
            pos += 5;
        }
        var ips = new List<string>();
        for (int i = 0; i < an && pos + 12 <= r.Length; i++)
        {
            pos += (r[pos] & 0xC0) == 0xC0 ? 2 : SkipName(r, pos);
            if (pos + 10 > r.Length) break;
            int type = (r[pos] << 8) | r[pos + 1];
            int dataLen = (r[pos + 8] << 8) | r[pos + 9];
            pos += 10;
            if (type == 1 && dataLen == 4 && pos + 4 <= r.Length)
                ips.Add($"{r[pos]}.{r[pos + 1]}.{r[pos + 2]}.{r[pos + 3]}");
            pos += dataLen;
        }
        return ips;
    }

    static int SkipName(byte[] r, int pos)
    {
        int start = pos;
        while (pos < r.Length && r[pos] != 0) pos += r[pos] + 1;
        return pos - start + 1;
    }
}
