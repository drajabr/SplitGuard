using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SplitGuard.Services;
using ZXing;
using ZXing.Common;
using AView = Android.Views;
using AImage = Avalonia.Controls.Image;

namespace SplitGuard.Droid;

// Camera2 → YUV frames feed both a ZXing QR decode and a grayscale preview painted into an
// Avalonia WriteableBitmap, so the live preview lives in the Avalonia card (no native-view
// compositing). One frame serves both; decode runs on the camera thread, preview blits are
// throttled and marshalled to the UI thread.
public class AndroidQrScanner : Java.Lang.Object, IQrScanner
{
    const int W = 640, H = 480;

    public Control Preview => _image;
    public event Action<string>? Decoded;
    public event Action<string>? Failed;

    readonly AImage _image = new() { Stretch = Avalonia.Media.Stretch.UniformToFill };
    WriteableBitmap? _bmp;
    // AutoRotate so a portrait-held QR reads off the landscape sensor frame; QR-only for speed.
    readonly BarcodeReaderGeneric _reader = new()
    {
        AutoRotate = true,
        Options = new DecodingOptions { TryHarder = true, PossibleFormats = new[] { BarcodeFormat.QR_CODE } },
    };

    CameraManager? _mgr;
    CameraDevice? _device;
    CameraCaptureSession? _session;
    ImageReader? _imgReader;
    HandlerThread? _thread;
    Handler? _handler;
    volatile bool _done;
    bool _askedPermission;
    long _lastPreviewMs;

    public void Start()
    {
        var ctx = Android.App.Application.Context;
        if (ContextCompat_CheckSelfCamera(ctx) != Android.Content.PM.Permission.Granted)
        {
            if (_askedPermission) { Failed?.Invoke("Camera permission is required to scan a QR"); return; }
            _askedPermission = true;
            MainActivity.RequestCameraThen(() => { if (!_done) Start(); }); // retry once after the prompt
            return;
        }
        try { OpenCamera(ctx); }
        catch (Exception ex) { Failed?.Invoke($"Camera unavailable: {ex.Message}"); }
    }

    static Android.Content.PM.Permission ContextCompat_CheckSelfCamera(Context ctx) =>
        ctx.CheckSelfPermission(Android.Manifest.Permission.Camera);

    void OpenCamera(Context ctx)
    {
        _mgr = (CameraManager)ctx.GetSystemService(Context.CameraService)!;
        string? id = null;
        foreach (var c in _mgr.GetCameraIdList())
        {
            var chars = _mgr.GetCameraCharacteristics(c);
            var facing = (Java.Lang.Integer?)chars.Get(CameraCharacteristics.LensFacing);
            if (facing?.IntValue() == (int)LensFacing.Back) { id = c; break; }
            id ??= c;
        }
        if (id is null) { Failed?.Invoke("No camera found"); return; }

        _thread = new HandlerThread("sg-qr");
        _thread.Start();
        _handler = new Handler(_thread.Looper!);

        _imgReader = ImageReader.NewInstance(W, H, ImageFormatType.Yuv420888, 2);
        _imgReader.SetOnImageAvailableListener(new FrameListener(this), _handler);

        _mgr.OpenCamera(id, new StateCb(this), _handler);
    }

    void OnCameraOpened(CameraDevice device)
    {
        _device = device;
        try
        {
            var surface = _imgReader!.Surface!;
            var req = device.CreateCaptureRequest(CameraTemplate.Preview);
            req.AddTarget(surface);
            device.CreateCaptureSession(new List<AView.Surface> { surface },
                new SessionCb(this, req.Build()), _handler);
        }
        catch (Exception ex) { Failed?.Invoke($"Camera error: {ex.Message}"); }
    }

    void OnSessionReady(CameraCaptureSession session, CaptureRequest req)
    {
        _session = session;
        try { session.SetRepeatingRequest(req, null, _handler); }
        catch (Exception ex) { Failed?.Invoke($"Camera error: {ex.Message}"); }
    }

