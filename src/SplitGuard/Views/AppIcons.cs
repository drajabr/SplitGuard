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
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int so = y * src.RowBytes + x * 4;
                    int check = srcBytes[so + (rgba ? 2 : 0)];
                    int circle = srcBytes[so + 1];
                    int dragon = srcBytes[so + (rgba ? 0 : 2)];
                    int alpha = srcBytes[so + 3];
                    if (alpha > 0 && alpha < 255)
                    {
                        // decoded premultiplied: recover the masks
                        check = Math.Min(255, check * 255 / alpha);
                        circle = Math.Min(255, circle * 255 / alpha);
                        dragon = Math.Min(255, dragon * 255 / alpha);
                    }
                    double r = Lerp(accent.R, 255, dragon);
                    double g = Lerp(accent.G, 255, dragon);
                    double b = Lerp(accent.B, 255, dragon);
                    if (tick)
                    {
                        r = Lerp(r, TickGreen.R, circle);
                        g = Lerp(g, TickGreen.G, circle);
                        b = Lerp(b, TickGreen.B, circle);
                        r = Lerp(r, 255, check);
                        g = Lerp(g, 255, check);
                        b = Lerp(b, 255, check);
                    }
                    int oo = y * dst.RowBytes + x * 4;
                    dstBytes[oo] = (byte)b;
                    dstBytes[oo + 1] = (byte)g;
                    dstBytes[oo + 2] = (byte)r;
                    dstBytes[oo + 3] = (byte)alpha;
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
