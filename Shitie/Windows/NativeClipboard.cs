using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Shitie.Core;

namespace Shitie.Windows;

internal sealed record ClipboardOrigin(string ProcessName, uint ProcessId);

internal static class NativeClipboard
{
    private const int MaxImage = 16 * 1024 * 1024;
    internal const string OwnerFormat = "Shitie.Clipboard.Owner.v1";
    private static uint Format(string name) => Native.RegisterClipboardFormat(name);
    private static byte[]? Bytes(uint format, int maximum = 110 * 1024 * 1024)
    {
        if (!Native.IsClipboardFormatAvailable(format)) return null;
        var handle = Native.GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        var size = Native.GlobalSize(handle);
        if (size == 0 || size > (nuint)maximum) return null;
        var pointer = Native.GlobalLock(handle);
        if (pointer == IntPtr.Zero) throw new ExternalException("剪切板内容暂时被占用。");
        try { var bytes = new byte[(int)size]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes; }
        finally { Native.GlobalUnlock(handle); }
    }
    private static string? String(uint format, Encoding encoding, int max) => Bytes(format, max) is { } data ? encoding.GetString(data).TrimEnd('\0') : null;
    internal static ClipPayload? Read(IntPtr hwnd, string? selfToken, CopyGesture? gesture, ClipboardOrigin? origin = null)
    {
        if (!Native.OpenClipboard(hwnd)) throw new ExternalException("剪切板正被其他程序使用。");
        try
        {
            if (Native.IsClipboardFormatAvailable(Format("ExcludeClipboardContentFromMonitorProcessing"))) return null;
            var canRecord = Bytes(Format("CanIncludeInClipboardHistory"), 1024);
            if (canRecord is { Length: >= 4 } && BitConverter.ToInt32(canRecord) == 0) return null;
            if (selfToken != null && String(Format(OwnerFormat), Encoding.Unicode, 4096) == selfToken) return null;
            var source = Native.GetClipboardOwner();
            var sourceName = source != IntPtr.Zero ? Native.ProcessName(source) : origin?.ProcessName ?? Native.ProcessName(Native.GetForegroundWindow());
            var sourceId = source != IntPtr.Zero ? Native.ProcessId(source) : origin?.ProcessId ?? Native.ProcessId(Native.GetForegroundWindow());
            ClipPayload? payload = null;
            if (Native.IsClipboardFormatAvailable(15))
            {
                var handle = Native.GetClipboardData(15);
                var count = Native.DragQueryFile(handle, uint.MaxValue, null, 0);
                if (count > 0 && count <= 10000)
                {
                    var files = new List<string>();
                    for (uint i = 0; i < count; i++)
                    {
                        var length = Native.DragQueryFile(handle, i, null, 0);
                        if (length > 32767) return null;
                        var text = new StringBuilder((int)length + 1);
                        Native.DragQueryFile(handle, i, text, length + 1);
                        files.Add(text.ToString());
                    }
                    payload = new() { Kind = ClipKind.Files, Files = files.ToArray() };
                }
            }
            if (payload == null && Bytes(Format("PNG"), MaxImage) is { } png)
            {
                try
                {
                    var image = ClipboardService.LoadImage(png);
                    if ((long)image.PixelWidth * image.PixelHeight <= 25_000_000) payload = new() { Kind = ClipKind.Image, Png = png, Text = $"{image.PixelWidth} × {image.PixelHeight}" };
                }
                catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException) { }
            }
            if (payload == null && (Native.IsClipboardFormatAvailable(17) || Native.IsClipboardFormatAvailable(8) || Native.IsClipboardFormatAvailable(2)))
            {
                BitmapSource? image = null;
                var dib = Bytes(17) ?? Bytes(8);
                if (dib != null)
                {
                    try { image = DecodeDib(dib); }
                    catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException) { }
                }
                if (image == null && Native.IsClipboardFormatAvailable(2))
                {
                    var handle = Native.GetClipboardData(2);
                    if (handle != IntPtr.Zero) image = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                }
                if (image != null && (long)image.PixelWidth * image.PixelHeight <= 25_000_000)
                {
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var stream = new MemoryStream(); encoder.Save(stream);
                    if (stream.Length <= MaxImage) payload = new() { Kind = ClipKind.Image, Png = stream.ToArray(), Text = $"{image.PixelWidth} × {image.PixelHeight}" };
                }
            }
            if (payload == null)
            {
                var text = String(13, Encoding.Unicode, 4_000_002);
                if (!string.IsNullOrEmpty(text) && text.Length <= 2_000_000)
                    payload = new() { Kind = ClipKind.Text, Text = text, Html = String(Format("HTML Format"), Encoding.UTF8, 8_000_000), Rtf = String(Format("Rich Text Format"), Encoding.UTF8, 8_000_000) };
            }
            if (payload == null) return null;
            payload.Source = sourceName;
            var effect = payload.Kind == ClipKind.Files ? Bytes(Format("Preferred DropEffect"), 1024) : null;
            var intent = gesture?.Consume(sourceId) ?? CopyIntent.None;
            payload.WasExplicitCopy = intent != CopyIntent.None;
            payload.WasCut = intent == CopyIntent.Cut || (effect is { Length: >= 4 } && (BitConverter.ToInt32(effect) & 2) != 0);
            return payload;
        }
        finally { Native.CloseClipboard(); }
    }
    internal static BitmapSource? DecodeDib(byte[] dib)
    {
        if (dib.Length < 40) return null;
        var header = BitConverter.ToInt32(dib, 0);
        if (header < 40 || header > dib.Length) return null;
        var width = BitConverter.ToInt32(dib, 4); var height = (long)BitConverter.ToInt32(dib, 8);
        if (width <= 0 || height == 0 || width * Math.Abs(height) > 25_000_000) return null;
        var bpp = BitConverter.ToUInt16(dib, 14); var compression = BitConverter.ToInt32(dib, 16);
        var colors = BitConverter.ToUInt32(dib, 32);
        if (colors > 256) return null;
        if (colors == 0 && bpp <= 8) colors = (uint)(1 << bpp);
        var offset = header + colors * 4L + (header == 40 ? compression == 3 ? 12 : compression == 6 ? 16 : 0 : 0);
        if (offset > dib.Length) return null;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B'); writer.Write((byte)'M'); writer.Write(dib.Length + 14); writer.Write(0); writer.Write((int)offset + 14); writer.Write(dib); stream.Position = 0;
        var decoder = new BmpBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var image = decoder.Frames[0]; image.Freeze(); return image;
    }
    internal static Dictionary<uint, byte[]> Formats(ClipPayload payload, string? selfToken = null)
    {
        var formats = new Dictionary<uint, byte[]>();
        if (payload.Kind == ClipKind.Text)
        {
            formats[13] = Encoding.Unicode.GetBytes(payload.Text + '\0');
            if (payload.Html != null) formats[Format("HTML Format")] = Encoding.UTF8.GetBytes(payload.Html + '\0');
            if (payload.Rtf != null) formats[Format("Rich Text Format")] = Encoding.UTF8.GetBytes(payload.Rtf + '\0');
        }
        else if (payload.Kind == ClipKind.Image)
        {
            formats[Format("PNG")] = payload.Png!;
            var encoder = new BmpBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(ClipboardService.LoadImage(payload.Png!)));
            using var stream = new MemoryStream(); encoder.Save(stream);
            formats[8] = stream.ToArray()[14..];
        }
        else
        {
            var files = payload.Files ?? [];
            if (files.Length == 0 || files.Any(path => !File.Exists(path) && !Directory.Exists(path))) throw new FileNotFoundException("原文件已被移动或删除，无法重新复制。");
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(20); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(1);
            writer.Write(Encoding.Unicode.GetBytes(string.Join('\0', files) + "\0\0"));
            formats[15] = stream.ToArray();
            formats[Format("Preferred DropEffect")] = BitConverter.GetBytes(1);
        }
        if (selfToken != null) formats[Format(OwnerFormat)] = Encoding.Unicode.GetBytes(selfToken + '\0');
        return formats;
    }
    internal static void Write(IntPtr hwnd, Dictionary<uint, byte[]> formats)
    {
        // Allocate all data before replacing the current clipboard.
        var allocated = new Dictionary<uint, IntPtr>();
        try
        {
            foreach (var pair in formats)
            {
                var handle = Native.GlobalAlloc(0x42, (nuint)Math.Max(1, pair.Value.Length));
                if (handle == IntPtr.Zero) throw new OutOfMemoryException();
                allocated[pair.Key] = handle;
                var pointer = Native.GlobalLock(handle);
                if (pointer == IntPtr.Zero) throw new OutOfMemoryException();
                try { Marshal.Copy(pair.Value, 0, pointer, pair.Value.Length); } finally { Native.GlobalUnlock(handle); }
            }
            if (!Native.OpenClipboard(hwnd)) throw new ExternalException("剪切板正被其他程序使用。");
            try
            {
                if (!Native.EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
                foreach (var format in allocated.Keys.ToArray())
                {
                    if (Native.SetClipboardData(format, allocated[format]) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                    allocated[format] = IntPtr.Zero; // Windows now owns this allocation.
                }
            }
            finally { Native.CloseClipboard(); }
        }
        finally { foreach (var handle in allocated.Values) if (handle != IntPtr.Zero) Native.GlobalFree(handle); }
    }
    internal static Dictionary<uint, byte[]> Backup(IntPtr hwnd)
    {
        if (!Native.OpenClipboard(hwnd)) throw new ExternalException("无法备份当前剪切板。");
        try
        {
            var result = new Dictionary<uint, byte[]>();
            for (uint format = Native.EnumClipboardFormats(0); format != 0; format = Native.EnumClipboardFormats(format))
            {
                if (format is 2 or 3 or 9 or 14 or >= 0x80 and <= 0x8E or >= 0x200 and <= 0x3FF) continue;
                if (Bytes(format) is { } bytes) result[format] = bytes;
            }
            if (!result.ContainsKey(8) && Bytes(8) is { } dib) result[8] = dib;
            return result;
        }
        finally { Native.CloseClipboard(); }
    }
}
