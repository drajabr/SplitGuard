using Avalonia.Media;

namespace SplitGuard;

// One source of truth for accent hues: the global picker and per-card overrides
// both resolve a hue name to a color here. "mono" is neutral and tracks the theme.
public static class Accents
{
    public static readonly (string Name, string Hex)[] Steps =
    {
        ("red",    "#E03B3B"),
        ("amber",  "#E0920C"),
        ("green",  "#2FA84F"),
        ("blue",   "#2F7FE4"),
        ("violet", "#8A5CF0"),
        ("mono",   ""),
    };

    // Resolve a hue name to a color; unknown names fall back to blue.
    // "mono" resolves to near-white on dark themes and near-black on light.
    public static Color Resolve(string name, bool isDark)
    {
        foreach (var (n, hex) in Steps)
            if (n == name)
                return hex.Length > 0 ? Color.Parse(hex)
                     : (isDark ? Color.Parse("#FFFFFF") : Color.Parse("#1A1A1A"));
        return Color.Parse("#2F7FE4");
    }

    public static bool IsMono(string name) => name == "mono";

    // Next hue in the cycle (used when cycling a card's accent).
    public static string Next(string? name)
    {
        var i = System.Array.FindIndex(Steps, s => s.Name == name);
        return Steps[(i + 1) % Steps.Length].Name;
    }
}
