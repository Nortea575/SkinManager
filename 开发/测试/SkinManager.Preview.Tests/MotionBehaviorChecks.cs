using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using SkinManager.Desktop;

namespace SkinManager.Preview.Tests;

internal static class MotionBehaviorChecks
{
    internal static async Task RunAsync()
    {
        var previousOverride = WindowMotion.AnimationEnabledOverrideForTests;
        try
        {
            await CheckModalWindowResultsAsync();
            await CheckOpacityReplacementAsync();
            await CheckPixelScrollAndInputTakeoverAsync();
            await CheckReversibleExpanderAndDisabledMotionAsync();
            await CheckDisabledButtonReleaseAsync();
            Console.WriteLine("通过：动效快速反向、关闭动画、虚拟列表像素滚动、嵌套滚动与键盘/滚动条接管");
        }
        finally
        {
            WindowMotion.AnimationEnabledOverrideForTests = previousOverride;
        }
    }

    private static async Task CheckModalWindowResultsAsync()
    {
        WindowMotion.AnimationEnabledOverrideForTests = true;
        var accepted = await RunModalScenarioAsync(dialog => dialog.DialogResult = true, "接受");
        var rejected = await RunModalScenarioAsync(dialog => dialog.DialogResult = false, "拒绝");
        var titleBarClosed = await RunModalScenarioAsync(dialog => dialog.Close(), "标题栏关闭");
        Check(accepted == true && rejected == false && titleBarClosed == false,
            $"模态关闭动效保留ShowDialog结果：true={accepted?.ToString() ?? "null"}, " +
            $"false={rejected?.ToString() ?? "null"}, x={titleBarClosed?.ToString() ?? "null"}");
    }

