using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Shitie.Controls;
using Shitie.Core;

namespace Shitie;

internal static class ScrollTests
{
    internal sealed record Row(string Text, double Height);

    internal static string Run()
    {
        // A real WPF presentation source exercises layout, rendering and
        // virtualization together, instead of just testing an easing formula.
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var list = new ListBox
        {
            ItemsSource = Enumerable.Range(0, 500).Select(i => new Row($"第 {i + 1} 条滚动验证内容", 92 + i % 3 * 24)).ToList(),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(FrameworkElement.HeightProperty, new Binding(nameof(Row.Height)));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Text)));
        border.AppendChild(text);
        list.ItemTemplate = new DataTemplate { VisualTree = border };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        SmoothScroll.SetIsEnabled(list, true);
        var window = new Window
        {
            Title = "拾贴 · 平滑滚动验证", Width = 540, Height = 540,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = list, ShowInTaskbar = false
        };
        static void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
        void Pump(int milliseconds) => SelfTests.PumpUntil(() => false, milliseconds);
        void Wheel(int delta) => list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        void Finish()
        {
            Check(SelfTests.PumpUntil(() => !SmoothScroll.IsAnimating(list), 2500), "Scroll animation never settled");
            list.UpdateLayout(); Pump(30);
        }
        try
        {
            window.Show(); window.UpdateLayout(); Pump(100);
            var viewer = SmoothScroll.FindChild<ScrollViewer>(list) ?? throw new InvalidOperationException("Missing scroll viewer");
            var panel = SmoothScroll.FindChild<VirtualizingStackPanel>(list) ?? throw new InvalidOperationException("Virtualization was disabled");
            Check(VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Pixel && viewer.ViewportHeight > 100, "List still scrolls in item units");
            Check(panel.Children.Count < 100, "All 500 items were realized");
            var realized = panel.Children.Count;
            var step = SystemParameters.WheelScrollLines < 0 ? viewer.ViewportHeight * .9 : SystemParameters.WheelScrollLines * 32d;
            Check(step > 0, "Windows wheel scrolling is disabled");
            var offsets = new List<double>();
            viewer.ScrollChanged += (_, e) => { if (Math.Abs(e.VerticalChange) > .001) offsets.Add(viewer.VerticalOffset); };
            Wheel(-120);
            if (SystemParameters.ClientAreaAnimation) Check(SmoothScroll.IsAnimating(list), "Wheel did not start an animation");
            Finish();
            Check(Math.Abs(viewer.VerticalOffset - step) < 1, "One wheel notch scrolled the wrong pixel distance");
            if (SystemParameters.ClientAreaAnimation)
            {
                Check(offsets.Distinct().Count() >= 4, "Content jumped instead of passing through intermediate positions");
                Check(offsets.Any(x => x > 0 && x < step - 1), "No intermediate scroll position was rendered");
                Check(offsets.Zip(offsets.Skip(1), (a, b) => b >= a - .5).All(x => x), "Downward animation moved backwards");
            }
            var frames = offsets.Count;
            var start = viewer.VerticalOffset;
            Wheel(-120); Pump(45); Wheel(-120); Finish();
            Check(Math.Abs(viewer.VerticalOffset - start - 2 * step) < 1, "Continuous wheel input lost distance");
            Wheel(-120); Pump(50);
            var reversingFrom = viewer.VerticalOffset;
            Wheel(120); Finish();
            Check(viewer.VerticalOffset < reversingFrom, "Reversing the wheel continued in the old direction");
            start = viewer.VerticalOffset;
            for (var i = 0; i < 4; i++) Wheel(-30);
            Finish();
            Check(Math.Abs(viewer.VerticalOffset - start - step) < 1, "High precision wheel deltas were rounded away");
            Wheel(-120);
            list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
            Check(!SmoothScroll.IsAnimating(list), "Scrollbar/pointer interaction did not interrupt easing");
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + 17.5); list.UpdateLayout(); Pump(40);
            var dragged = viewer.VerticalOffset; Pump(250);
            Check(Math.Abs(viewer.VerticalOffset - dragged) < .5, "Animation pulled the scrollbar back after a manual move");
            viewer.ScrollToTop(); list.UpdateLayout(); Pump(60); Wheel(120); Finish();
            Check(viewer.VerticalOffset < .5 && !SmoothScroll.IsAnimating(list), "Top edge overscrolled or kept rendering");
            Wheel(-120); window.Hide();
            Check(!SmoothScroll.IsAnimating(list), "Hidden window kept its rendering subscription");
            return $"Scroll rendering: {frames} intermediate offsets, {realized}/500 realized containers; reversal, precision, manual takeover and idle cleanup passed. Windows animations: {SystemParameters.ClientAreaAnimation}.";
        }
        finally { window.Close(); SmoothScroll.SetIsEnabled(list, false); }
    }

    internal static void SeedDemo(string root)
    {
        new SettingsFile(root).Save(new AppSettings { HistoryEnabled = false, CopySoundEnabled = false, CutSoundEnabled = false, StartWithWindows = false });
        using var store = new HistoryStore(root);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < 160; i++)
        {
            var lines = (i % 3) switch
            {
                0 => "一段简短的文字，滚动时卡片可以停在任意位置。",
                1 => "这是一条较长的内容，用来检查不同高度的卡片。\n连续滚动、反向滚动以及拖动滚动条时，内容应当平稳移动。",
                _ => "连续滚动保留惯性目标，反向输入立即响应。\n设置页面也可以平滑滚动。\n演示内容独立存放，不修改日常使用的数据。"
            };
            store.Upsert(new() { Kind = ClipKind.Text, Text = $"滚动体验 · 第 {i + 1:000} 条\n{lines}", Source = "演示内容" }, now - i * 60_000);
        }
    }
}
