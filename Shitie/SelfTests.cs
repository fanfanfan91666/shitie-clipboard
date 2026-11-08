using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shitie.Core;
using Shitie.Windows;

namespace Shitie;

internal static class SelfTests
{
    public static int Run(string[] args)
    {
        var reportIndex = Array.IndexOf(args, "--report");
        var report = reportIndex >= 0 && reportIndex + 1 < args.Length ? args[reportIndex + 1] : Path.Combine(AppContext.BaseDirectory, "self-test-results.txt");
        var root = Path.Combine(Path.GetTempPath(), "ShitieTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var results = new List<string>(); var failed = 0;
        results.Add("Windows work area: " + SystemParameters.WorkArea);
        void Test(string name, Action action)
        {
            try { action(); results.Add("PASS " + name); }
            catch (Exception ex) { results.Add("FAIL " + name + "\n" + ex); failed++; }
        }
        static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        static ClipPayload Text(string text) => new() { Text = text, Kind = ClipKind.Text, Source = "测试应用" };
        var dbRoot = Path.Combine(root, "数据库-中文路径");
        Test("Unicode paths, persistence, encryption, deduplication and ordering", () =>
        {
            using (var db = new HistoryStore(dbRoot))
            {
                var first = db.Upsert(Text("第一条私密内容 ♫"), 1000);
                db.Upsert(Text("第二条"), 2000);
                Check(db.Upsert(Text("第一条私密内容 ♫"), 3000) == first, "Dedup changed id");
                Check(db.Stats().Count == 2 && db.List()[0].Id == first, "Dedup/order incorrect");
                Check(db.Get(first)?.Text == "第一条私密内容 ♫", "Payload round trip failed");
                Check(db.List("测试应用").Count == 2, "Source search failed");
            }
            var raw = File.ReadAllBytes(Path.Combine(dbRoot, "history.db"));
            Check(!Encoding.UTF8.GetString(raw).Contains("私密内容"), "Plaintext content in database");
            using var reopened = new HistoryStore(dbRoot);
            Check(reopened.Stats().Count == 2, "Restart lost data");
            var target = reopened.List().Last().Id;
            reopened.Touch(target);
            Check(reopened.List()[0].Id == target, "Recopy did not promote entry");
            reopened.Delete(target);
            Check(reopened.Stats().Count == 1, "Delete failed");
            reopened.Clear();
            Check(reopened.Stats().Count == 0, "Clear failed");
        });
        Test("Full content search beyond preview, type filters and pagination", () =>
        {
            using var db = new HistoryStore(Path.Combine(root, "search"));
            db.Upsert(Text(new string('a', 1200) + "很后面的关键词"));
            db.Upsert(new ClipPayload { Kind = ClipKind.Files, Files = [@"C:\测试\文档.txt"], Source = "explorer" });
            for (var i = 0; i < 90; i++) db.Upsert(Text("分页记录 " + i));
            Check(db.List("关键词").Count == 1, "Full text search failed");
            Check(db.List(kind: ClipKind.Files).Count == 1, "Type filter failed");
            var one = db.List(limit: 80); var two = db.List(offset: 80, limit: 80);
            Check(one.Count == 80 && two.Count == 12 && !one.Select(x => x.Id).Intersect(two.Select(x => x.Id)).Any(), "Pagination failed");
        });
        Test("Retention, permanent retention and oldest first storage eviction", () =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var db = new HistoryStore(Path.Combine(root, "retention"));
            db.Upsert(Text("过期"), now - 400L * 86_400_000);
            db.Upsert(Text("近期"), now - 2L * 86_400_000);
            var refreshed = db.Upsert(Text("重新复制"), now - 400L * 86_400_000);
            db.Touch(refreshed);
            db.Cleanup(new AppSettings { RetentionDays = 365 }, now);
            Check(db.Stats().Count == 2 && db.List("过期").Count == 0, "Age cleanup or promotion failed");
            db.Upsert(Text("永久"), 1000);
            db.Cleanup(new AppSettings { RetentionDays = 0 }, now);
            Check(db.List("永久").Count == 1, "Permanent retention failed");
            db.Cleanup(new AppSettings { RetentionDays = 0, MaximumStorageMb = 0 }, now);
            Check(db.Stats().Count == 0, "Storage budget failed");
        });
        Test("Rich text, file paths and image payload round trips", () =>
        {
            using var db = new HistoryStore(Path.Combine(root, "formats"));
            var rich = Text("格式化文字"); rich.Html = "<b>格式化文字</b>"; rich.Rtf = "{\\rtf1 test}";
            var id = db.Upsert(rich); var back = db.Get(id)!;
            Check(back.Html == rich.Html && back.Rtf == rich.Rtf, "Rich text lost");
            var files = new ClipPayload { Kind = ClipKind.Files, Files = [@"C:\中文路径\hello.txt"], WasCut = true };
            Check(db.Get(db.Upsert(files))!.Files!.Single() == files.Files[0], "File list lost");
            var image = new ClipPayload { Kind = ClipKind.Image, Png = TinyPng(), Text = "4 × 4" };
            Check(db.Get(db.Upsert(image))!.Png!.SequenceEqual(image.Png), "Image lost");
            Check(ClipboardService.LoadImage(image.Png).PixelWidth == 4, "Image decode failed");
        });
        Test("Clipboard parser, privacy opt-out flags and oversize limits", () =>
        {
            var data = new DataObject(); data.SetText("剪切板解析测试");
            Check(ClipboardService.Read(data)?.Text == "剪切板解析测试", "Text parser failed");
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            Check(ClipboardService.Excluded(data), "History opt-out ignored");
            var included = new DataObject(); included.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(1)));
            Check(!ClipboardService.Excluded(included), "History opt-in rejected");
            included.SetData("ExcludeClipboardContentFromMonitorProcessing", new byte[] { 1 });
            Check(ClipboardService.Excluded(included), "Monitor opt-out ignored");
            var tooLong = new DataObject(); tooLong.SetText(new string('x', 2_000_001));
            Check(ClipboardService.Read(tooLong) == null, "Oversize text accepted");
            var pngData = new DataObject(); pngData.SetData("PNG", new MemoryStream(TinyPng()));
            Check(ClipboardService.Read(pngData)?.Kind == ClipKind.Image, "PNG parser failed");
        });
        Test("Settings persistence, recovery and sound synthesis", () =>
        {
            var file = new SettingsFile(root); var settings = new AppSettings { RetentionDays = 90, Volume = 57, HotkeyModifiers = 3, HotkeyKey = 0x51, StartWithWindows = false };
            file.Save(settings); var loaded = file.Load();
            Check(loaded.RetentionDays == 90 && loaded.Volume == 57 && loaded.HotkeyDisplay == "Ctrl + Alt + Q", "Settings round trip failed");
            Check(loaded.MuteAutomaticScreenshots, "Default screenshot setting lost");
            settings.MuteAutomaticScreenshots = false; file.Save(settings);
            Check(!file.Load().MuteAutomaticScreenshots, "Screenshot sound opt-out lost");
            File.WriteAllText(file.FilePath, "{\"CopySoundEnabled\":true,\"Volume\":27}");
            Check(file.Load().MuteAutomaticScreenshots && file.Load().Volume == 27, "Old settings migration failed");
            File.WriteAllText(file.FilePath, "broken json");
            Check(file.Load().RetentionDays == 365 && Directory.GetFiles(root, "*.invalid-*").Length == 1, "Settings recovery failed");
            foreach (var tone in SoundService.Tones.Where(x => x != "自定义 WAV"))
            {
                var wav = SoundService.Generate(tone);
                Check(Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && BitConverter.ToInt32(wav, 40) == wav.Length - 44, "Invalid WAV: " + tone);
            }
        });
        Test("Screenshot auto-copy is silent while ordinary and explicit image copies keep feedback", () =>
        {
            var settings = new AppSettings();
            foreach (var source in new[] { "SnippingTool", "screenclippinghost", "ScreenSketch.exe" })
            {
                var image = new ClipPayload { Kind = ClipKind.Image, Source = source, Png = TinyPng() };
                Check(!SoundPolicy.ShouldPlayForCapture(settings, image), "Automatic screenshot was not muted: " + source);
                image.WasExplicitCopy = true;
                Check(SoundPolicy.ShouldPlayForCapture(settings, image), "Explicit Ctrl+C was muted: " + source);
                image.WasExplicitCopy = false; image.WasCut = true;
                Check(SoundPolicy.ShouldPlayForCapture(settings, image), "Explicit cut was muted");
                image.WasCut = false; settings.MuteAutomaticScreenshots = false;
                Check(SoundPolicy.ShouldPlayForCapture(settings, image), "Screenshot option off still muted");
                settings.MuteAutomaticScreenshots = true;
            }
            foreach (var source in new[] { "msedge", "chrome", "mspaint", "Photos", "explorer", "应用", "MySnippingTool" })
                Check(SoundPolicy.ShouldPlayForCapture(settings, new() { Kind = ClipKind.Image, Source = source }), "Ordinary image was muted: " + source);
            Check(SoundPolicy.ShouldPlayForCapture(settings, new() { Kind = ClipKind.Text, Source = "SnippingTool", Text = "OCR text" }), "Screenshot text extraction was muted");
            Check(SoundPolicy.ShouldPlayForCapture(settings, new() { Kind = ClipKind.Files, Source = "SnippingTool" }), "File copy was muted");
            using var history = new HistoryStore(Path.Combine(root, "screenshot-policy"));
            var screenshot = new ClipPayload { Kind = ClipKind.Image, Source = "SnippingTool", Png = TinyPng(), Text = "4 × 4" };
            Check(!SoundPolicy.ShouldPlayForCapture(settings, screenshot), "Screenshot should be silent");
            var id = history.Upsert(screenshot);
            Check(history.Get(id)?.Png?.Length > 0 && history.Stats().Count == 1, "Silent screenshot was excluded from history");
        });
        if (args.Contains("--clipboard")) Test("Live Windows clipboard listener, repeated content, exclusions and restore", () =>
        {
            using var producer = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Shitie test producer") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
            var original = NativeClipboard.Backup(producer.Handle);
            results.Add("Original clipboard detected type: " + (NativeClipboard.Read(producer.Handle, null, null)?.Kind.ToString() ?? "empty/excluded/unsupported"));
            var captured = new List<ClipPayload>(); using var got = new AutoResetEvent(false);
            using var service = new ClipboardService();
            service.Captured += payload => { lock (captured) captured.Add(payload); got.Set(); };
            void Put(Dictionary<uint, byte[]> formats)
            {
                for (var attempt = 0; ; attempt++)
                {
                    try { NativeClipboard.Write(producer.Handle, formats); return; }
                    catch (ExternalException) when (attempt < 10) { PumpUntil(() => false, 80); }
                }
            }
            try
            {
                Put(NativeClipboard.Formats(Text("拾贴集成测试-A")));
                Check(PumpUntil(() => got.WaitOne(0), 3000), "No clipboard event");
                Put(NativeClipboard.Formats(Text("拾贴集成测试-A")));
                Check(PumpUntil(() => got.WaitOne(0), 3000), "Repeated text was not detected");
                var write = service.WriteAsync(Text("拾贴集成测试-B"));
                Check(PumpUntil(() => write.IsCompleted, 4000), "Clipboard write timed out");
                write.GetAwaiter().GetResult();
                Check(!PumpUntil(() => got.WaitOne(0), 400), "Own clipboard write recaptured");
                var hidden = NativeClipboard.Formats(Text("拾贴不记录测试")); hidden[Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing")] = [1];
                Put(hidden);
                Check(!PumpUntil(() => got.WaitOne(0), 400), "Excluded data was captured");
                lock (captured) Check(captured.Count == 2 && captured.All(x => x.Text == "拾贴集成测试-A"), "Unexpected capture count");
                Check(service.RegisterAsync(3, 0x76).Result, "Test hotkey registration failed");
                Check(Native.RegisterHotKey(producer.Handle, 9, 3, 0x77), "Could not reserve conflict hotkey");
                Check(!service.RegisterAsync(3, 0x77).Result, "Hotkey conflict not reported");
                Check(!Native.RegisterHotKey(producer.Handle, 10, 3, 0x76), "Original hotkey was not restored");
                var png = new ClipPayload { Kind = ClipKind.Image, Png = TinyPng(), Text = "4 × 4" };
                Put(NativeClipboard.Formats(png));
                Check(PumpUntil(() => got.WaitOne(0), 3000), "PNG not captured");
                lock (captured) Check(captured.Last().Kind == ClipKind.Image && captured.Last().Png!.SequenceEqual(png.Png!), "Live image roundtrip failed");
                var existingOwner = NativeClipboard.Read(producer.Handle, null, null, new("SnippingTool", uint.MaxValue));
                Check(existingOwner?.Source != "SnippingTool", "A source hint overrode the actual clipboard producer");
                var dibOnly = NativeClipboard.Formats(png); dibOnly.Remove(Native.RegisterClipboardFormat("PNG")); Put(dibOnly);
                Check(PumpUntil(() => got.WaitOne(0), 3000), "DIB image not captured");
                lock (captured) Check(captured.Last().Kind == ClipKind.Image, "DIB parsed as wrong type");
                var filePath = Path.Combine(root, "文件剪切测试.txt"); File.WriteAllText(filePath, "test");
                var fileData = NativeClipboard.Formats(new ClipPayload { Kind = ClipKind.Files, Files = [filePath] });
                fileData[Native.RegisterClipboardFormat("Preferred DropEffect")] = BitConverter.GetBytes(2); Put(fileData);
                Check(PumpUntil(() => got.WaitOne(0), 3000), "File cut not captured");
                lock (captured) Check(captured.Last().WasCut && captured.Last().Files!.Single() == filePath, "File cut classification failed");
            }
            catch (Exception ex)
            {
                results.Add("LIVE_DETAIL " + ex);
                throw;
            }
            finally
            {
                service.Enabled = false;
                Native.UnregisterHotKey(producer.Handle, 9); Native.UnregisterHotKey(producer.Handle, 10);
                Put(original);
            }
        });
        results.Add($"\n{results.Count(x => x.StartsWith("PASS"))} passed, {failed} failed. {DateTimeOffset.Now:O}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        File.WriteAllLines(report, results, Encoding.UTF8);
        // Only remove the unique directory created by this test run.
        try { Directory.Delete(root, true); } catch { }
        return failed == 0 ? 0 : 1;
    }
    internal static byte[] TinyPng()
    {
        var pixels = Enumerable.Repeat(new byte[] { 190, 170, 60, 255 }, 16).SelectMany(x => x).ToArray();
        var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static bool PumpUntil(Func<bool> ready, int timeout)
    {
        if (ready()) return true;
        var frame = new System.Windows.Threading.DispatcherFrame();
        var watch = Stopwatch.StartNew(); var complete = false;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { complete = ready(); if (complete || watch.ElapsedMilliseconds >= timeout) frame.Continue = false; };
        timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); timer.Stop();
        return complete;
    }
}
