namespace Shitie.Core;

public enum ClipKind { Text, Image, Files }

public sealed class ClipPayload
{
    public ClipKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string? Html { get; set; }
    public string? Rtf { get; set; }
    public byte[]? Png { get; set; }
    public string[]? Files { get; set; }
    public string Source { get; set; } = "应用";
    public bool WasCut { get; set; }
    public bool WasExplicitCopy { get; set; }
    public string Preview => Kind switch
    {
        ClipKind.Image => "图片 · " + Text,
        ClipKind.Files => string.Join("\n", (Files ?? []).Select(Path.GetFileName)),
        _ => Text
    };
    public string Fingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        Add(Kind.ToString());
        if (Kind == ClipKind.Image) hash.AppendData(Png ?? []);
        else if (Kind == ClipKind.Files) foreach (var file in Files ?? []) Add(file);
        else { Add(Text); Add(Html ?? ""); Add(Rtf ?? ""); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

public sealed class ClipEntry
{
    public long Id { get; set; }
    public ClipKind Kind { get; set; }
    public long LastUsed { get; set; }
    public string Preview { get; set; } = "";
    public string Source { get; set; } = "";
    public bool WasCut { get; set; }
    public string KindLabel => Kind switch { ClipKind.Image => "图片", ClipKind.Files => "文件", _ => "文字" };
    public string Glyph => Kind switch { ClipKind.Image => "\uE91B", ClipKind.Files => "\uE8B7", _ => "\uE8D2" };
    public string Meta => $"{DateTimeOffset.FromUnixTimeMilliseconds(LastUsed).LocalDateTime:MM-dd HH:mm}  ·  {Source}" + (WasCut ? "  ·  剪切" : "");
}

public sealed class AppSettings
{
    public bool HistoryEnabled { get; set; } = true;
    public bool CopySoundEnabled { get; set; } = true;
    public bool CutSoundEnabled { get; set; } = true;
    public bool MuteAutomaticScreenshots { get; set; } = true;
    public string CopyTone { get; set; } = "轻响";
    public string CutTone { get; set; } = "双响";
    public string CopySoundPath { get; set; } = "";
    public string CutSoundPath { get; set; } = "";
    public double Volume { get; set; } = 35;
    public int RetentionDays { get; set; } = 365;
    public int MaximumStorageMb { get; set; } = 512;
    public uint HotkeyModifiers { get; set; } = 6;
    public uint HotkeyKey { get; set; } = 0x56;
    public bool StartWithWindows { get; set; } = true;
    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;
    public void Normalize()
    {
        Volume = double.IsFinite(Volume) ? Math.Clamp(Volume, 0, 100) : 35;
        RetentionDays = Math.Clamp(RetentionDays, 0, 36500);
        MaximumStorageMb = Math.Clamp(MaximumStorageMb, 64, 4096);
        if ((HotkeyModifiers & 0xB) == 0 || HotkeyKey < 0x20 || HotkeyKey > 0xFE) { HotkeyModifiers = 6; HotkeyKey = 0x56; }
        CopySoundPath ??= "";
        CutSoundPath ??= "";
        CopyTone ??= "轻响";
        CutTone ??= "双响";
    }
    public string HotkeyDisplay => HotkeyText(HotkeyModifiers, HotkeyKey);
    public static string HotkeyText(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        if ((modifiers & 8) != 0) parts.Add("Win");
        parts.Add(key is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 ? ((char)key).ToString() : ((System.Windows.Forms.Keys)key).ToString());
        return string.Join(" + ", parts);
    }
}

public sealed class SettingsFile(string root)
{
    public string FilePath { get; } = Path.Combine(root, "settings.json");
    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
            value.Normalize();
            return value;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Preserve the original so a damaged settings file never destroys history.
            File.Copy(FilePath, FilePath + ".invalid-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
            return new();
        }
    }
    public void Save(AppSettings value)
    {
        value.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        File.Move(temp, FilePath, true);
    }
}
