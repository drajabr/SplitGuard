using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace SplitGuard.Views;

// Renders a string to a black-on-white QR WriteableBitmap (high contrast so it always
// scans regardless of the surrounding theme). Pure-managed via ZXing.Net's BitMatrix —
// no System.Drawing — so it works on both the desktop and Android heads.
public static class QrGen
{
    public static WriteableBitmap Generate(string text, int size = 512)
    {
        var writer = new QRCodeWriter();
        var hints = new Dictionary<EncodeHintType, object>
        {
            { EncodeHintType.MARGIN, 1 },
            { EncodeHintType.ERROR_CORRECTION, ErrorCorrectionLevel.M },
            { EncodeHintType.CHARACTER_SET, "UTF-8" },
        };
        var matrix = writer.encode(text, BarcodeFormat.QR_CODE, size, size, hints);
        int w = matrix.Width, h = matrix.Height;

        // Rgba8888 (not Bgra) — the Android emulator's GLES rejects a BGRA texture upload
        // (GL_INVALID_OPERATION), leaving a blank image. Black/white is byte-order-symmetric
        // anyway, so this is identical on the desktop.
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
            PixelFormat.Rgba8888, AlphaFormat.Opaque);
        using var fb = bmp.Lock();
        var row = new byte[fb.RowBytes];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte v = matrix[x, y] ? (byte)0 : (byte)255; // dark module → black, else white
                int o = x * 4;
                row[o] = v; row[o + 1] = v; row[o + 2] = v; row[o + 3] = 255;
            }
            Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, w * 4);
        }
        return bmp;
    }
}
