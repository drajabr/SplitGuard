using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SplitGuard.Services;

// A newer release than the running build: where to fetch its Windows installer (empty on
// platforms that go through the release page instead) and the release's web page.
public record UpdateInfo(Version Version, string Tag, string DownloadUrl, string AssetName, string PageUrl);

// Self-update against the public GitHub releases of drajabr/SplitGuard: query the latest
// release, download its installer, then hand off to it. No auth — the API is public and the
// unauthenticated rate limit (60/hour) is far above one check a day.
public static class UpdateService
{
    const string LatestApi = "https://api.github.com/repos/drajabr/SplitGuard/releases/latest";

    // The running build as major.minor.build (drops the always-zero revision) so it compares
    // cleanly against a "vX.Y.Z" release tag.
    public static Version CurrentVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, v.Build);
        }
    }

    static HttpClient NewClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub rejects requests without a User-Agent.
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SplitGuard", CurrentVersion.ToString()));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    // The latest release, but only when it's newer than what's running. With
    // requireInstaller (Windows) it must also ship an .exe asset; without it (Android)
    // the release page is the destination. Returns null when up to date (or the latest
    // is a draft/prerelease).
    public static async Task<UpdateInfo?> CheckAsync(bool requireInstaller = true, CancellationToken ct = default)
    {
        using var http = NewClient();
        var json = await http.GetStringAsync(LatestApi, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!TryParseTag(tag, out var latest) || latest <= CurrentVersion) return null;
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        if (page.Length == 0) page = $"https://github.com/drajabr/SplitGuard/releases/tag/{tag}";

        if (!requireInstaller) return new UpdateInfo(latest, tag, "", "", page);

        if (!root.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            if (url.Length > 0) return new UpdateInfo(latest, tag, url, name, page);
        }
        return null;
    }

    static bool TryParseTag(string tag, out Version version) =>
        Version.TryParse(tag.TrimStart('v', 'V').Trim(), out version!);

    static string UpdatesDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SplitGuard", "updates");

    // Download the installer to a local cache, reporting 0..1 progress; returns its path.
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(UpdatesDir);
        var path = Path.Combine(UpdatesDir, info.AssetName);
        using var http = NewClient();
        using var resp = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        var tmp = path + ".part";
        await using (var dst = File.Create(tmp))
        {
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    // Launch the downloaded installer; it self-elevates via its own manifest (UAC). The
    // caller must exit the app right after so the setup can replace the running files.
    public static void LaunchInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
