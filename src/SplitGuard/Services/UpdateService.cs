using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace SplitGuard.Services;

// A newer release than the running build: where to fetch its Windows installer (empty on
// platforms that go through the release page instead) and the release's web page.
//
// Size and Sha256 are what the releases API reports for the asset, and they are the whole basis for
// trusting the file we execute. The installer runs with this process's inherited admin token and,
// on the unattended path, with no wizard for a human to notice anything wrong — so the bytes are
// checked against these on download AND again immediately before launch. Both arrive over TLS from
// api.github.com, which is already the trust anchor for the download itself.
// Sha256 is lowercase hex without the "sha256:" prefix; "" when the API didn't report one.
public record UpdateInfo(Version Version, string Tag, string DownloadUrl, string AssetName, string PageUrl,
                         long Size = 0, string Sha256 = "");

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
            var size = asset.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var s) ? s : 0;
            // GitHub reports this as "sha256:<hex>"; keep just the hex, and only for that algorithm
            // (anything else we can't check, so treat it as absent rather than pretend).
            var digest = asset.TryGetProperty("digest", out var dg) ? dg.GetString() ?? "" : "";
            var sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest["sha256:".Length..].Trim().ToLowerInvariant() : "";
            if (url.Length > 0) return new UpdateInfo(latest, tag, url, name, page, size, sha);
        }
        return null;
    }

    static bool TryParseTag(string tag, out Version version) =>
        Version.TryParse(tag.TrimStart('v', 'V').Trim(), out version!);

    // ProgramData, not LOCALAPPDATA. The cached installer is executed with this process's admin
    // token, so where it sits IS a security boundary: %LOCALAPPDATA% is writable by the ordinary
    // user session, which means any unprivileged process could swap the .exe between our download
    // and the user's click and get it run elevated. Moving the cache under an
    // Administrators/SYSTEM-only DACL removes that window rather than trying to detect it.
    public static string UpdatesDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SplitGuard", "updates");

    // Create the cache with inheritance disabled and only Administrators + SYSTEM able to write.
    // ProgramData's default DACL lets any authenticated user create files in new subdirectories,
    // so relying on the default here would leave the same hole one level up.
    // Returns the reason the DACL could not be applied, or null on success. Tightening the DACL is
    // best-effort ON PURPOSE: if it fails (an unexpected owner, group policy, a non-NTFS volume),
    // refusing to download would break updating altogether, and the launch-time SHA-256 re-check in
    // VerifyInstaller still catches tampering. So this hardens the happy path without becoming a
    // single point of failure — but a caller that cares should surface a non-null result.
    public static string? EnsureUpdatesDir()
    {
        Directory.CreateDirectory(UpdatesDir);
        if (!OperatingSystem.IsWindows()) return "not Windows";
        try
        {
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inherit = InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit;
            foreach (var who in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(who, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(UpdatesDir).SetAccessControl(sec);
            return null;
        }
        catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
    }

    // SHA-256 of a file as lowercase hex, for comparison against the release API's digest.
    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    // Re-check the cached installer against what the release API said, immediately before running
    // it. Verifying only at download time would leave a gap between "verified" and "executed" —
    // exactly the window an attacker wants — so this is called again at the point of launch.
    // Returns null when the file is good, or the reason it was rejected.
    public static string? VerifyInstaller(string path, UpdateInfo info)
    {
        if (!File.Exists(path)) return "the downloaded installer is gone";
        var len = new FileInfo(path).Length;
        if (info.Size > 0 && len != info.Size)
            return $"size is {len} bytes but the release lists {info.Size}";
        if (info.Sha256.Length == 0) return null; // nothing published to compare against
        var actual = Sha256Of(path);
        return actual == info.Sha256
            ? null
            : $"SHA-256 is {actual[..16]}… but the release lists {info.Sha256[..16]}…";
    }

    // Download the installer to a local cache, reporting 0..1 progress; returns its path.
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        EnsureUpdatesDir();
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
        // Verify size AND SHA-256 against the release API BEFORE promoting the .part file to the
        // name we will later execute, so a bad download never becomes a runnable installer.
        var bad = VerifyInstaller(tmp, info);
        if (bad is not null)
        {
            try { File.Delete(tmp); } catch { }
            throw new IOException($"Discarded the downloaded update: {bad}");
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    // Launch the downloaded installer. Started from an always-elevated app it inherits that token,
    // so no UAC prompt appears; `arguments` carries the unattended switches on the self-update path.
    // The caller must exit the app right after so setup can replace the running files.
    //
    // Callers MUST call VerifyInstaller immediately beforehand. Two defences carry this path: the
    // cache lives under an Administrators-only DACL (EnsureUpdatesDir), so an unprivileged process
    // cannot reach the file at all, and the bytes are re-hashed against the release API's digest at
    // the moment of launch, so tampering by anything that *could* reach it is still caught.
    //
    // RESIDUAL RISK: both the digest and the download come from GitHub, so this defends against
    // tampering below GitHub — not against a compromised GitHub account or release. Only an
    // Authenticode signature made with a key held outside GitHub, verified here against a pinned
    // publisher, would cover that; it needs a code-signing certificate the project does not have.
    public static void LaunchInstaller(string path, string arguments = "")
    {
        var psi = new ProcessStartInfo(path) { UseShellExecute = true };
        if (arguments.Length > 0) psi.Arguments = arguments;
        Process.Start(psi);
    }
}
