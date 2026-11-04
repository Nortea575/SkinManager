using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SkinManager.Desktop;

internal static class ExpanderMotionBehavior
{
    private static readonly ConditionalWeakTable<Expander, ExpanderController> Controllers = new();

    internal static void Attach(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var expander in FindVisualChildren<Expander>(root))
        {
            Controllers.GetValue(expander, control => new ExpanderController(control)).Attach();
        }
    }

    internal static bool IsAttached(Expander expander) => Controllers.TryGetValue(expander, out _);

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

    private sealed class ExpanderController(Expander expander)
    {
        private bool _attached;
        private bool _collapsing;
        private long _transitionSequence;

        internal void Attach()
        {
            if (_attached)
            {
                return;
            }

            _attached = true;
            expander.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            expander.PreviewKeyDown += OnPreviewKeyDown;
            expander.Expanded += OnExpanded;
            expander.Collapsed += OnCollapsed;
            expander.Unloaded += OnUnloaded;
        }

        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (expander.IsExpanded && !IsInsideContent(e.OriginalSource as DependencyObject))
            {
                e.Handled = true;
                CollapseWithFeedback();
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (expander.IsExpanded && e.Key is Key.Space or Key.Enter &&
                !IsInsideContent(e.OriginalSource as DependencyObject))
            {
                e.Handled = true;
                CollapseWithFeedback();
            }
        }

        private void OnExpanded(object? sender, RoutedEventArgs e)
        {
            _collapsing = false;
            var content = expander.Content as UIElement;
            if (content is null)
            {
                return;
            }

            var sequence = ++_transitionSequence;
            content.IsHitTestVisible = true;
            if (!WindowMotion.AnimationsEnabled)
            {
                content.BeginAnimation(UIElement.OpacityProperty, null);
                content.Opacity = 1;
                return;
            }

            if (!content.HasAnimatedProperties)
            {
                content.Opacity = 0;
            }

            WindowMotion.AnimateOpacity(content, 1, 155, () =>
            {
                if (sequence == _transitionSequence && expander.IsExpanded)
                {
                    content.Opacity = 1;
                }
            });
        }

        private void OnCollapsed(object? sender, RoutedEventArgs e)
        {
            _collapsing = false;
            _transitionSequence++;
            if (expander.Content is UIElement content)
            {
                content.BeginAnimation(UIElement.OpacityProperty, null);
                content.Opacity = 1;
                content.IsHitTestVisible = true;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (expander.IsLoaded)
            {
                return;
            }

            _transitionSequence++;
            if (expander.Content is UIElement content)
            {
                content.BeginAnimation(UIElement.OpacityProperty, null);
                content.Opacity = 1;
                content.IsHitTestVisible = true;
            }
        }

        private void CollapseWithFeedback()
        {
            if (!expander.IsExpanded)
            {
                return;
            }

            var content = expander.Content as UIElement;
            if (_collapsing && content is not null)
            {
                _collapsing = false;
                var reverseSequence = ++_transitionSequence;
                content.IsHitTestVisible = true;
                WindowMotion.AnimateOpacity(content, 1, 145, () =>
                {
                    if (reverseSequence == _transitionSequence && expander.IsExpanded)
                    {
                        content.Opacity = 1;
                    }
                });
                return;
            }

            if (content is null)
            {
                expander.IsExpanded = false;
                return;
            }

            if (!WindowMotion.AnimationsEnabled)
            {
                var returnFocusToHeader = content.IsKeyboardFocusWithin;
                expander.IsExpanded = false;
                if (returnFocusToHeader)
                {
                    expander.Focus();
                }
                return;
            }

            _collapsing = true;
            var sequence = ++_transitionSequence;
            content.IsHitTestVisible = false;
            WindowMotion.AnimateOpacity(content, 0, 125, () =>
            {
                if (sequence != _transitionSequence || !expander.IsExpanded)
                {
                    return;
                }

                var returnFocusToHeader = content.IsKeyboardFocusWithin;
                expander.IsExpanded = false;
                content.Opacity = 1;
                content.IsHitTestVisible = true;
                _collapsing = false;
                if (returnFocusToHeader)
                {
                    expander.Focus();
                }
            });
        }

        private bool IsInsideContent(DependencyObject? candidate)
        {
            if (candidate is null || expander.Content is not DependencyObject content)
            {
                return false;
            }

            var current = candidate;
            while (current is not null)
            {
                if (ReferenceEquals(current, content))
                {
                    return true;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }
    }
}
