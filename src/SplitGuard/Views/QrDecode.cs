using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace SplitGuard.Views;

// Decode a QR out of a still image (a dropped / pasted picture, or a single camera frame) with
// ZXing — the read counterpart to QrGen. Pure-managed via Avalonia's Bitmap + ZXing's
// RGBLuminanceSource, so it works on both heads with no System.Drawing.
public static class QrDecode
{
    static readonly BarcodeReaderGeneric Reader = new()
    {
        AutoRotate = true,
        Options = new DecodingOptions { TryHarder = true, PossibleFormats = new[] { BarcodeFormat.QR_CODE } },
    };

    // Decode from an image stream (PNG/JPEG/…). Returns the QR text, or null if none is found.
    public static string? FromImageStream(Stream stream)
    {
        try { using var bmp = new Bitmap(stream); return FromBitmap(bmp); }
        catch { return null; }
    }

    // Decode from BGRA8888 pixels (e.g. a camera frame already in a buffer). ZXing's reader keeps
    // mutable decode state and isn't thread-safe, and this runs from webcam frame threads AND the UI
    // drop thread — so serialize every decode through the one reader.
    public static string? FromBgra(byte[] bgra, int width, int height)
    {
        if (width < 1 || height < 1 || bgra.Length < width * height * 4) return null;
        try
        {
            lock (Reader)
                return Reader.Decode(new RGBLuminanceSource(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32))?.Text;
        }
        catch { return null; }
    }

    static string? FromBitmap(Bitmap bmp)
    {
        var size = bmp.PixelSize;
        if (size.Width < 1 || size.Height < 1) return null;
        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { bmp.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle.AddrOfPinnedObject(), buffer.Length, stride); }
        finally { handle.Free(); }
        return FromBgra(buffer, size.Width, size.Height);
    }
}
