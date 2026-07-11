using Avalonia.Media;

namespace SplitGuard;

// One source of truth for accent hues: the global picker and per-card overrides
// both resolve a hue name to a color here. "mono" is neutral and tracks the theme.
public static class Accents
{
    // Hues tuned to the design mocks (docs/design/ui-mocks.html): calmer, slightly
    // desaturated so accents read as chrome, not alarms.
    public static readonly (string Name, string Hex)[] Steps =
    {
        ("green", "#3DAF7E"),
        ("red",   "#D76D64"),
        ("blue",  "#4A93CF"),
        ("mono",  ""),
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

    // Foreground that reads on top of an accent fill: dark text on light accents
    // (e.g. mono-on-dark resolves to white), white on everything else.
    public static Color On(Color accent) =>
        0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B > 160
            ? Color.Parse("#1A1A1A")
            : Colors.White;

    // Next hue in the cycle (used when cycling a card's accent).
    public static string Next(string? name)
    {
        var i = System.Array.FindIndex(Steps, s => s.Name == name);
        return Steps[(i + 1) % Steps.Length].Name;
    }
}
