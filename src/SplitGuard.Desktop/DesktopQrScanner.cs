using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SplitGuard.Services;
using SplitGuard.Views;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WBuffer = Windows.Storage.Streams.Buffer;
using WDataReader = Windows.Storage.Streams.DataReader;

namespace SplitGuard;

// Desktop QR scanner: a live WinRT webcam feed (MediaCapture + MediaFrameReader) painted into an
// Avalonia WriteableBitmap, with per-frame ZXing decode. When there's no camera (or access is
// denied), the same stage becomes a drop / paste target — the user drops a QR image or pastes a
// peer descriptor instead. Mirrors AndroidQrScanner's IQrScanner shape so MainView hosts it the
// same way. It never raises Failed: "no camera" and recoverable drop/paste misses are surfaced
// inline on the fallback so the panel stays alive for another try (Failed would close the drawer).
public sealed class DesktopQrScanner : IQrScanner
{
    public Control Preview => _root;
    public event Action<string>? Decoded;
    // Interface-mandated (Android raises it for fatal camera errors), but the desktop scanner never
    // has a fatal state — "no camera" and drop/paste misses degrade in-panel — so it discards
    // subscribers rather than ever firing and tearing the drawer down.
    public event Action<string>? Failed { add { } remove { } }

    readonly Grid _root = new() { Focusable = true, Background = Brushes.Transparent };
    readonly Image _video = new() { Stretch = Stretch.UniformToFill, IsVisible = false };
    readonly Border _reticle;
    readonly Border _fallback;
    readonly TextBlock _fallbackTitle = new()
    {
        Text = "Starting camera…", FontWeight = FontWeight.SemiBold, FontSize = 14,
        Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC)),
        HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
    };

    readonly object _sync = new();       // guards the _capture/_reader read-modify-write across threads
    MediaCapture? _capture;
    MediaFrameReader? _reader;
    WriteableBitmap? _bmp;
    int _done;                           // 0 = live, 1 = torn down; latched atomically in Fire/Stop
    bool _live;                          // a webcam frame has been shown (UI thread only)
    long _lastPreviewMs, _lastDecodeMs;

    // Ring of frame buffers so the hot path doesn't allocate a ~1 MB LOH array every frame. A blit
    // posted to the UI thread captures one buffer; the next two frames use the others, so it's read
    // before it's reused. FrameArrived is serialized per reader, so no lock is needed here.
    readonly byte[]?[] _ring = new byte[3][];
    int _ringIdx;

    public DesktopQrScanner()
    {
        _reticle = new Border
        {
            Width = 150, Height = 150,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(2.5), CornerRadius = new CornerRadius(12),
            IsVisible = false, IsHitTestVisible = false,
        };

        var muted = new SolidColorBrush(Color.FromRgb(0xA6, 0xA6, 0xA6));
        var pasteBtn = new Button
        {
            Content = "Paste from clipboard",
            HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(14, 7),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Cursor = new Cursor(StandardCursorType.Hand),
        };
        pasteBtn.Click += (_, _) => PasteFromClipboard();

        _fallback = new Border
        {
            Background = Brushes.Transparent,
            Child = new StackPanel
            {
                Spacing = 9, MaxWidth = 280,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                        FontSize = 30, Foreground = muted, HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    _fallbackTitle,
                    new TextBlock
                    {
                        Text = "Drop a QR image here, or paste a peer descriptor.",
                        Foreground = muted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    pasteBtn,
                },
            },
        };

        _root.Children.Add(_video);
        _root.Children.Add(_reticle);
        _root.Children.Add(_fallback);

        DragDrop.SetAllowDrop(_root, true);
        _root.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _root.AddHandler(DragDrop.DropEvent, OnDrop);
        _root.KeyDown += OnKeyDown;
    }

    public void Start()
    {
        // Kick off camera init on the UI thread (Avalonia's Win32 dispatcher is STA — WinRT is happy
        // there). Any failure just leaves the drop / paste fallback in place.
        Dispatcher.UIThread.Post(async () =>
        {
            _root.Focus();
            try { await InitCameraAsync(); }
            catch { DisposeCamera(); ShowNoCamera(); }
        });
    }

    async Task InitCameraAsync()
    {
        if (_done != 0) return;
        var capture = new MediaCapture();
        var settings = new MediaCaptureInitializationSettings
        {
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,   // CPU frames → SoftwareBitmap we can read
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
        };
        await capture.InitializeAsync(settings);

        // The drawer may have closed (Stop ran) while InitializeAsync was in flight — if so, release
        // what we just opened instead of storing it, so the camera never lingers held.
        bool keep;
        lock (_sync) { keep = _done == 0; if (keep) _capture = capture; }
        if (!keep) { try { capture.Dispose(); } catch { } return; }

        MediaFrameSource? source = null;
        foreach (var kv in capture.FrameSources)
            if (kv.Value.Info.SourceKind == MediaFrameSourceKind.Color) { source = kv.Value; break; }
        if (source is null) { DisposeCamera(); ShowNoCamera(); return; }   // e.g. IR-only Hello camera

        // Prefer a modest resolution so the per-frame ZXing decode stays cheap.
        try
        {
            var fmt = source.SupportedFormats
                .Where(f => f.VideoFormat.Width >= 480 && string.Equals(f.Subtype, "NV12", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.VideoFormat.Width).FirstOrDefault()
                ?? source.SupportedFormats.Where(f => f.VideoFormat.Width >= 480)
                    .OrderBy(f => f.VideoFormat.Width).FirstOrDefault();
            if (fmt is not null) await source.SetFormatAsync(fmt);
        }
        catch { /* keep the source's default format */ }

        var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        reader.FrameArrived += OnFrameArrived;
        lock (_sync) { keep = _done == 0; if (keep) _reader = reader; }
        if (!keep)   // closed during CreateFrameReaderAsync — dispose the orphan reader + the capture
        {
            try { reader.FrameArrived -= OnFrameArrived; reader.Dispose(); } catch { }
            DisposeCamera();
            return;
        }

        var status = await reader.StartAsync();
        if (_done != 0) { DisposeCamera(); return; }
        // Another app may hold the device (StartAsync returns a status, not an exception): release it
        // so it isn't left powered/held behind a "no camera" message, and fall back to drop / paste.
        if (status != MediaFrameReaderStartStatus.Success) { DisposeCamera(); ShowNoCamera(); return; }
    }

    void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (_done != 0) return;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var sb = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (sb is null) return;

            var now = Environment.TickCount64;
            bool doDecode = now - _lastDecodeMs >= 100;
            bool doPreview = now - _lastPreviewMs >= 66;
            if (!doDecode && !doPreview) return;

            SoftwareBitmap? converted = null;
            if (sb.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                sb = converted = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            int w = sb.PixelWidth, h = sb.PixelHeight;
            var bytes = NextBuffer(w * h * 4);
            var ibuf = new WBuffer((uint)bytes.Length);
            sb.CopyToBuffer(ibuf);
            using (var dr = WDataReader.FromBuffer(ibuf)) dr.ReadBytes(bytes);
            converted?.Dispose();

            if (doDecode)
            {
                _lastDecodeMs = now;
                var text = QrDecode.FromBgra(bytes, w, h);
                if (!string.IsNullOrEmpty(text)) { Fire(text!); return; }
            }
            if (doPreview)
            {
                _lastPreviewMs = now;
                Dispatcher.UIThread.Post(() => BlitPreview(bytes, w, h));
            }
        }
        catch { /* a frame in flight while tearing down — ignore */ }
    }

    // Hand out the next ring buffer (reallocated only when the frame size changes).
    byte[] NextBuffer(int size)
    {
        int i = _ringIdx;
        _ringIdx = (i + 1) % _ring.Length;
        var b = _ring[i];
        if (b is null || b.Length != size) b = _ring[i] = new byte[size];
        return b;
    }

    void BlitPreview(byte[] bgra, int w, int h)
    {
        if (_done != 0) return;
        if (_bmp is null || _bmp.PixelSize.Width != w || _bmp.PixelSize.Height != h)
        {
            _bmp?.Dispose();
            _bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        }
        using (var fb = _bmp.Lock())
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, fb.Address, Math.Min(bgra.Length, fb.RowBytes * fb.Size.Height));
        if (!ReferenceEquals(_video.Source, _bmp)) _video.Source = _bmp;
        _video.InvalidateVisual();
        if (!_live)
        {
            _live = true;
            _video.IsVisible = true; _reticle.IsVisible = true; _fallback.IsVisible = false;
        }
    }

    void ShowNoCamera() => Dispatcher.UIThread.Post(() =>
    {
        if (_done != 0 || _live) return;
        _fallbackTitle.Text = "No webcam detected";
    });

    // Surface a recoverable drop/paste miss inline on the fallback and keep waiting — never tear the
    // scanner down (that would kill the only scan surface on a webcam-less PC). Ignored when a webcam
    // is live, since the camera is the active surface there.
    void ReportMiss(string message) => Dispatcher.UIThread.Post(() =>
    {
        if (_done != 0 || _live) return;
        _fallbackTitle.Text = message;
    });

    // A decoded / dropped / pasted string. Atomically latch so the first hit wins (a webcam decode on
    // a frame thread can race a drop/paste on the UI thread) and the drawer imports it exactly once.
    void Fire(string text)
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        Dispatcher.UIThread.Post(() => Decoded?.Invoke(text));
    }

    void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = (e.DataTransfer.Contains(DataFormat.Text) || e.DataTransfer.Contains(DataFormat.File))
            ? DragDropEffects.Copy : DragDropEffects.None;

    async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_done != 0) return;
        var text = e.DataTransfer.TryGetText();
        if (!string.IsNullOrWhiteSpace(text)) { Fire(text); return; }

        foreach (var f in e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>() ?? Enumerable.Empty<IStorageFile>())
        {
            try
            {
                using var ms = new MemoryStream();
                await using (var s = await f.OpenReadAsync()) await s.CopyToAsync(ms);
                // A picture → decode the QR out of it; a .conf / descriptor → read it as text (only if
                // it actually looks like WireGuard config, so a non-QR binary image doesn't get Fired
                // as garbage — it falls through to the "couldn't read" message instead).
                ms.Position = 0;
                var qr = QrDecode.FromImageStream(ms);
                if (qr is not null) { Fire(qr); return; }
                ms.Position = 0;
                var body = new StreamReader(ms).ReadToEnd();
                if (body.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
                    || body.Contains("[Peer]", StringComparison.OrdinalIgnoreCase)) { Fire(body); return; }
            }
            catch { /* try the next file */ }
        }
        ReportMiss("Couldn't read a QR or peer descriptor from that");
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { PasteFromClipboard(); e.Handled = true; }
    }

    async void PasteFromClipboard()
    {
        if (_done != 0) return;
        var clip = TopLevel.GetTopLevel(_root)?.Clipboard;
        var text = clip is null ? null : await clip.TryGetTextAsync();
        if (!string.IsNullOrWhiteSpace(text)) Fire(text);
        else ReportMiss("Clipboard has no peer info — copy a descriptor first");
    }

    // Release the camera device WITHOUT latching _done — used on init-failure paths so the drop /
    // paste fallback stays usable after the webcam is given up.
    void DisposeCamera()
    {
        MediaFrameReader? r; MediaCapture? c;
        lock (_sync) { r = _reader; c = _capture; _reader = null; _capture = null; }
        try { if (r is not null) r.FrameArrived -= OnFrameArrived; } catch { }
        try { r?.Dispose(); } catch { }   // disposing the reader stops the stream
        try { c?.Dispose(); } catch { }   // releases the camera device
    }

    public void Stop()
    {
        Interlocked.Exchange(ref _done, 1);
        DisposeCamera();
        Dispatcher.UIThread.Post(() =>
        {
            _video.Source = null;
            try { _bmp?.Dispose(); } catch { }
            _bmp = null;
        });
    }

    public void Dispose() => Stop();
}
