using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SkinManager.Desktop;

internal static class MenuMotionBehavior
{
    private static readonly ConditionalWeakTable<MenuItem, MenuItemController> Controllers = new();

    internal static void Attach(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var item in FindVisualChildren<MenuItem>(root))
        {
            Controllers.GetValue(item, menuItem => new MenuItemController(menuItem)).Attach();
        }
    }

    internal static bool IsAttached(MenuItem item) => Controllers.TryGetValue(item, out _);

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

    private sealed class MenuItemController(MenuItem item)
    {
        private bool _attached;
        private long _sequence;

        internal void Attach()
        {
            if (_attached)
            {
                return;
            }

            _attached = true;
            item.MouseEnter += OnMouseEnter;
            item.MouseLeave += OnMouseLeave;
            item.Unloaded += OnUnloaded;
        }

        private void OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => AnimateHover(true);

        private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => AnimateHover(false);

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (item.IsLoaded)
            {
                return;
            }

            _sequence++;
            item.MouseEnter -= OnMouseEnter;
            item.MouseLeave -= OnMouseLeave;
            item.Unloaded -= OnUnloaded;
            _attached = false;
        }

        private void AnimateHover(bool isHovered)
        {
            item.ApplyTemplate();
            if (item.Template.FindName("ItemSurface", item) is Border surface)
            {
                var sequence = ++_sequence;
                surface.BeginAnimation(UIElement.OpacityProperty, null);
                if (!WindowMotion.AnimationsEnabled)
                {
                    surface.Opacity = 1;
                    return;
                }

                surface.Opacity = isHovered ? 0.88 : surface.Opacity;
                WindowMotion.AnimateOpacity(surface, 1, 115, () =>
                {
                    if (sequence == _sequence)
                    {
                        surface.Opacity = 1;
                    }
                });
            }
        }
    }
}