    // Called on the camera thread for each frame.
    void OnFrame(Android.Media.Image img)
    {
        try
        {
            int w = img.Width, h = img.Height;
            var y = img.GetPlanes()![0];
            var buf = y.Buffer!;
            int rowStride = y.RowStride;
            var luma = new byte[w * h];
            if (rowStride == w)
            {
                buf.Get(luma, 0, w * h);
            }
            else
            {
                var row = new byte[rowStride];
                for (int r = 0; r < h; r++)
                {
                    buf.Position(r * rowStride);
                    int n = Math.Min(rowStride, buf.Remaining());
                    buf.Get(row, 0, n);
                    Array.Copy(row, 0, luma, r * w, w);
                }
            }

            Decode(luma, w, h);
            MaybePreview(luma, w, h);
        }
        catch { }
    }

    void Decode(byte[] luma, int w, int h)
    {
        if (_done) return;
        string? text;
        try
        {
            var src = new PlanarYUVLuminanceSource(luma, w, h, 0, 0, w, h, false);
            text = _reader.Decode(src)?.Text;
        }
        catch { text = null; }
        if (string.IsNullOrEmpty(text)) return;
        _done = true;
        Decoded?.Invoke(text!);
    }

    void MaybePreview(byte[] luma, int w, int h)
    {
        var now = SystemClock.ElapsedRealtime();
        if (now - _lastPreviewMs < 66) return; // ~15 fps
        _lastPreviewMs = now;
        // Grayscale RGBA from luminance (cheap; a QR aiming view doesn't need color).
        var rgba = new byte[w * h * 4];
        for (int i = 0, j = 0; i < luma.Length; i++, j += 4)
        {
            var v = luma[i];
            rgba[j] = v; rgba[j + 1] = v; rgba[j + 2] = v; rgba[j + 3] = 255;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (_done) return;
            _bmp ??= new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Rgba8888, AlphaFormat.Opaque);
            using (var fb = _bmp.Lock())
                System.Runtime.InteropServices.Marshal.Copy(rgba, 0, fb.Address, rgba.Length);
            if (!ReferenceEquals(_image.Source, _bmp)) _image.Source = _bmp;
            _image.InvalidateVisual();
        });
    }

    public void Stop()
    {
        _done = true;
        try { _session?.Close(); } catch { }
        try { _device?.Close(); } catch { }
        try { _imgReader?.Close(); } catch { }
        try { _thread?.QuitSafely(); } catch { }
        _session = null; _device = null; _imgReader = null; _thread = null; _handler = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Stop();
        base.Dispose(disposing);
    }

    // ---- Camera2 callbacks ----
    class StateCb : CameraDevice.StateCallback
    {
        readonly AndroidQrScanner _s;
        public StateCb(AndroidQrScanner s) => _s = s;
        public override void OnOpened(CameraDevice camera) => _s.OnCameraOpened(camera);
        public override void OnDisconnected(CameraDevice camera) { try { camera.Close(); } catch { } }
        public override void OnError(CameraDevice camera, CameraError error)
        {
            try { camera.Close(); } catch { }
            _s.Failed?.Invoke($"Camera error ({error})");
        }
    }

    class SessionCb : CameraCaptureSession.StateCallback
    {
        readonly AndroidQrScanner _s; readonly CaptureRequest _req;
        public SessionCb(AndroidQrScanner s, CaptureRequest req) { _s = s; _req = req; }
        public override void OnConfigured(CameraCaptureSession session) => _s.OnSessionReady(session, _req);
        public override void OnConfigureFailed(CameraCaptureSession session) => _s.Failed?.Invoke("Camera setup failed");
    }

    class FrameListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        readonly AndroidQrScanner _s;
        public FrameListener(AndroidQrScanner s) => _s = s;
        public void OnImageAvailable(ImageReader? reader)
        {
            var img = reader?.AcquireLatestImage();
            if (img is null) return;
            try { _s.OnFrame(img); } finally { img.Close(); }
        }
    }
}
