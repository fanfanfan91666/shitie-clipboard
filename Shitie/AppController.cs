using System.Threading.Channels;
using System.Windows;
using Shitie.Core;
using Shitie.Windows;
using Forms = System.Windows.Forms;

namespace Shitie;

internal sealed class AppController : IDisposable
{
    private readonly HistoryStore _store;
    private readonly SettingsFile _settingsFile;
    private readonly ClipboardService _clipboard;
    private readonly SoundService _sound;
    private readonly Channel<Action> _jobs = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _worker;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _pauseMenu;
    private readonly Forms.ToolStripMenuItem _historyMenu;
    private readonly System.Windows.Threading.DispatcherTimer _cleanup;
    private readonly bool _testProfile;
    private volatile AppSettings _settings;
    private bool _disposed;
    public string DataRoot { get; }
    public AppSettings Settings => _settings;
    public MainWindow Window { get; }
    public bool Exiting { get; private set; }
    public event Action? HistoryChanged;
    public event Action<string>? Notice;
    public AppController(string root, bool testProfile)
    {
        DataRoot = root; _testProfile = testProfile;
        _settingsFile = new(root);
        _settings = _settingsFile.Load();
        if (testProfile) _settings.StartWithWindows = false;
        _store = new(root);
        _worker = Task.Run(async () => { await foreach (var action in _jobs.Reader.ReadAllAsync()) action(); });
        _sound = new(root);
        _sound.Failed += Tell;
        _clipboard = new();
        _clipboard.Captured += Captured;
        _clipboard.Notice += Tell;
        _clipboard.HotkeyPressed += () => UI(() => Window?.ToggleFromHotkey());
        Window = new(this);
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, Font = new System.Drawing.Font("Microsoft YaHei UI", 10), BackColor = System.Drawing.Color.White, Padding = new Forms.Padding(6) };
        menu.Items.Add(new Forms.ToolStripMenuItem("拾贴 · 本地剪切板") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        _historyMenu = new Forms.ToolStripMenuItem("剪切板历史") { ShortcutKeyDisplayString = _settings.HotkeyDisplay };
        _historyMenu.Click += (_, _) => Show(false);
        menu.Items.Add(_historyMenu);
        _pauseMenu = new Forms.ToolStripMenuItem("暂停记录");
        _pauseMenu.Click += async (_, _) => await ToggleHistoryAsync();
        menu.Items.Add(_pauseMenu);
        menu.Items.Add("偏好设置", null, (_, _) => Show(true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出拾贴", null, (_, _) => Exit());
        using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/shitie.ico"))!.Stream;
        _tray = new Forms.NotifyIcon { Text = "拾贴 · 剪切板历史", ContextMenuStrip = menu, Icon = new System.Drawing.Icon(iconStream), Visible = true };
        _tray.DoubleClick += (_, _) => Show(false);
        _cleanup = new() { Interval = TimeSpan.FromMinutes(10) };
        _cleanup.Tick += async (_, _) =>
        {
            try { await RunStore(() => { _store.Cleanup(_settings); return true; }); HistoryChanged?.Invoke(); }
            catch (Exception ex) { Tell("自动清理暂未完成：" + ex.Message); }
        };
        _cleanup.Start();
        UpdateState();
    }
    private static void UI(Action action) => Application.Current.Dispatcher.BeginInvoke(action);
    private void Tell(string message) => UI(() => Notice?.Invoke(message));
    public async Task InitializeAsync()
    {
        try
        {
            await RunStore(() => { _store.Cleanup(_settings); return true; });
            if (!await _clipboard.RegisterAsync(_settings.HotkeyModifiers, _settings.HotkeyKey)) Tell("唤起快捷键已被其他程序占用，请在偏好设置中换一个组合。");
            if (!_testProfile) StartupRegistration.Apply(_settings.StartWithWindows);
            _settingsFile.Save(_settings);
            if (!_clipboard.GestureAvailable) Tell("独立剪切音效暂不可用；复制反馈和历史记录仍可使用。");
            await Window.RefreshAsync();
        }
        catch (Exception ex) { Tell("启动设置未完全应用：" + ex.Message); }
    }
    private void Captured(ClipPayload payload)
    {
        var settings = _settings;
        if (SoundPolicy.ShouldPlayForCapture(settings, payload)) UI(() => _sound.Play(settings, payload.WasCut));
        if (!settings.HistoryEnabled) return;
        _jobs.Writer.TryWrite(() =>
        {
            try { _store.Upsert(payload); _store.Cleanup(settings); UI(() => HistoryChanged?.Invoke()); }
            catch (Exception ex) { Tell("这条记录未能保存：" + ex.Message); }
        });
    }
    private Task<T> RunStore<T>(Func<T> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_jobs.Writer.TryWrite(() => { try { done.SetResult(action()); } catch (Exception ex) { done.SetException(ex); } })) done.SetException(new ObjectDisposedException(nameof(AppController)));
        return done.Task;
    }
    public Task<(List<ClipEntry> Entries, long Count, long Bytes)> LoadAsync(string query, ClipKind? kind, int limit) => RunStore(() => { var list = _store.List(query, kind, 0, limit); var stats = _store.Stats(); return (list, stats.Count, stats.Bytes); });
    public Task<ClipPayload?> GetAsync(long id) => RunStore(() => _store.Get(id));
    public async Task CopyAsync(long id)
    {
        var payload = await GetAsync(id) ?? throw new IOException("这条记录已经被删除。");
        await _clipboard.WriteAsync(payload);
        _sound.Play(_settings, false);
        await RunStore(() => { _store.Touch(id); return true; });
        HistoryChanged?.Invoke();
    }
    public async Task DeleteAsync(long id) { await RunStore(() => { _store.Delete(id); return true; }); HistoryChanged?.Invoke(); }
    public async Task ClearAsync() { await RunStore(() => { _store.Clear(); return true; }); HistoryChanged?.Invoke(); }
    public async Task ToggleHistoryAsync()
    {
        var next = _settings.Clone(); next.HistoryEnabled = !next.HistoryEnabled;
        try { await SaveAsync(next); }
        catch (Exception ex) { Tell("记录状态未能更新：" + ex.Message); }
    }
    public async Task SaveAsync(AppSettings settings)
    {
        settings.Normalize();
        var old = _settings;
        if (!await _clipboard.RegisterAsync(settings.HotkeyModifiers, settings.HotkeyKey)) throw new IOException("这个快捷键已被占用，请换一个组合；原快捷键继续有效。");
        try
        {
            if (_testProfile) settings.StartWithWindows = false;
            else StartupRegistration.Apply(settings.StartWithWindows);
            _settingsFile.Save(settings);
        }
        catch
        {
            await _clipboard.RegisterAsync(old.HotkeyModifiers, old.HotkeyKey);
            if (!_testProfile) { try { StartupRegistration.Apply(old.StartWithWindows); } catch { } }
            throw;
        }
        _settings = settings;
        UpdateState();
        await RunStore(() => { _store.Cleanup(settings); return true; });
        HistoryChanged?.Invoke();
    }
    private void UpdateState()
    {
        if (_pauseMenu != null) { _pauseMenu.Text = _settings.HistoryEnabled ? "暂停记录" : "继续记录"; _pauseMenu.Checked = !_settings.HistoryEnabled; }
        if (_historyMenu != null) _historyMenu.ShortcutKeyDisplayString = _settings.HotkeyDisplay;
        Window?.UpdateSettingsStatus();
    }
    public void PreviewSound(AppSettings settings, bool cut) => _sound.Play(settings, cut, true);
    public void Show(bool settings) { Window.ShowPanel(settings); }
    public void Exit() { if (Exiting) return; Exiting = true; Window.Close(); Application.Current.Shutdown(); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _cleanup.Stop();
        _clipboard.Dispose();
        _jobs.Writer.TryComplete();
        _worker.GetAwaiter().GetResult();
        _store.Dispose();
        _sound.Dispose();
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Icon?.Dispose();
        _tray.Dispose();
    }
}
