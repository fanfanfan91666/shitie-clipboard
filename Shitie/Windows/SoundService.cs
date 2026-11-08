using System.Windows.Media;
using Shitie.Core;

namespace Shitie.Windows;

internal sealed class SoundService : IDisposable
{
    private readonly string _root;
    private readonly MediaPlayer _player = new();
    private Uri? _opened;
    public static string[] Tones { get; } = ["轻响", "清脆", "木点", "双响", "自定义 WAV"];
    public event Action<string>? Failed;
    public SoundService(string root)
    {
        _root = Path.Combine(root, "sounds");
        Directory.CreateDirectory(_root);
        _player.MediaOpened += (_, _) => _player.Play();
        _player.MediaFailed += (_, e) => Failed?.Invoke("提示音播放失败：" + e.ErrorException.Message);
    }
    public void Play(AppSettings settings, bool cut, bool preview = false)
    {
        if (!preview && (cut ? !settings.CutSoundEnabled : !settings.CopySoundEnabled)) return;
        var tone = cut ? settings.CutTone : settings.CopyTone;
        try
        {
            var path = tone == "自定义 WAV" ? (cut ? settings.CutSoundPath : settings.CopySoundPath) : EnsureTone(tone);
            if (!File.Exists(path)) { Failed?.Invoke("找不到自定义音效，请在设置中重新选择 WAV 文件。"); return; }
            var uri = new Uri(Path.GetFullPath(path));
            _player.Volume = Math.Clamp(settings.Volume / 100, 0, 1);
            if (uri != _opened) { _opened = uri; _player.Open(uri); }
            else { _player.Position = TimeSpan.Zero; _player.Play(); }
        }
        catch (Exception ex) { Failed?.Invoke("提示音播放失败：" + ex.Message); }
    }
    private string EnsureTone(string tone)
    {
        var file = Path.Combine(_root, tone + "-v1.wav");
        if (File.Exists(file)) return file;
        var bytes = Generate(tone);
        File.WriteAllBytes(file, bytes);
        return file;
    }
    internal static byte[] Generate(string tone)
    {
        const int rate = 44100;
        var duration = tone == "双响" ? .24 : .13;
        var count = (int)(rate * duration);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + count * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(count * 2);
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / rate;
            double Bell(double start, double frequency, double decay)
            {
                var d = t - start; if (d < 0) return 0;
                var envelope = Math.Min(d / .004, 1) * Math.Exp(-d * decay);
                return Math.Sin(2 * Math.PI * frequency * d) * envelope;
            }
            var sample = tone switch
            {
                "双响" => (Bell(0, 620, 33) + Bell(.09, 920, 32)) * .5,
                "清脆" => Bell(0, 1480, 32) * .42 + Bell(0, 2220, 50) * .14,
                "木点" => Bell(0, 410, 58) * .65 + Bell(0, 780, 95) * .13,
                _ => Bell(0, 900, 36) * .5
            };
            var tail = Math.Min(1, (duration - t) / .012);
            writer.Write((short)(Math.Clamp(sample * tail, -1, 1) * 22000));
        }
        return stream.ToArray();
    }
    public void Dispose() => _player.Close();
}
