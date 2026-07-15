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
        ("green",  "#3DAF7E"),
        ("red",    "#D76D64"),
        ("blue",   "#4A93CF"),
        ("purple", "#8E86C9"),
        ("mono",   ""),
    };

    // Foreground that reads on top of an accent fill: dark text on light accents
    // (e.g. mono-on-dark resolves to white), white on everything else.
    public static Color On(Color accent) =>
        0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B > 160
            ? Color.Parse("#1A1A1A")
            : Colors.White;
}
