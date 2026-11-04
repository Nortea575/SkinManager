using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace SkinManager.Desktop;

internal static class SmoothScrollBehavior
{
    private static readonly ConditionalWeakTable<ScrollViewer, ScrollController> Controllers = new();

    internal static void Attach(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var scrollViewer in FindVisualChildren<ScrollViewer>(root))
        {
            Attach(scrollViewer);
        }
    }

    internal static void Attach(ScrollViewer scrollViewer)
    {
        ArgumentNullException.ThrowIfNull(scrollViewer);
        var controller = Controllers.GetValue(scrollViewer, viewer => new ScrollController(viewer));
        controller.Attach();
    }

    internal static bool RequestScroll(ScrollViewer scrollViewer, int wheelDelta)
    {
        ArgumentNullException.ThrowIfNull(scrollViewer);
        var controller = Controllers.GetValue(scrollViewer, viewer => new ScrollController(viewer));
        return controller.RequestScroll(wheelDelta);
    }

    internal static bool IsAnimating(ScrollViewer scrollViewer) =>
        Controllers.TryGetValue(scrollViewer, out var controller) && controller.IsAnimating;

    internal static bool IsAttached(ScrollViewer scrollViewer) => Controllers.TryGetValue(scrollViewer, out _);

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class ScrollController
    {
        private readonly ScrollViewer viewer;
        private readonly DispatcherTimer _timer;
        private readonly MouseWheelEventHandler _wheelHandler;
        private readonly KeyEventHandler _keyDownHandler;
        private readonly MouseButtonEventHandler _mouseDownHandler;
        private readonly DragStartedEventHandler _thumbDragStartedHandler;
        private double _fromOffset;
        private double _targetOffset;
        private long _startedAt;
        private bool _wheelHandlerAttached;
        private bool _tickHandlerAttached;
        private const double DurationMilliseconds = 190;

        internal bool IsAnimating => _timer.IsEnabled;
        internal double TargetOffset => _targetOffset;

        internal ScrollController(ScrollViewer viewer)
        {
            this.viewer = viewer;
            _timer = new DispatcherTimer(DispatcherPriority.Render, viewer.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _wheelHandler = OnPreviewMouseWheel;
            _keyDownHandler = OnPreviewKeyDown;
            _mouseDownHandler = OnPreviewMouseDown;
            _thumbDragStartedHandler = OnThumbDragStarted;
        }

        internal void Attach()
        {
            if (!viewer.IsLoaded)
            {
                viewer.Loaded -= Viewer_Loaded;
                viewer.Loaded += Viewer_Loaded;
            }

            viewer.Unloaded -= Viewer_Unloaded;
            viewer.Unloaded += Viewer_Unloaded;
            if (viewer.IsLoaded)
            {
                AttachWheelHandler();
            }
        }

        internal bool RequestScroll(int wheelDelta)
        {
            if (wheelDelta == 0 || Keyboard.Modifiers != ModifierKeys.None || viewer.ScrollableHeight <= 0)
            {
                return false;
            }

            var lines = SystemParameters.WheelScrollLines;
            if (lines == 0)
            {
                return false;
            }

            var stride = lines < 0
                ? Math.Max(32, viewer.ViewportHeight * 0.82)
                : Math.Max(28, lines * 28d);
            var basis = IsAnimating ? _targetOffset : viewer.VerticalOffset;
            var target = Math.Clamp(basis - (wheelDelta / 120d * stride), 0, viewer.ScrollableHeight);
            if (Math.Abs(target - basis) < 0.5)
            {
                return false;
            }

            if (!WindowMotion.AnimationsEnabled)
            {
                StopAnimation();
                viewer.ScrollToVerticalOffset(target);
                return true;
            }

            _fromOffset = viewer.VerticalOffset;
            _targetOffset = target;
            _startedAt = Stopwatch.GetTimestamp();
            _timer.Stop();
            if (!_tickHandlerAttached)
            {
                _timer.Tick += OnTick;
                _tickHandlerAttached = true;
            }
            _timer.Start();
            return true;
        }

        private void Viewer_Loaded(object sender, RoutedEventArgs e) => AttachWheelHandler();

        private void Viewer_Unloaded(object sender, RoutedEventArgs e)
        {
            StopAnimation();
            if (_wheelHandlerAttached)
            {
                viewer.RemoveHandler(UIElement.PreviewMouseWheelEvent, _wheelHandler);
                viewer.RemoveHandler(UIElement.PreviewKeyDownEvent, _keyDownHandler);
                viewer.RemoveHandler(UIElement.PreviewMouseDownEvent, _mouseDownHandler);
                viewer.RemoveHandler(Thumb.DragStartedEvent, _thumbDragStartedHandler);
                _wheelHandlerAttached = false;
            }
        }

        private void AttachWheelHandler()
        {
            if (_wheelHandlerAttached)
            {
                return;
            }

            viewer.AddHandler(UIElement.PreviewMouseWheelEvent, _wheelHandler, true);
            viewer.AddHandler(UIElement.PreviewKeyDownEvent, _keyDownHandler, true);
            viewer.AddHandler(UIElement.PreviewMouseDownEvent, _mouseDownHandler, true);
            viewer.AddHandler(Thumb.DragStartedEvent, _thumbDragStartedHandler, true);
            _wheelHandlerAttached = true;
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled)
            {
                return;
            }

            var nearestViewer = FindNearestScrollViewer(e.OriginalSource as DependencyObject);
            if (nearestViewer is not null && !ReferenceEquals(nearestViewer, viewer) &&
                CanScrollInDirection(nearestViewer, e.Delta))
            {
                return;
            }

            if (RequestScroll(e.Delta))
            {
                e.Handled = true;
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (IsExplicitScrollKey(e.Key) && !e.Handled &&
                ReferenceEquals(FindNearestScrollViewer(e.OriginalSource as DependencyObject), viewer))
            {
                StopAnimation();
            }
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null &&
                ReferenceEquals(FindNearestScrollViewer(e.OriginalSource as DependencyObject), viewer))
            {
                StopAnimation();
            }
        }

        private void OnThumbDragStarted(object sender, DragStartedEventArgs e)
        {
            if (ReferenceEquals(FindNearestScrollViewer(e.OriginalSource as DependencyObject), viewer))
            {
                StopAnimation();
            }
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (!viewer.IsLoaded)
            {
                StopAnimation();
                return;
            }

            if (!WindowMotion.AnimationsEnabled)
            {
                viewer.ScrollToVerticalOffset(_targetOffset);
                StopAnimation();
                return;
            }

            var elapsed = Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;
            var progress = Math.Clamp(elapsed / DurationMilliseconds, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            viewer.ScrollToVerticalOffset(_fromOffset + ((_targetOffset - _fromOffset) * eased));
            if (progress >= 1)
            {
                viewer.ScrollToVerticalOffset(_targetOffset);
                StopAnimation();
            }
        }

        private void StopAnimation()
        {
            _timer.Stop();
            if (_tickHandlerAttached)
            {
                _timer.Tick -= OnTick;
                _tickHandlerAttached = false;
            }
            if (viewer.IsLoaded && _targetOffset > viewer.ScrollableHeight)
            {
                _targetOffset = viewer.ScrollableHeight;
            }
        }

    }

    private static bool IsExplicitScrollKey(Key key) =>
        key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End;

    private static bool CanScrollInDirection(ScrollViewer scrollViewer, int wheelDelta)
    {
        if (scrollViewer.ScrollableHeight <= 0)
        {
            return false;
        }

        var offset = Controllers.TryGetValue(scrollViewer, out var controller) && controller.IsAnimating
            ? controller.TargetOffset
            : scrollViewer.VerticalOffset;
        return wheelDelta > 0 ? offset > 0.5 : offset < scrollViewer.ScrollableHeight - 0.5;
    }

    private static ScrollViewer? FindNearestScrollViewer(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            current = GetParent(current);
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = GetParent(current);
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject source) => source switch
    {
        FrameworkContentElement contentElement => contentElement.Parent,
        Visual or Visual3D => VisualTreeHelper.GetParent(source),
        _ => LogicalTreeHelper.GetParent(source)
    };
}
