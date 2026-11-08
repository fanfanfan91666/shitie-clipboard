using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Shitie.Core;
using Shitie.Windows;

namespace Shitie;

public partial class MainWindow : Window
{
    private readonly AppController _app;
    private readonly ObservableCollection<ClipEntry> _items = [];
    private readonly DispatcherTimer _searchTimer;
    private ClipKind? _filter;
    private int _limit = 80, _refreshVersion, _selectionVersion;
    private uint _hotkeyModifiers, _hotkeyKey;
    private bool _loaded;
    internal MainWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        // Fit the first launch to the usable screen area, including display scaling.
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(980, Math.Max(720, workArea.Width - 32));
        MinHeight = Math.Min(630, Math.Max(480, workArea.Height - 32));
        Width = Math.Min(1120, Math.Max(MinWidth, workArea.Width - 32));
        Height = Math.Min(770, Math.Max(MinHeight, workArea.Height - 32));
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await RefreshAsync(); };
        HistoryList.ItemsSource = _items;
        _loaded = true;
        _app.HistoryChanged += () => { if (IsVisible && HistoryView.Visibility == Visibility.Visible) { _searchTimer.Stop(); _searchTimer.Start(); } };
        _app.Notice += message => StatusLabel.Text = message;
        SourceInitialized += (_, _) => { var hwnd = new WindowInteropHelper(this).Handle; var round = 2; try { Native.DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); } catch (DllNotFoundException) { } };
        DataPathLabel.Text = app.DataRoot;
        CopyToneBox.ItemsSource = SoundService.Tones;
        CutToneBox.ItemsSource = SoundService.Tones;
        UpdateSettingsStatus();
    }
    public void UpdateSettingsStatus()
    {
        var settings = _app.Settings;
        RecordingLabel.Text = settings.HistoryEnabled ? "正在记录" : "已暂停记录";
        RecordingDot.Fill = new SolidColorBrush(settings.HistoryEnabled ? Color.FromRgb(99, 205, 183) : Color.FromRgb(224, 175, 103));
        HotkeyLabel.Text = settings.HotkeyDisplay;
        PauseButton.Content = settings.HistoryEnabled ? "暂停记录" : "继续记录";
    }
    public void ShowPanel(bool settings)
    {
        if (settings) LoadSettings();
        HistoryView.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsView.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        HistoryNav.Background = new SolidColorBrush(settings ? Colors.Transparent : Color.FromRgb(40, 71, 84));
        SettingsNav.Background = new SolidColorBrush(settings ? Color.FromRgb(40, 71, 84) : Colors.Transparent);
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        if (!settings) { _ = RefreshAsync(); SearchBox.Focus(); }
    }
    public void ToggleFromHotkey()
    {
        if (IsVisible && IsActive && WindowState != WindowState.Minimized) Hide();
        else ShowPanel(false);
    }
    public async Task RefreshAsync()
    {
        var version = ++_refreshVersion;
        try
        {
            var selected = (HistoryList.SelectedItem as ClipEntry)?.Id;
            var result = await _app.LoadAsync(SearchBox.Text.Trim(), _filter, _limit);
            if (version != _refreshVersion) return;
            _items.Clear(); foreach (var item in result.Entries) _items.Add(item);
            StatsLabel.Text = $"{result.Count:N0} 条记录  ·  {result.Bytes / 1048576d:0.0} MB";
            EmptyPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var searching = SearchBox.Text.Length > 0 || _filter.HasValue;
            EmptyTitle.Text = searching ? "没有找到匹配内容" : "从下一次复制开始";
            EmptyHint.Text = searching ? "试试其他关键词，或切换内容类型。" : "复制一段文字、一张图片或一个文件，\n拾贴会替你保存在这里。";
            ResultLabel.Text = _items.Count == 0 ? "支持文字、图片和文件路径" : $"已显示 {_items.Count} 条 · 选择预览，双击复制";
            LoadMoreButton.Visibility = _items.Count == _limit ? Visibility.Visible : Visibility.Collapsed;
            HistoryList.SelectedItem = _items.FirstOrDefault(x => x.Id == selected) ?? _items.FirstOrDefault();
        }
        catch (Exception ex) { StatusLabel.Text = "读取历史失败：" + ex.Message; }
    }
    private async void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var version = ++_selectionVersion;
        var item = HistoryList.SelectedItem as ClipEntry;
        PreviewText.Text = ""; PreviewImage.Source = null;
        PreviewText.Visibility = Visibility.Visible; PreviewImageScroll.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = DeleteButton.IsEnabled = item != null;
        PreviewMeta.Text = item == null ? "选择一条记录，查看完整内容" : item.KindLabel + " · " + item.Meta;
        if (item == null) return;
        try
        {
            var payload = await _app.GetAsync(item.Id);
            if (version != _selectionVersion || payload == null) return;
            if (payload.Kind == ClipKind.Image)
            {
                PreviewImage.Source = ClipboardService.LoadImage(payload.Png!);
                PreviewImageScroll.Visibility = Visibility.Visible; PreviewText.Visibility = Visibility.Collapsed;
            }
            else PreviewText.Text = payload.Kind == ClipKind.Files ? string.Join("\n\n", payload.Files ?? []) : payload.Text;
        }
        catch (Exception ex) { StatusLabel.Text = "预览未能打开：" + ex.Message; }
    }
    private void HistoryNavClick(object sender, RoutedEventArgs e) => ShowPanel(false);
    private void SettingsNavClick(object sender, RoutedEventArgs e) => ShowPanel(true);
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded) return;
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _limit = 80; _searchTimer.Stop(); _searchTimer.Start();
    }
    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        var tag = (sender as RadioButton)?.Tag?.ToString();
        _filter = Enum.TryParse<ClipKind>(tag, out var kind) ? kind : null;
        _limit = 80; _ = RefreshAsync();
    }
    private void ClearSearchClick(object sender, RoutedEventArgs e) => SearchBox.Clear();
    private async void LoadMoreClick(object sender, RoutedEventArgs e) { _limit += 80; await RefreshAsync(); }
    private async void PauseClick(object sender, RoutedEventArgs e) => await _app.ToggleHistoryAsync();
    private async void CopyClick(object sender, RoutedEventArgs e) => await CopySelectedAsync();
    private async Task CopySelectedAsync()
    {
        if (HistoryList.SelectedItem is not ClipEntry item) return;
        CopyButton.IsEnabled = false;
        StatusLabel.Text = "正在复制…";
        try { await _app.CopyAsync(item.Id); StatusLabel.Text = "已复制到剪切板，并移到历史最前面 · 返回原窗口后粘贴即可"; await RefreshAsync(); }
        catch (Exception ex) { StatusLabel.Text = "未能复制：" + ex.Message; }
        finally { CopyButton.IsEnabled = HistoryList.SelectedItem != null; }
    }
    private async void DeleteClick(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not ClipEntry item) return;
        try { await _app.DeleteAsync(item.Id); await RefreshAsync(); StatusLabel.Text = "已删除这条历史记录"; }
        catch (Exception ex) { StatusLabel.Text = "未能删除：" + ex.Message; }
    }
    private async void ClearHistoryClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "确定删除所有历史记录吗？这个操作无法撤销。\n当前系统剪切板中的内容不会被清除。", "清空剪切板历史", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try { await _app.ClearAsync(); await RefreshAsync(); StatusLabel.Text = "历史记录已清空"; }
        catch (Exception ex) { StatusLabel.Text = "未能清空历史：" + ex.Message; }
    }
    private async void HistoryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(HistoryList, e.OriginalSource as DependencyObject) is ListBoxItem) await CopySelectedAsync();
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!_app.Exiting) { e.Cancel = true; Hide(); } }
    private void HideClick(object sender, RoutedEventArgs e) => Hide();
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private async void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (HotkeyBox.IsKeyboardFocused && SettingsView.Visibility == Visibility.Visible) return;
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        if (HistoryView.Visibility != Visibility.Visible) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        if (e.Key == Key.Enter && (HistoryList.IsKeyboardFocusWithin || SearchBox.IsKeyboardFocusWithin)) { e.Handled = true; await CopySelectedAsync(); }
        if (e.Key == Key.Delete && HistoryList.IsKeyboardFocusWithin) { e.Handled = true; DeleteClick(sender, e); }
        if (e.Key == Key.Down && SearchBox.IsKeyboardFocusWithin && _items.Count > 0) { HistoryList.Focus(); HistoryList.SelectedIndex = 0; e.Handled = true; }
    }
    private void LoadSettings()
    {
        var s = _app.Settings;
        HistoryEnabledCheck.IsChecked = s.HistoryEnabled; StartupCheck.IsChecked = s.StartWithWindows;
        CopySoundCheck.IsChecked = s.CopySoundEnabled; CutSoundCheck.IsChecked = s.CutSoundEnabled;
        MuteScreenshotsCheck.IsChecked = s.MuteAutomaticScreenshots;
        CopyToneBox.SelectedItem = s.CopyTone; CutToneBox.SelectedItem = s.CutTone;
        CopyPathBox.Text = s.CopySoundPath; CutPathBox.Text = s.CutSoundPath;
        VolumeSlider.Value = s.Volume;
        SelectTagged(RetentionBox, s.RetentionDays); SelectTagged(StorageBox, s.MaximumStorageMb);
        _hotkeyModifiers = s.HotkeyModifiers; _hotkeyKey = s.HotkeyKey; HotkeyBox.Text = s.HotkeyDisplay;
        SettingsFeedback.Text = "修改后点击保存生效"; SettingsFeedback.Foreground = (Brush)FindResource("Muted");
    }
    private static void SelectTagged(ComboBox box, int value)
    {
        var item = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => Convert.ToInt32(x.Tag) == value);
        if (item == null) { item = new ComboBoxItem { Content = value == 0 ? "永久保留" : value.ToString(), Tag = value }; box.Items.Add(item); }
        box.SelectedItem = item;
    }
    private AppSettings ReadSettings()
    {
        var s = _app.Settings.Clone();
        s.HistoryEnabled = HistoryEnabledCheck.IsChecked == true; s.StartWithWindows = StartupCheck.IsChecked == true;
        s.CopySoundEnabled = CopySoundCheck.IsChecked == true; s.CutSoundEnabled = CutSoundCheck.IsChecked == true;
        s.MuteAutomaticScreenshots = MuteScreenshotsCheck.IsChecked == true;
        s.CopyTone = CopyToneBox.SelectedItem as string ?? "轻响"; s.CutTone = CutToneBox.SelectedItem as string ?? "双响";
        s.CopySoundPath = CopyPathBox.Text; s.CutSoundPath = CutPathBox.Text; s.Volume = VolumeSlider.Value;
        s.RetentionDays = Convert.ToInt32((RetentionBox.SelectedItem as ComboBoxItem)?.Tag ?? 365);
        s.MaximumStorageMb = Convert.ToInt32((StorageBox.SelectedItem as ComboBoxItem)?.Tag ?? 512);
        s.HotkeyModifiers = _hotkeyModifiers; s.HotkeyKey = _hotkeyKey;
        return s;
    }
    private async void SaveSettingsClick(object sender, RoutedEventArgs e)
    {
        SaveSettingsButton.IsEnabled = false;
        try
        {
            var settings = ReadSettings();
            foreach (var pair in new[] { (settings.CopyTone, settings.CopySoundPath), (settings.CutTone, settings.CutSoundPath) })
                if (pair.Item1 == "自定义 WAV" && (!File.Exists(pair.Item2) || !Path.GetExtension(pair.Item2).Equals(".wav", StringComparison.OrdinalIgnoreCase))) throw new IOException("请为自定义音效选择有效的 WAV 文件。");
            var shortening = settings.RetentionDays > 0 && (_app.Settings.RetentionDays == 0 || settings.RetentionDays < _app.Settings.RetentionDays);
            var shrinking = settings.MaximumStorageMb < _app.Settings.MaximumStorageMb;
            if ((shortening || shrinking) && MessageBox.Show(this, "缩短保留时间或减少空间上限，会立即删除不再符合设置的旧记录。\n是否保存并自动清理？", "应用历史保留设置", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            await _app.SaveAsync(settings);
            SettingsFeedback.Text = "设置已保存，立即生效"; SettingsFeedback.Foreground = (Brush)FindResource("Accent");
        }
        catch (Exception ex) { SettingsFeedback.Text = ex.Message; SettingsFeedback.Foreground = new SolidColorBrush(Color.FromRgb(180, 75, 57)); }
        finally { SaveSettingsButton.IsEnabled = true; }
    }
    private void HotkeyBoxKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        var modifiers = Keyboard.Modifiers;
        var validKey = key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.F1 and <= Key.F11;
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 || !validKey || (modifiers & ModifierKeys.Windows) != 0)
        { SettingsFeedback.Text = "请使用 Ctrl / Alt 加字母、数字或 F1–F11 的组合。"; return; }
        if (modifiers == ModifierKeys.Control && key is Key.C or Key.X or Key.V or Key.A or Key.Z)
        { SettingsFeedback.Text = "请加上 Shift 或 Alt，避免覆盖常用快捷键。"; return; }
        _hotkeyModifiers = (uint)modifiers; _hotkeyKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        HotkeyBox.Text = AppSettings.HotkeyText(_hotkeyModifiers, _hotkeyKey);
        SettingsFeedback.Text = "快捷键已选择，保存后生效";
    }
    private void PreviewCopySoundClick(object sender, RoutedEventArgs e) => _app.PreviewSound(ReadSettings(), false);
    private void PreviewCutSoundClick(object sender, RoutedEventArgs e) => _app.PreviewSound(ReadSettings(), true);
    private void BrowseCopySoundClick(object sender, RoutedEventArgs e) => BrowseSound(false);
    private void BrowseCutSoundClick(object sender, RoutedEventArgs e) => BrowseSound(true);
    private void BrowseSound(bool cut)
    {
        var dialog = new OpenFileDialog { Title = "选择提示音", Filter = "WAV 音频文件|*.wav", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        (cut ? CutPathBox : CopyPathBox).Text = dialog.FileName;
        (cut ? CutToneBox : CopyToneBox).SelectedItem = "自定义 WAV";
    }
    private void OpenDataFolderClick(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("explorer.exe", _app.DataRoot) { UseShellExecute = true });
}
