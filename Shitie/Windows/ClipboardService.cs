using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shitie.Core;

namespace Shitie.Windows;

internal sealed class ClipboardService : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _selfToken = Guid.NewGuid().ToString("N");
    private readonly CopyGesture _gesture = new();
    private Dispatcher _dispatcher = null!;
    private HwndSource _window = null!;
    private DispatcherTimer? _retry;
    private uint _sequence;
    private ClipboardOrigin? _pendingOrigin;
    private int _attempt;
    private uint _modifiers, _key;
    private bool _hasHotkey;
    private volatile bool _enabled = true;
    public event Action<ClipPayload>? Captured;
    public event Action? HotkeyPressed;
    public event Action<string>? Notice;
    public bool GestureAvailable => _gesture.Available;
    public bool Enabled { get => _enabled; set => _enabled = value; }
    public ClipboardService()
    {
        _thread = new Thread(() =>
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                _window = new HwndSource(new HwndSourceParameters("Shitie.Clipboard") { Width = 0, Height = 0, ParentWindow = new IntPtr(-3), WindowStyle = 0 });
                _window.AddHook(WindowProc);
                _sequence = Native.GetClipboardSequenceNumber();
                if (!Native.AddClipboardFormatListener(_window.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法监听系统剪切板");
                _retry = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(45) };
                _retry.Tick += (_, _) => TryCapture();
                _ready.SetResult(true);
                Dispatcher.Run();
                _retry.Stop();
                Native.UnregisterHotKey(_window.Handle, 1);
                Native.RemoveClipboardFormatListener(_window.Handle);
                _window.Dispose();
            }
            catch (Exception ex) { _ready.TrySetException(ex); Notice?.Invoke("剪切板监听已停止：" + ex.Message); }
        }) { IsBackground = true, Name = "Shitie clipboard STA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }
    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WmClipboardUpdate)
        {
            if (!_enabled) { _sequence = Native.GetClipboardSequenceNumber(); return IntPtr.Zero; }
            var sequence = Native.GetClipboardSequenceNumber();
            if (sequence != _sequence)
            {
                _sequence = sequence;
                var owner = Native.GetClipboardOwner();
                if (owner == IntPtr.Zero) owner = Native.GetForegroundWindow();
                _pendingOrigin = new(Native.ProcessName(owner), Native.ProcessId(owner));
                _attempt = 0;
                _retry!.Stop();
                _retry.Interval = TimeSpan.FromMilliseconds(45);
                // Let the producer finish OleFlushClipboard before opening its data.
                _retry.Start();
            }
            handled = true;
        }
        else if (msg == Native.WmHotkey) { HotkeyPressed?.Invoke(); handled = true; }
        return IntPtr.Zero;
    }
    internal static bool Excluded(IDataObject data)
    {
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", false)) return true;
        if (!data.GetDataPresent("CanIncludeInClipboardHistory", false)) return false;
        var value = data.GetData("CanIncludeInClipboardHistory", false);
        return value switch
        {
            MemoryStream stream => stream.Length >= 4 && BitConverter.ToInt32(stream.ToArray()) == 0,
            byte[] bytes => bytes.Length >= 4 && BitConverter.ToInt32(bytes) == 0,
            int number => number == 0,
            _ => false
        };
    }
    private void TryCapture()
    {
        _retry!.Stop();
        if (!_enabled) return;
        try
        {
            var hint = Native.GetClipboardSequenceNumber() == _sequence ? _pendingOrigin : null;
            var payload = NativeClipboard.Read(_window.Handle, _selfToken, _gesture, hint);
            if (payload == null) return;
            _sequence = Native.GetClipboardSequenceNumber();
            Captured?.Invoke(payload);
        }
        catch (Exception ex) when (ex is COMException or ExternalException or IOException or InvalidOperationException or ArgumentException)
        {
            if (++_attempt < 18) { _retry.Interval = TimeSpan.FromMilliseconds(Math.Min(45 + _attempt * 20, 220)); _retry.Start(); }
            else Notice?.Invoke("刚才的内容暂时无法读取：" + ex.Message);
        }
        catch (Exception ex) { Notice?.Invoke("这条剪切板内容未能记录：" + ex.Message); }
    }
    internal static ClipPayload? Read(IDataObject data)
    {
        const int maxBytes = 16 * 1024 * 1024;
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            if (paths.Length > 10000 || paths.Sum(x => (long)x.Length) > maxBytes / 2) return null;
            return new() { Kind = ClipKind.Files, Files = paths };
        }
        if (data.GetDataPresent("PNG", false) && data.GetData("PNG", false) is MemoryStream png && png.Length <= maxBytes)
        {
            try
            {
                var image = LoadImage(png.ToArray());
                if ((long)image.PixelWidth * image.PixelHeight > 25_000_000) return null;
                return new() { Kind = ClipKind.Image, Png = png.ToArray(), Text = $"{image.PixelWidth} × {image.PixelHeight}" };
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException) { }
        }
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 25_000_000) return null;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            if (stream.Length > maxBytes) return null;
            return new() { Kind = ClipKind.Image, Png = stream.ToArray(), Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight}" };
        }
        string? text = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string;
        if (string.IsNullOrEmpty(text) || text.Length > 2_000_000) return null;
        string? Optional(string format) => data.GetDataPresent(format, false) && data.GetData(format, false) is string value && value.Length <= 2_000_000 ? value : null;
        return new() { Kind = ClipKind.Text, Text = text, Html = Optional(DataFormats.Html), Rtf = Optional(DataFormats.Rtf) };
    }
    internal static BitmapImage LoadImage(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }
    public async Task WriteAsync(ClipPayload payload)
    {
        var formats = NativeClipboard.Formats(payload, _selfToken);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _dispatcher.InvokeAsync(() => NativeClipboard.Write(_window.Handle, formats));
                return;
            }
            catch (ExternalException) when (attempt < 8) { await Task.Delay(50 + attempt * 15); }
        }
    }
    public Task<bool> RegisterAsync(uint modifiers, uint key) => _dispatcher.InvokeAsync(() =>
    {
        if (_hasHotkey && modifiers == _modifiers && key == _key) return true;
        var oldModifiers = _modifiers; var oldKey = _key; var hadOld = _hasHotkey;
        Native.UnregisterHotKey(_window.Handle, 1);
        if (Native.RegisterHotKey(_window.Handle, 1, modifiers | 0x4000, key)) { _modifiers = modifiers; _key = key; _hasHotkey = true; return true; }
        _hasHotkey = hadOld && Native.RegisterHotKey(_window.Handle, 1, oldModifiers | 0x4000, oldKey);
        return false;
    }).Task;
    public void Dispose()
    {
        _enabled = false;
        _gesture.Dispose();
        _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        _thread.Join(TimeSpan.FromSeconds(3));
    }
}
