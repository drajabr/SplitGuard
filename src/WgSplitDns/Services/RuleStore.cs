using System.Security.Cryptography;
using System.Text.Json;
using WgSplitDns.Models;

namespace WgSplitDns.Services;

public class RuleStore
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WgSplitDns");
    static readonly string ConfigPath = Path.Combine(Dir, "config.json");
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts) ?? new AppConfig();
        }
        catch
        {
            // Corrupt config: start fresh rather than crash; the old file is preserved.
            try { File.Copy(ConfigPath, ConfigPath + ".bad", overwrite: true); } catch { }
        }
        return new AppConfig();
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(Dir);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOpts));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    public static string Protect(string base64Key)
    {
        var blob = ProtectedData.Protect(Convert.FromBase64String(base64Key), null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(blob);
    }

    public static string Unprotect(string protectedBase64)
    {
        var raw = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(raw);
    }
}
