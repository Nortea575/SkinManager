using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;

namespace SkinManager.Desktop;

internal static class WindowMotion
{
    private static readonly ConditionalWeakTable<Window, WindowMotionState> States = new();
    private static readonly ConditionalWeakTable<UIElement, OpacityMotionState> OpacityStates = new();
    private static readonly Color CaptionColor = Color.FromRgb(0x18, 0x2A, 0x35);
    private static readonly Color CaptionTextColor = Color.FromRgb(0xEC, 0xF2, 0xF6);

    // A local override keeps motion tests independent of global Windows accessibility settings.
    internal static bool? AnimationEnabledOverrideForTests { get; set; }

    internal static bool AnimationsEnabled => AnimationEnabledOverrideForTests ??
        (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast);

    internal static void PrepareButtonPressFeedback(Button button)
    {
        if (button.RenderTransform is null || button.RenderTransform == Transform.Identity)
        {
            button.RenderTransformOrigin = new Point(0.5, 0.5);
            button.RenderTransform = new ScaleTransform(1, 1);
        }
    }

    internal static void AnimateButtonPressFeedback(Button button, bool pressed)
    {
        if (button.RenderTransform is not ScaleTransform scale)
        {
            return;
        }

        var currentX = scale.ScaleX;
        var currentY = scale.ScaleY;
        if ((pressed && !button.IsEnabled) || !AnimationsEnabled)
        {
            ResetButtonPressFeedback(scale);
            return;
        }

        var target = pressed ? 0.98 : 1;
        var duration = TimeSpan.FromMilliseconds(pressed ? 75 : 135);
        var xAnimation = new DoubleAnimation(currentX, target, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = pressed ? FillBehavior.HoldEnd : FillBehavior.Stop
        };
        var yAnimation = new DoubleAnimation(currentY, target, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = pressed ? FillBehavior.HoldEnd : FillBehavior.Stop
        };
        if (!pressed)
        {
            xAnimation.Completed += (_, _) =>
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            };
        }
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, xAnimation, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, yAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void ResetButtonPressFeedback(ScaleTransform scale)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
    }

    internal static void Attach(
        Window window,
        bool animateWindow = true,
        bool animateClose = true,
        bool animateButtonPress = true)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (States.TryGetValue(window, out _))
        {
            return;
        }

        var state = new WindowMotionState(window, animateWindow, animateClose, animateButtonPress);
        States.Add(window, state);
        state.Attach();
    }

    internal static void AttachBehaviors(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        SmoothScrollBehavior.Attach(root);
        ExpanderMotionBehavior.Attach(root);
        if (Window.GetWindow(root) is { } window && States.TryGetValue(window, out var state))
        {
            state.AttachButtons(root);
        }
    }

    internal static void AnimateOpacity(UIElement element, double targetOpacity, int durationMilliseconds, Action? completed = null)
    {
        var opacityState = OpacityStates.GetValue(element, static _ => new OpacityMotionState());
        var animationSequence = ++opacityState.Sequence;
        var fromOpacity = element.Opacity;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (!AnimationsEnabled)
        {
            element.Opacity = targetOpacity;
            completed?.Invoke();
            return;
        }

        element.Opacity = fromOpacity;
        var animation = new DoubleAnimation(fromOpacity, targetOpacity, TimeSpan.FromMilliseconds(durationMilliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (animationSequence != opacityState.Sequence)
            {
                return;
            }

            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = targetOpacity;
            completed?.Invoke();
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void ApplyDarkCaption(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        uint darkMode = 1;
        uint caption = ToColorRef(CaptionColor);
        uint captionText = ToColorRef(CaptionTextColor);
        _ = DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(uint));
        _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(uint));
        _ = DwmSetWindowAttribute(handle, 36, ref captionText, sizeof(uint));
    }

    private static uint ToColorRef(Color color) =>
        (uint)(color.R | (color.G << 8) | (color.B << 16));

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int valueSize);

    private sealed class WindowMotionState(
        Window window,
        bool animateWindow,
        bool animateClose,
        bool animateButtonPress)
    {
        private readonly HashSet<Button> _buttons = [];
        private bool _fadeOnLoad;
        private bool _closingAnimation;
        private bool _allowClose;
        private bool _closed;

        internal void Attach()
        {
            window.SourceInitialized += OnSourceInitialized;
            window.Loaded += OnLoaded;
            window.Closing += OnClosing;
            window.Closed += OnClosed;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            ApplyDarkCaption(window);
            if (animateWindow && AnimationsEnabled)
            {
                window.Opacity = 0;
                _fadeOnLoad = true;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_closingAnimation || _allowClose || _closed)
            {
                _fadeOnLoad = false;
                return;
            }

            AttachBehaviors(window);
            if (animateButtonPress)
            {
                foreach (var button in FindVisualChildren<Button>(window))
                {
                    AttachButton(button);
                }
            }

            if (_fadeOnLoad && AnimationsEnabled)
            {
                _fadeOnLoad = false;
                AnimateOpacity(window, 1, 165);
            }
            else
            {
                window.Opacity = 1;
            }
        }

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_allowClose || _closed || !animateWindow || !animateClose || !window.IsVisible ||
                _closingAnimation || !AnimationsEnabled || window.Owner is { IsVisible: false })
            {
                return;
            }

            var dialogResult = ReadDialogResult(window);
            e.Cancel = true;
            _closingAnimation = true;
            AnimateOpacity(window, 0, 135, () =>
            {
                _allowClose = true;
                _closingAnimation = false;
                if (dialogResult is bool result)
                {
                    try
                    {
                        window.DialogResult = result;
                        return;
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                window.Close();
            });
        }

        private static bool? ReadDialogResult(Window window)
        {
            try
            {
                return window.DialogResult;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _closed = true;
            foreach (var button in _buttons)
            {
                button.PreviewMouseDown -= Button_PreviewMouseDown;
                button.PreviewMouseUp -= Button_PreviewMouseUp;
                button.MouseLeave -= Button_MouseLeave;
                button.IsEnabledChanged -= Button_IsEnabledChanged;
                button.LostMouseCapture -= Button_LostMouseCapture;
                if (button.RenderTransform is ScaleTransform scale)
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    scale.ScaleX = 1;
                    scale.ScaleY = 1;
                }
            }

            _buttons.Clear();
            window.SourceInitialized -= OnSourceInitialized;
            window.Loaded -= OnLoaded;
            window.Closing -= OnClosing;
            window.Closed -= OnClosed;
        }

        private void AttachButton(Button button)
        {
            if (!_buttons.Add(button))
            {
                return;
            }

            PrepareButtonPressFeedback(button);
            button.PreviewMouseDown += Button_PreviewMouseDown;
            button.PreviewMouseUp += Button_PreviewMouseUp;
            button.MouseLeave += Button_MouseLeave;
            button.IsEnabledChanged += Button_IsEnabledChanged;
            button.LostMouseCapture += Button_LostMouseCapture;
        }

        internal void AttachButtons(DependencyObject root)
        {
            if (!animateButtonPress || _closed)
            {
                return;
            }

            foreach (var button in FindVisualChildren<Button>(root))
            {
                AttachButton(button);
            }
        }

        private static void Button_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Button button)
            {
                AnimateButton(button, pressed: true);
            }
        }

        private static void Button_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Button button)
            {
                AnimateButton(button, pressed: false);
            }
        }

        private static void Button_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is Button button)
            {
                AnimateButton(button, pressed: false);
            }
        }

        private static void Button_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is Button button && e.NewValue is false)
            {
                AnimateButton(button, pressed: false);
            }
        }

        private static void Button_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is Button button)
            {
                AnimateButton(button, pressed: false);
            }
        }

        private static void AnimateButton(Button button, bool pressed)
        {
            AnimateButtonPressFeedback(button, pressed);
        }

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
    }

    private sealed class OpacityMotionState
    {
        internal long Sequence { get; set; }
    }
}