    private static async Task<bool?> RunModalScenarioAsync(Action<Window> close, string scenario)
    {
        var owner = new Window
        {
            Content = new Border { Width = 240, Height = 120 },
            Width = 260,
            Height = 140,
            Left = -2_000,
            Top = -2_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize
        };
        owner.Show();
        var dialog = new Window
        {
            Owner = owner,
            Content = new Button { Content = scenario },
            Width = 240,
            Height = 100,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize
        };
        WindowMotion.Attach(dialog);
        var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            close(dialog);
        };
        dialog.Loaded += (_, _) => closeTimer.Start();
        try
        {
            var result = dialog.ShowDialog();
            closeTimer.Stop();
            if (dialog.IsVisible)
            {
                await WaitUntilAsync(() => !dialog.IsVisible, $"{scenario}模态窗口关闭动效完成");
            }
            return result;
        }
        finally
        {
            closeTimer.Stop();
            if (dialog.IsVisible)
            {
                dialog.Close();
            }
            owner.Close();
        }
    }

    private static async Task CheckOpacityReplacementAsync()
    {
        WindowMotion.AnimationEnabledOverrideForTests = true;
        var element = new Border { Opacity = 0 };
        var window = new Window
        {
            Content = element,
            Width = 180,
            Height = 80,
            Left = -2_000,
            Top = -2_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize
        };
        WindowMotion.Attach(window, animateWindow: false, animateClose: false, animateButtonPress: false);
        try
        {
            window.Show();
            await WaitUntilAsync(() => window.IsLoaded, "透明度动画替换目标进入已加载状态");
            WindowMotion.AnimateOpacity(element, 1, 600);
            await Task.Delay(500);
            var fadeOutCompleted = false;
            WindowMotion.AnimateOpacity(element, 0, 250, () => fadeOutCompleted = true);
            await WaitUntilAsync(() => fadeOutCompleted && element.Opacity <= 0.001 && !element.HasAnimatedProperties,
                "过期淡入完成回调不能清除新淡出时钟或覆盖透明度终值");
            Check(element.Opacity <= 0.001 && !element.HasAnimatedProperties,
                "重叠透明度动画只由最新动画提交最终状态");
            Console.WriteLine("通过：过期淡入Completed不能覆盖后启动的淡出");
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task CheckPixelScrollAndInputTakeoverAsync()
    {
        WindowMotion.AnimationEnabledOverrideForTests = true;
        var list = new ListBox
        {
            Height = 160,
            Width = 330,
            ItemsSource = Enumerable.Range(0, 80).Select(index => $"可虚拟化项目 {index:D2}").ToArray()
        };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        list.ItemContainerStyle = new Style(typeof(ListBoxItem));
        list.ItemContainerStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 30d));

        var innerHost = new StackPanel();
        innerHost.Children.Add(list);
        for (var index = 0; index < 24; index++)
        {
            innerHost.Children.Add(new Border { Height = 28, Child = new TextBlock { Text = $"外层项目 {index}" } });
        }

        var outer = new ScrollViewer
        {
            Height = 280,
            Width = 360,
            Content = innerHost,
            CanContentScroll = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var focusInput = new TextBox { Text = "保留焦点", Width = 180, Margin = new Thickness(0, 6, 0, 0) };
        var opacityTarget = new Border { Height = 4, Background = Brushes.SteelBlue };
        var content = new StackPanel();
        content.Children.Add(outer);
        content.Children.Add(focusInput);
        content.Children.Add(opacityTarget);
        var window = new Window
        {
            Content = content,
            Width = 400,
            Height = 360,
            Left = -2_000,
            Top = -2_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize
        };
        WindowMotion.Attach(window, animateWindow: false, animateClose: false, animateButtonPress: false);
        try
        {
            window.Show();
            await WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                var inner = FindVisualChildren<ScrollViewer>(list).FirstOrDefault();
                return inner is { ScrollableHeight: > 500 } && outer.ScrollableHeight > 200;
            }, "长虚拟列表与外层区域均产生真实滚动范围");
            var innerViewer = FindVisualChildren<ScrollViewer>(list).First();

            Check(list.Items.Count == 80 &&
                  VirtualizingPanel.GetIsVirtualizing(list) &&
                  VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Pixel,
                "滚动验证使用80行虚拟列表和Pixel滚动单位");

            var routedWheel = RaiseWheel(list.ItemContainerGenerator.ContainerFromIndex(0) as UIElement
                ?? throw new InvalidOperationException("虚拟列表首行尚未生成。"), -120);
            Check(routedWheel.Handled && SmoothScrollBehavior.IsAnimating(innerViewer) &&
                  !SmoothScrollBehavior.IsAnimating(outer),
                "嵌套滚轮由最近且可滚动的列表接管");
            await WaitUntilAsync(() => !SmoothScrollBehavior.IsAnimating(innerViewer), "列表滚轮动画在有限时长内结束");
            Check(innerViewer.VerticalOffset is > 20 and < 200 && outer.VerticalOffset < 1,
                $"一格滚轮按像素移动少数行且不滚动外层：inner={innerViewer.VerticalOffset:0}, outer={outer.VerticalOffset:0}");

            innerViewer.ScrollToEnd();
            list.UpdateLayout();
            await WaitUntilAsync(() => innerViewer.VerticalOffset >= innerViewer.ScrollableHeight - 1,
                "内层列表可以滚到真实边界");
            var lastItem = list.ItemContainerGenerator.ContainerFromIndex(79) as UIElement
                ?? throw new InvalidOperationException("虚拟列表末行尚未生成。");
            var boundaryWheel = RaiseWheel(lastItem, -120);
            Check(boundaryWheel.Handled && SmoothScrollBehavior.IsAnimating(outer),
                "内层到达边界后滚轮交给可滚动的外层面板");
            await WaitUntilAsync(() => !SmoothScrollBehavior.IsAnimating(outer), "外层边界接续动画在有限时长内结束");
            Check(outer.VerticalOffset > 1 && innerViewer.VerticalOffset >= innerViewer.ScrollableHeight - 1,
                "边界接续只移动外层，内层停留在末端");

            innerViewer.ScrollToTop();
            innerViewer.UpdateLayout();
            Check(SmoothScrollBehavior.RequestScroll(innerViewer, -120) && SmoothScrollBehavior.IsAnimating(innerViewer),
                "键盘接管测试先启动一段列表滚轮动画");
            RaiseKey(innerViewer, Key.End);
            Check(!SmoothScrollBehavior.IsAnimating(innerViewer), "Home/End等显式键盘导航立即接管滚动位置");
            var keyboardOffset = innerViewer.VerticalOffset;
            await Task.Delay(240);
            Check(Math.Abs(innerViewer.VerticalOffset - keyboardOffset) < 1,
                "键盘接管后旧滚轮动画不会将位置拉回");

            Check(SmoothScrollBehavior.RequestScroll(innerViewer, -120) && SmoothScrollBehavior.IsAnimating(innerViewer),
                "拖动接管测试先启动一段列表滚轮动画");
            var scrollThumb = FindVisualChildren<Thumb>(innerViewer).FirstOrDefault()
                ?? throw new InvalidOperationException("虚拟列表未生成滚动条滑块。");
            scrollThumb.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseDownEvent
            });
            Check(!SmoothScrollBehavior.IsAnimating(innerViewer), "按下滚动条滑块立即取消旧动画");

            WindowMotion.AnimationEnabledOverrideForTests = false;
            innerViewer.ScrollToTop();
            innerViewer.UpdateLayout();
            var immediateScrollRequested = SmoothScrollBehavior.RequestScroll(innerViewer, -120);
            Check(immediateScrollRequested && !SmoothScrollBehavior.IsAnimating(innerViewer) &&
                  WindowMotion.AnimationsEnabled == false,
                $"禁用动画时滚轮直接落到目标位置且不遗留计时器 (requested={immediateScrollRequested}, " +
                $"animating={SmoothScrollBehavior.IsAnimating(innerViewer)}, offset={innerViewer.VerticalOffset:0}, " +
                $"range={innerViewer.ScrollableHeight:0}, animations={WindowMotion.AnimationsEnabled})");
            await WaitUntilAsync(() => innerViewer.VerticalOffset > 20,
                "禁用动画的像素偏移在下一布局周期直接应用");
            Keyboard.Focus(focusInput);
            WindowMotion.AnimateOpacity(opacityTarget, 0.2, 160);
            Check(Math.Abs(opacityTarget.Opacity - 0.2) < 0.001 && !opacityTarget.HasAnimatedProperties,
                "禁用动画时透明度变更即时完成且没有残留时钟");
            Check(ReferenceEquals(Keyboard.FocusedElement, focusInput), "禁用动效不改变当前键盘焦点");
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task CheckReversibleExpanderAndDisabledMotionAsync()
    {
        WindowMotion.AnimationEnabledOverrideForTests = true;
        var input = new TextBox { Text = "保持焦点的内容" };
        var expander = new Expander
        {
            Header = "高级选项",
            Content = new StackPanel { Children = { input } },
            IsExpanded = false
        };
        var window = new Window
        {
            Content = expander,
            Width = 280,
            Height = 160,
            Left = -2_000,
            Top = -2_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize
        };
        WindowMotion.Attach(window, animateWindow: false, animateClose: false, animateButtonPress: false);
        try
        {
            window.Show();
            await WaitUntilAsync(() => window.IsLoaded, "制作器折叠面板进入已加载状态");
            expander.IsExpanded = true;
            await WaitUntilAsync(() => expander.Content is UIElement expandedContent &&
                expandedContent.Opacity >= 0.99 && !expandedContent.HasAnimatedProperties,
                "展开动画正常结束并清理时钟");

            Keyboard.Focus(input);
            RaiseHeaderPointer(expander);
            Check(expander.IsExpanded && expander.Content is UIElement { IsHitTestVisible: false },
                "折叠先淡出内容且暂时屏蔽已离场控件");
            RaiseHeaderPointer(expander);
            Check(expander.IsExpanded, "动画进行中再次点击反向展开意图不会被吞掉");
            await WaitUntilAsync(() => expander.IsExpanded && expander.Content is UIElement expandedContent &&
                expandedContent.Opacity >= 0.99 && expandedContent.IsHitTestVisible &&
                !expandedContent.HasAnimatedProperties,
                "快速反向后面板停在完整展开状态且焦点内容可用");

            Keyboard.Focus(input);
            RaiseHeaderKey(expander, Key.Enter);
            await WaitUntilAsync(() => !expander.IsExpanded, "键盘Enter触发折叠反馈后完成收起");
            Check(expander.IsKeyboardFocusWithin && !input.IsKeyboardFocusWithin,
                "折叠隐藏键盘焦点内容后焦点返回面板标题");

            WindowMotion.AnimationEnabledOverrideForTests = false;
            expander.IsExpanded = true;
            Keyboard.Focus(input);
            RaiseHeaderPointer(expander);
            Check(!expander.IsExpanded && expander.Content is UIElement collapsedContent &&
                  Math.Abs(collapsedContent.Opacity - 1) < 0.001 && collapsedContent.IsHitTestVisible &&
                  !collapsedContent.HasAnimatedProperties,
                "禁用动效时折叠即时完成且不留下不可见内容状态");
            Check(expander.IsKeyboardFocusWithin && !input.IsKeyboardFocusWithin,
                "禁用动效的折叠也把焦点交回标题");
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task CheckDisabledButtonReleaseAsync()
    {
        WindowMotion.AnimationEnabledOverrideForTests = true;
        var button = new Button { Content = "按压状态" };
        var window = new Window
        {
            Content = button,
            Width = 180,
            Height = 80,
            Left = -2_000,
            Top = -2_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize
        };
        WindowMotion.Attach(window, animateWindow: false, animateClose: false);
        try
        {
            window.Show();
            await WaitUntilAsync(() => window.IsLoaded && button.RenderTransform is ScaleTransform,
                "按钮按压动效完成初始化");
            WindowMotion.AnimateButtonPressFeedback(button, pressed: true);
            await WaitUntilAsync(() => button.RenderTransform is ScaleTransform { ScaleX: <= 0.981 },
                "按钮按下到达轻微缩小状态");
            button.IsEnabled = false;
            await WaitUntilAsync(() => button.RenderTransform is ScaleTransform scale &&
                scale.ScaleX >= 0.999 && !scale.HasAnimatedProperties,
                "按钮禁用时释放缩放状态并清理动画时钟");
            button.IsEnabled = true;
            Check(button.RenderTransform is ScaleTransform { ScaleX: >= 0.999 },
                "按钮重新启用时保持原始比例");
        }
        finally
        {
            window.Close();
        }
    }

    private static MouseWheelEventArgs RaiseWheel(UIElement source, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent
        };
        source.RaiseEvent(args);
        return args;
    }

    private static void RaiseKey(ScrollViewer source, Key key)
    {
        var presentationSource = PresentationSource.FromVisual(source)
            ?? throw new InvalidOperationException("滚动列表没有连接到 WPF 窗口。");
        source.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, presentationSource, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
    }

    private static void RaiseHeaderPointer(Expander source) => source.RaiseEvent(
        new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
        });

    private static void RaiseHeaderKey(Expander source, Key key)
    {
        var presentationSource = PresentationSource.FromVisual(source)
            ?? throw new InvalidOperationException("折叠面板没有连接到 WPF 窗口。");
        source.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, presentationSource, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
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

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"动效验证超时：{description}");
            }

            await Task.Delay(20);
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"动效验证失败：{description}");
        }
    }
}
