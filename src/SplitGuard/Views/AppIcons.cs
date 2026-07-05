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

    // Build a multi-size .ico for the given accent (used to regenerate Assets/app.ico from
    // the exact same compositor used at runtime).
    public static void ExportIco(string icoPath, Color accent)
    {
        var idle = new List<byte[]>();
        foreach (var size in IcoSizes) idle.Add(Compose(size, accent, tick: false));
        File.WriteAllBytes(icoPath, BuildIco(idle));
    }

    // Inflated (rounded) downward triangle background, in normalized [0,1] coordinates.
    static readonly (double X, double Y) TriA = (0.15, 0.19); // top-left
    static readonly (double X, double Y) TriB = (0.85, 0.19); // top-right
    static readonly (double X, double Y) TriC = (0.50, 0.82); // bottom apex
    const double TriRound = 0.09;   // corner rounding → the "inflated" look
    const double DragonFill = 0.50; // dragon size as a fraction of the canvas
    const double DragonCY = 0.45;   // dragon vertical center (upper part of the triangle)

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
            bool rgba = src.Format == PixelFormat.Rgba8888;

            // Recover all channel masks (un-premultiply) up front so they can be sampled
            // at fractional, zoomed coordinates.
            var mCheck = new double[size * size];
            var mCircle = new double[size * size];
            var mDragon = new double[size * size];
            var mAlpha = new double[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int o = y * src.RowBytes + x * 4;
                    int a = srcBytes[o + 3];
                    int check = srcBytes[o + (rgba ? 2 : 0)];
                    int circle = srcBytes[o + 1];
                    int dragon = srcBytes[o + (rgba ? 0 : 2)];
                    if (a > 0 && a < 255)
                    {
                        check = Math.Min(255, check * 255 / a);
                        circle = Math.Min(255, circle * 255 / a);
                        dragon = Math.Min(255, dragon * 255 / a);
                    }
                    int i = y * size + x;
                    mCheck[i] = check; mCircle[i] = circle; mDragon[i] = dragon; mAlpha[i] = a;
                }

            // Fit the dragon's bounding box so it sits, centered, inside the triangle.
            int minX = size, minY = size, maxX = -1, maxY = -1;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (mDragon[y * size + x] > 30)
                    {
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
            double bcx, bcy, dscale;
            if (maxX < 0) { bcx = bcy = (size - 1) / 2.0; dscale = 1; }
            else
            {
                bcx = (minX + maxX) / 2.0;
                bcy = (minY + maxY) / 2.0;
                double bw = maxX - minX + 1, bh = maxY - minY + 1;
                dscale = size * DragonFill / Math.Max(bw, bh);
            }

            var dstBytes = new byte[dst.RowBytes * size];
            var aa = 1.5 / size; // edge softness for the triangle, in normalized units
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Inflated triangle background coverage (0..1).
                    var nx = x / (double)(size - 1);
                    var ny = y / (double)(size - 1);
                    var sd = SdTriangle(nx, ny) - TriRound;
                    var bg = Math.Clamp(0.5 - sd / aa, 0, 1);

                    // Dragon (and tick) sampled at the dragon-fit transform.
                    var dx = bcx + (x - size * 0.5) / dscale;
                    var dy = bcy + (y - size * DragonCY) / dscale;
                    var dr = Math.Min(Sample(mDragon, size, dx, dy), Sample(mAlpha, size, dx, dy)) / 255.0;

                    // Triangle in accent; dragon knocked out in white on top of it.
                    double r = accent.R + (255 - accent.R) * dr;
                    double g = accent.G + (255 - accent.G) * dr;
                    double b = accent.B + (255 - accent.B) * dr;
                    var a = bg; // dragon is clipped to the triangle

                    if (tick)
                    {
                        var ci = Sample(mCircle, size, dx, dy) / 255.0;
                        var ck = Sample(mCheck, size, dx, dy) / 255.0;
                        r = r * (1 - ci) + TickGreen.R * ci; g = g * (1 - ci) + TickGreen.G * ci; b = b * (1 - ci) + TickGreen.B * ci;
                        r = r * (1 - ck) + 255 * ck; g = g * (1 - ck) + 255 * ck; b = b * (1 - ck) + 255 * ck;
                        a = Math.Max(a, Math.Max(ci, ck));
                    }

                    int oo = y * dst.RowBytes + x * 4;
                    dstBytes[oo] = (byte)Math.Clamp(b, 0, 255);
                    dstBytes[oo + 1] = (byte)Math.Clamp(g, 0, 255);
                    dstBytes[oo + 2] = (byte)Math.Clamp(r, 0, 255);
                    dstBytes[oo + 3] = (byte)Math.Round(a * 255);
                }
            }
            Marshal.Copy(dstBytes, 0, dst.Address, dstBytes.Length);
        }
        using var ms = new MemoryStream();
        output.Save(ms);
        output.Dispose();
        return ms.ToArray();
    }

    static double Sample(double[] a, int size, double fx, double fy)
    {
        if (fx < 0 || fy < 0 || fx > size - 1 || fy > size - 1) return 0;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        int x1 = Math.Min(x0 + 1, size - 1), y1 = Math.Min(y0 + 1, size - 1);
        double tx = fx - x0, ty = fy - y0;
        double top = a[y0 * size + x0] * (1 - tx) + a[y0 * size + x1] * tx;
        double bot = a[y1 * size + x0] * (1 - tx) + a[y1 * size + x1] * tx;
        return top * (1 - ty) + bot * ty;
    }

    static double Lerp(double from, double to, double mask) => from + (to - from) * mask / 255.0;

    // Signed distance to the triangle (negative inside); subtract a radius to round it.
    static double SdTriangle(double px, double py)
    {
        double ax = TriA.X, ay = TriA.Y, bx = TriB.X, by = TriB.Y, cx = TriC.X, cy = TriC.Y;
        double e0x = bx - ax, e0y = by - ay, e1x = cx - bx, e1y = cy - by, e2x = ax - cx, e2y = ay - cy;
        double v0x = px - ax, v0y = py - ay, v1x = px - bx, v1y = py - by, v2x = px - cx, v2y = py - cy;
        double p0x = v0x - e0x * Math.Clamp((v0x * e0x + v0y * e0y) / (e0x * e0x + e0y * e0y), 0, 1);
        double p0y = v0y - e0y * Math.Clamp((v0x * e0x + v0y * e0y) / (e0x * e0x + e0y * e0y), 0, 1);
        double p1x = v1x - e1x * Math.Clamp((v1x * e1x + v1y * e1y) / (e1x * e1x + e1y * e1y), 0, 1);
        double p1y = v1y - e1y * Math.Clamp((v1x * e1x + v1y * e1y) / (e1x * e1x + e1y * e1y), 0, 1);
        double p2x = v2x - e2x * Math.Clamp((v2x * e2x + v2y * e2y) / (e2x * e2x + e2y * e2y), 0, 1);
        double p2y = v2y - e2y * Math.Clamp((v2x * e2x + v2y * e2y) / (e2x * e2x + e2y * e2y), 0, 1);
        double s = Math.Sign(e0x * e2y - e0y * e2x);
        double d0 = Math.Min(p0x * p0x + p0y * p0y, p1x * p1x + p1y * p1y);
        d0 = Math.Min(d0, p2x * p2x + p2y * p2y);
        double d1 = Math.Min(Math.Min(s * (v0x * e0y - v0y * e0x), s * (v1x * e1y - v1y * e1x)), s * (v2x * e2y - v2y * e2x));
        return -Math.Sqrt(d0) * Math.Sign(d1);
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
