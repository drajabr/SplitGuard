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
        // Encode at NATIVE module resolution (1x1 request → modules + the 1-module margin),
        // then integer-upscale ourselves. Asking ZXing for the final pixel size pads the
        // leftover into extra quiet zone, so a short payload (the pair descriptor) came out
        // with a much fatter white border than a long one (the tunnel export).
        var matrix = writer.encode(text, BarcodeFormat.QR_CODE, 1, 1, hints);
        int mw = matrix.Width, mh = matrix.Height;
        int scale = Math.Max(1, size / mw);
        int w = mw * scale, h = mh * scale;

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
                byte v = matrix[x / scale, y / scale] ? (byte)0 : (byte)255; // dark module → black
                int o = x * 4;
                row[o] = v; row[o + 1] = v; row[o + 2] = v; row[o + 3] = 255;
            }
            Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, w * 4);
        }
        return bmp;
    }
}
