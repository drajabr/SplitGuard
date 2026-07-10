using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SplitGuard.Views;

// Single source of truth for the syntax palette. Fixed, accent-independent, used
// identically by fields, the collapsed description, and the raw editor.
public static class Syntax
{
    public const string Ip = "#4098D7";
    public const string Domain = "#58A65C";
    public const string Key = "#9A6FD0";
    public const string Num = "#C77E16";
    public const string Prop = "#7F8896";   // config property names / section headers

    public static readonly IBrush IpBrush = SolidColorBrush.Parse(Ip);
    public static readonly IBrush DomainBrush = SolidColorBrush.Parse(Domain);

    // Splits chip values so parts can be colored separately (ip vs /mask).
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

    // Failover role badge: highlights the "active" member of an overlap group.
    public static readonly IValueConverter IsActive = new FuncValueConverter<string?, bool>(s => s == "active");

    public static readonly IValueConverter Not = new FuncValueConverter<bool, bool>(b => !b);
}
