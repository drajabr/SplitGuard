using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SplitGuard.Views;

// Composes the app icon from channel-encoded templates (A=silhouette, R=dragon,
// G=tick circle, B=tick check) so the icon always carries the current accent color.
public static class AppIcons
{
    static readonly int[] IcoSizes = { 16, 24, 32, 48, 64, 128, 256 };
    static readonly Color TickGreen = Color.FromRgb(0x2E, 0x7D, 0x32);
    // The icon is the dragon alone (no filled background): enlarge it to nearly fill the
    // tile, then give it a thick outline so it reads on any taskbar/tray background.
    const double DragonScale = 1.75;      // how much the dragon fills the tile (>1 grows it)
    const double OutlineFrac = 0.05;      // outline thickness as a fraction of the tile size
    static readonly Color OutlineColor = Color.FromRgb(0xFF, 0xFF, 0xFF); // white keyline
    static readonly Dictionary<uint, (WindowIcon Idle, WindowIcon Active, Bitmap Logo)> Cache = new();

    public static (WindowIcon Idle, WindowIcon Active, Bitmap Logo) Get(Color accent)
    {
        var key = accent.ToUInt32();
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var idle = new List<byte[]>();
        var active = new List<byte[]>();
        foreach (var size in IcoSizes)
        {
            idle.Add(Compose(size, accent, tick: false));
            active.Add(Compose(size, accent, tick: true));
        }
        var result = (
            new WindowIcon(new MemoryStream(BuildIco(idle))),
            new WindowIcon(new MemoryStream(BuildIco(active))),
            new Bitmap(new MemoryStream(idle[4]))); // 64px for the header logo
        Cache[key] = result;
        return result;
    }

    static byte[] Compose(int size, Color accent, bool tick)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://SplitGuard/Assets/icon-template-{size}.png"));
        using var template = WriteableBitmap.Decode(stream);
        var output = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var src = template.Lock())
        using (var dst = output.Lock())
        {
            var srcBytes = new byte[src.RowBytes * size];
            Marshal.Copy(src.Address, srcBytes, 0, srcBytes.Length);
            var dstBytes = new byte[dst.RowBytes * size];
            bool rgba = src.Format == PixelFormat.Rgba8888;

            // Recover the raw masks. The icon is now the dragon ALONE (no filled
            // background): the dragon channel is the shape, enlarged to nearly fill the
            // tile, with a dilated outline behind it. The tick keeps its own disc.
            var dragonMask = new double[size * size];
            var circleMask = new double[size * size];
            var checkMask = new double[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int o = y * src.RowBytes + x * 4;
                    int d = srcBytes[o + (rgba ? 0 : 2)];
                    int ci = srcBytes[o + 1];
                    int ck = srcBytes[o + (rgba ? 2 : 0)];
                    int a = srcBytes[o + 3];
                    if (a > 0 && a < 255) { d = d * 255 / a; ci = ci * 255 / a; ck = ck * 255 / a; }
                    int i = y * size + x;
                    dragonMask[i] = Math.Min(255, d);
                    circleMask[i] = Math.Min(255, ci);
                    checkMask[i] = Math.Min(255, ck);
                }
            double c = (size - 1) / 2.0;

            // Scaled dragon body, then a dilated copy for the outline ring.
            var body = new double[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    body[y * size + x] = SampleMask(dragonMask, size,
                        c + (x - c) / DragonScale, c + (y - c) / DragonScale);
            int outline = Math.Max(1, (int)Math.Round(size * OutlineFrac));
            var expanded = Dilate(body, size, outline);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = y * size + x;
                    double bodyT = body[i] / 255.0;           // 1 = dragon fill, 0 = outline/empty
                    // Body is accent; the ring around it is the outline color.
                    double r = LerpD(OutlineColor.R, accent.R, bodyT);
                    double g = LerpD(OutlineColor.G, accent.G, bodyT);
                    double b = LerpD(OutlineColor.B, accent.B, bodyT);
                    double a = expanded[i];                    // dragon + its outline

                    if (tick)
                    {
                        double ci = circleMask[i], ck = checkMask[i];
                        r = LerpD(r, TickGreen.R, ci / 255.0);
                        g = LerpD(g, TickGreen.G, ci / 255.0);
                        b = LerpD(b, TickGreen.B, ci / 255.0);
                        r = LerpD(r, 255, ck / 255.0);
                        g = LerpD(g, 255, ck / 255.0);
                        b = LerpD(b, 255, ck / 255.0);
                        a = Math.Max(a, ci);                   // the tick disc is opaque on its own
                    }

                    int oo = y * dst.RowBytes + x * 4;
                    dstBytes[oo] = (byte)Math.Clamp(b, 0, 255);
                    dstBytes[oo + 1] = (byte)Math.Clamp(g, 0, 255);
                    dstBytes[oo + 2] = (byte)Math.Clamp(r, 0, 255);
                    dstBytes[oo + 3] = (byte)Math.Clamp(a, 0, 255);
                }
            }
            Marshal.Copy(dstBytes, 0, dst.Address, dstBytes.Length);
        }
        using var ms = new MemoryStream();
        output.Save(ms);
        output.Dispose();
        return ms.ToArray();
    }

    static double Lerp(double from, double to, int mask) => from + (to - from) * mask / 255.0;
    static double LerpD(double from, double to, double t) => from + (to - from) * t;

    // Grayscale dilation (separable max filter) — grows the mask by `r` px in each
    // direction to form the outline shape behind the dragon body.
    static double[] Dilate(double[] src, int size, int r)
    {
        var tmp = new double[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double m = 0;
                for (int dx = -r; dx <= r; dx++)
                {
                    int xx = x + dx;
                    if (xx >= 0 && xx < size) m = Math.Max(m, src[y * size + xx]);
                }
                tmp[y * size + x] = m;
            }
        var outp = new double[size * size];
        for (int x = 0; x < size; x++)
            for (int y = 0; y < size; y++)
            {
                double m = 0;
                for (int dy = -r; dy <= r; dy++)
                {
                    int yy = y + dy;
                    if (yy >= 0 && yy < size) m = Math.Max(m, tmp[yy * size + x]);
                }
                outp[y * size + x] = m;
            }
        return outp;
    }

    // Bilinear sample of a 0..255 mask; anything outside the image reads as 0 (no dragon).
    static double SampleMask(double[] mask, int size, double fx, double fy)
    {
        if (fx < 0 || fy < 0 || fx > size - 1 || fy > size - 1) return 0;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        int x1 = Math.Min(x0 + 1, size - 1), y1 = Math.Min(y0 + 1, size - 1);
        double tx = fx - x0, ty = fy - y0;
        double top = mask[y0 * size + x0] * (1 - tx) + mask[y0 * size + x1] * tx;
        double bot = mask[y1 * size + x0] * (1 - tx) + mask[y1 * size + x1] * tx;
        return top * (1 - ty) + bot * ty;
    }

    static byte[] BuildIco(List<byte[]> pngs)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)pngs.Count);
        int offset = 6 + 16 * pngs.Count;
        for (int i = 0; i < pngs.Count; i++)
        {
            int size = IcoSizes[i];
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((ushort)1); bw.Write((ushort)32);
            bw.Write((uint)pngs[i].Length); bw.Write((uint)offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs) bw.Write(png);
        return ms.ToArray();
    }
}
