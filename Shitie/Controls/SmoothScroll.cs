using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Shitie.Controls;

public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(ScrollState), typeof(SmoothScroll));

    public static bool GetIsEnabled(DependencyObject owner) => (bool)owner.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject owner, bool enabled) => owner.SetValue(IsEnabledProperty, enabled);
    internal static bool IsAnimating(DependencyObject owner) => (owner.GetValue(StateProperty) as ScrollState)?.IsAnimating == true;
    internal static void Cancel(DependencyObject owner) => (owner.GetValue(StateProperty) as ScrollState)?.Stop();

    private static void EnabledChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is not FrameworkElement element) return;
        (owner.GetValue(StateProperty) as ScrollState)?.Dispose();
        owner.ClearValue(StateProperty);
        if ((bool)e.NewValue) owner.SetValue(StateProperty, new ScrollState(element));
    }

    internal static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private static T? Ancestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = current is Visual or Visual3D ? VisualTreeHelper.GetParent(current)
                : (current as FrameworkContentElement)?.Parent;
        }
        return null;
    }

    private sealed class ScrollState : IDisposable
    {
        private readonly FrameworkElement _root;
        private ScrollViewer? _viewer;
        private double _destination;
        private TimeSpan? _lastFrame;
        public bool IsAnimating { get; private set; }

        public ScrollState(FrameworkElement root)
        {
            _root = root;
            // Keep item recycling, but allow cards to be partially visible.
            if (root is ItemsControl) root.SetCurrentValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Pixel);
            root.Loaded += Loaded;
            root.Unloaded += Unloaded;
            root.IsVisibleChanged += VisibilityChanged;
            root.PreviewMouseWheel += MouseWheel;
            root.PreviewMouseDown += MouseDown;
            root.PreviewKeyDown += KeyDown;
            root.PreviewTouchDown += TouchDown;
        }

        private ScrollViewer? Viewer()
        {
            if (_viewer != null) return _viewer;
            if (_root is Control control) control.ApplyTemplate();
            _viewer = _root as ScrollViewer ?? FindChild<ScrollViewer>(_root);
            return _viewer;
        }
        private void Loaded(object sender, RoutedEventArgs e) { _viewer = null; Viewer(); }
        private void Unloaded(object sender, RoutedEventArgs e) { Stop(); _viewer = null; }
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!(bool)e.NewValue) Stop(); }
        private void MouseDown(object sender, MouseButtonEventArgs e) => Stop();
        private void TouchDown(object? sender, TouchEventArgs e) => Stop();
        private void KeyDown(object sender, KeyEventArgs e) => Stop();

        private void MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || e.Delta == 0 || Viewer() is not { } viewer) return;
            var source = e.OriginalSource as DependencyObject;
            var innerViewer = Ancestor<ScrollViewer>(source);
            if (innerViewer != null && innerViewer != viewer) return;
            if (Ancestor<ComboBox>(source) != null) return;
            if (viewer.ScrollableHeight <= 0 || viewer.ViewportHeight <= 0) return;

            var lines = SystemParameters.WheelScrollLines;
            var step = lines < 0 ? viewer.ViewportHeight * .9 : Math.Max(0, lines) * 32d;
            var delta = -e.Delta / 120d * step;
            var current = viewer.VerticalOffset;
            // Reversing the wheel starts from the visible position, without
            // making the user first cancel accumulated movement the other way.
            if (!IsAnimating || Math.Sign(delta) != Math.Sign(_destination - current)) _destination = current;
            _destination = Math.Clamp(_destination + delta, 0, viewer.ScrollableHeight);
            e.Handled = true;

            if (!SystemParameters.ClientAreaAnimation)
            {
                viewer.ScrollToVerticalOffset(_destination);
                Stop();
                return;
            }
            if (Math.Abs(_destination - current) < .2) return;
            if (IsAnimating) return;
            _lastFrame = null;
            IsAnimating = true;
            CompositionTarget.Rendering += Render;
        }

        private void Render(object? sender, EventArgs e)
        {
            if (!_root.IsVisible || _viewer is not { } viewer) { Stop(); return; }
            var timestamp = ((RenderingEventArgs)e).RenderingTime;
            if (_lastFrame == timestamp) return;
            var elapsed = _lastFrame.HasValue ? (timestamp - _lastFrame.Value).TotalSeconds : 1d / 60;
            _lastFrame = timestamp;
            _destination = Math.Clamp(_destination, 0, viewer.ScrollableHeight);
            var remaining = _destination - viewer.VerticalOffset;
            if (Math.Abs(remaining) < .2)
            {
                viewer.ScrollToVerticalOffset(_destination);
                Stop();
                return;
            }
            // Time-based easing gives the same feel on 60/120/144 Hz displays.
            var blend = 1 - Math.Exp(-Math.Clamp(elapsed, 0, .05) / .045);
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + remaining * blend);
        }

        public void Stop()
        {
            if (IsAnimating) CompositionTarget.Rendering -= Render;
            IsAnimating = false;
            _lastFrame = null;
        }
        public void Dispose()
        {
            Stop();
            _root.Loaded -= Loaded;
            _root.Unloaded -= Unloaded;
            _root.IsVisibleChanged -= VisibilityChanged;
            _root.PreviewMouseWheel -= MouseWheel;
            _root.PreviewMouseDown -= MouseDown;
            _root.PreviewKeyDown -= KeyDown;
            _root.PreviewTouchDown -= TouchDown;
        }
    }
}
