using Avalonia.Data.Converters;

namespace SplitGuard.Views;

// Splits chip values so parts can be colored separately (ip vs /mask, host vs :port).
public static class Syntax
{
    public static readonly IValueConverter IpPart = new FuncValueConverter<string?, string>(s =>
    {
        if (string.IsNullOrEmpty(s)) return "";
        var i = s.IndexOf('/');
        return i < 0 ? s : s[..i];
    });

    public static readonly IValueConverter MaskPart = new FuncValueConverter<string?, string>(s =>
    {
        if (string.IsNullOrEmpty(s)) return "";
        var i = s.IndexOf('/');
        return i < 0 ? "" : s[i..];
    });

    public static readonly IValueConverter HostPart = new FuncValueConverter<string?, string>(s =>
    {
        if (string.IsNullOrEmpty(s)) return "";
        var i = s.LastIndexOf(':');
        return i <= 0 ? s : s[..i];
    });

    public static readonly IValueConverter PortPart = new FuncValueConverter<string?, string>(s =>
    {
        if (string.IsNullOrEmpty(s)) return "";
        var i = s.LastIndexOf(':');
        return i <= 0 ? "" : s[i..];
    });
}
