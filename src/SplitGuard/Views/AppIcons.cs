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

    // Fraction of the canvas left as padding around the dragon (rest is filled by it).
    const double Pad = 0.06;

    static byte[] Compose(int size, Color accent, bool tick)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://SplitGuard.Core/Assets/icon-template-{size}.png"));
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

            // Fit the dragon's bounding box to the canvas (minus padding), centered — the
            // maximum zoom that never clips, regardless of where the dragon sits.
            int minX = size, minY = size, maxX = -1, maxY = -1;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (mDragon[y * size + x] > 30)
                    {
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
            double bcx, bcy, scale;
            if (maxX < 0) { bcx = bcy = (size - 1) / 2.0; scale = 1; }
            else
            {
                bcx = (minX + maxX) / 2.0;
                bcy = (minY + maxY) / 2.0;
                double bw = maxX - minX + 1, bh = maxY - minY + 1;
                scale = size * (1 - 2 * Pad) / Math.Max(bw, bh);
            }

            var dstBytes = new byte[dst.RowBytes * size];
            double cc = (size - 1) / 2.0;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var sx = bcx + (x - cc) / scale;
                    var sy = bcy + (y - cc) / scale;
                    var check = Sample(mCheck, size, sx, sy);
                    var circle = Sample(mCircle, size, sx, sy);
                    var dragon = Sample(mDragon, size, sx, sy);
                    var alpha = Sample(mAlpha, size, sx, sy);

                    // The dragon itself is the icon (accent-colored), no background fill.
                    // Alpha follows the dragon shape; the "active" tick overlay extends it.
                    double r = accent.R, g = accent.G, b = accent.B;
                    var shape = dragon;
                    if (tick)
                    {
                        r = Lerp(r, TickGreen.R, circle);
                        g = Lerp(g, TickGreen.G, circle);
                        b = Lerp(b, TickGreen.B, circle);
                        r = Lerp(r, 255, check);
                        g = Lerp(g, 255, check);
                        b = Lerp(b, 255, check);
                        shape = Math.Max(dragon, circle);
                    }
                    int oo = y * dst.RowBytes + x * 4;
                    dstBytes[oo] = (byte)b;
                    dstBytes[oo + 1] = (byte)g;
                    dstBytes[oo + 2] = (byte)r;
                    // Clip the source alpha to the dragon/tick shape.
                    dstBytes[oo + 3] = (byte)Math.Round(Math.Min(alpha, shape));
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
