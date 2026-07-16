using Avalonia.Media;

namespace SplitGuard;

// One source of truth for accent hues: the global picker and per-card overrides
// both resolve a hue name to a color here. "mono" is neutral and tracks the theme.
public static class Accents
{
    // Hues tuned to the design mocks (docs/design/design-system.html): calmer, slightly
    // desaturated so accents read as chrome, not alarms.
    public static readonly (string Name, string Hex)[] Steps =
    {
        ("green",  "#3DAF7E"),
        ("red",    "#D76D64"),
        ("blue",   "#4A93CF"),
        ("purple", "#8E86C9"),
        ("mono",   ""),
    };

    // Accent used as TEXT on the light themes: the raw mid-tone hues measure ~2.8-3.4:1 on
    // bright surfaces (below WCAG AA), so text gets a darkened variant per hue — same idea as
    // the theme-keyed syntax palette. Dark themes keep the raw hue (>=4.6:1 there).
    public static string TextOnLight(string name) => name switch
    {
        "green"  => "#1E7A54", // 5.0:1 on the light surface
        "red"    => "#B04A42", // 4.4:1
        "blue"   => "#2F6E9E", // 4.5:1
        "purple" => "#5F5799", // 5.2:1
        _        => "#1A1A1A", // mono on light = near-black already
    };

    // Foreground that reads on top of an accent fill. The threshold is 135 (not the classic
    // 160): the mid-tone accents (green at luma ~140) fail AA with white text (~2.8:1) but
    // clear it comfortably with dark ink (>=5.2:1), so ink wins for anything mid or brighter.
    public static Color On(Color accent) =>
        0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B > 135
            ? Color.Parse("#1A1A1A")
            : Colors.White;
}
