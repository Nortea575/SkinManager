using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SkinManager.Core;

namespace SkinManager.Desktop;

public partial class MainWindow
{
    private const string SkinOrderDragFormat = "SkinManager.SkinId";
    private Point? _skinDragStartPoint;
    private string? _skinDragSourceId;
    private ListBoxItem? _skinDropTarget;
    private bool _skinDropBefore;
    private string? _skinOrderPersistenceWarning;

    private bool CanReorderSkins =>
        !_isBusy && !_isEditing && string.IsNullOrWhiteSpace(SearchBox?.Text);

    private void UpdateSortStatus()
    {
        if (SortStatusText is null || SkinList is null)
        {
            return;
        }

        SkinList.Tag = CanReorderSkins;
        SortStatusText.Text = !string.IsNullOrWhiteSpace(SearchBox.Text)
            ? AppLocalization.Get("搜索结果已筛选；清空搜索后可排序")
            : _skinOrderPersistenceWarning ?? AppLocalization.Get("拖动皮肤卡片调整顺序（单击选择）");
        SortStatusText.ToolTip = SortStatusText.Text;
    }

    private void SkinListItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            !CanReorderSkins || sender is not ListBoxItem item || item.DataContext is not SkinCard card)
        {
            _skinDragStartPoint = null;
            _skinDragSourceId = null;
            return;
        }

        _skinDragStartPoint = e.GetPosition(SkinList);
        _skinDragSourceId = card.Entry.Id;
    }

    private void SkinListItem_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_skinDragStartPoint is not { } start || _skinDragSourceId is not { } id ||
            e.LeftButton != MouseButtonState.Pressed || !CanReorderSkins || sender is not ListBoxItem item)
        {
            return;
        }

        var current = e.GetPosition(SkinList);
        if (!HasPassedSkinDragThreshold(start, current))
        {
            return;
        }

        _skinDragStartPoint = null;
        try
        {
            var data = new DataObject(SkinOrderDragFormat, id);
            DragDrop.DoDragDrop(item, data, DragDropEffects.Move);
        }
        finally
        {
            _skinDragSourceId = null;
            ClearSkinDropMarker();
        }
    }

    internal static bool HasPassedSkinDragThreshold(Point start, Point current) =>
        Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private void SkinList_DragOver(object sender, DragEventArgs e)
    {
        if (!CanReorderSkins || !e.Data.GetDataPresent(SkinOrderDragFormat))
        {
            e.Effects = DragDropEffects.None;
            ClearSkinDropMarker();
            e.Handled = true;
            return;
        }

        ScrollSkinListAtDragEdge(e);
        var sourceId = e.Data.GetData(SkinOrderDragFormat) as string;
        var item = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        var insertAfter = false;
        if (item?.DataContext is not SkinCard target ||
            string.Equals(target.Entry.Id, sourceId, StringComparison.OrdinalIgnoreCase))
        {
            item = null;
            if (SkinList.Items.Count > 0 &&
                SkinList.ItemContainerGenerator.ContainerFromIndex(SkinList.Items.Count - 1) is ListBoxItem lastItem &&
                e.GetPosition(lastItem).Y >= lastItem.ActualHeight / 2)
            {
                item = lastItem;
                insertAfter = true;
            }
        }

        if (item?.DataContext is SkinCard dropTarget &&
            !string.Equals(dropTarget.Entry.Id, sourceId, StringComparison.OrdinalIgnoreCase))
        {
            if (!insertAfter)
            {
                insertAfter = e.GetPosition(item).Y >= item.ActualHeight / 2;
            }

            _skinDropBefore = !insertAfter;
            ShowSkinDropMarker(item, _skinDropBefore);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        ClearSkinDropMarker();
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void SkinList_Drop(object sender, DragEventArgs e)
    {
        if (!CanReorderSkins || !e.Data.GetDataPresent(SkinOrderDragFormat) ||
            e.Data.GetData(SkinOrderDragFormat) is not string sourceId ||
            _skinDropTarget?.DataContext is not SkinCard target)
        {
            ClearSkinDropMarker();
            return;
        }

        if (string.Equals(sourceId, target.Entry.Id, StringComparison.OrdinalIgnoreCase))
        {
            ClearSkinDropMarker();
            return;
        }

        try
        {
            if (_skinOrderStore.Move(sourceId, target.Entry.Id, insertAfter: !_skinDropBefore))
            {
                _skinOrderPersistenceWarning = null;
                MoveSkinCardInMemory(sourceId, target.Entry.Id, insertAfter: !_skinDropBefore);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _skinOrderPersistenceWarning = AppLocalization.Get("无法保存排序；检查皮肤库目录权限后重试。");
            SortStatusText.ToolTip = exception.Message;
            UpdateSortStatus();
        }

        ClearSkinDropMarker();
        UpdateSortStatus();
        e.Handled = true;
    }

    private void MoveSkinCardInMemory(string sourceId, string targetId, bool insertAfter)
    {
        var moving = _skins.FirstOrDefault(item =>
            string.Equals(item.Entry.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        var target = _skins.FirstOrDefault(item =>
            string.Equals(item.Entry.Id, targetId, StringComparison.OrdinalIgnoreCase));
        if (moving is null || target is null || ReferenceEquals(moving, target))
        {
            RefreshList(sourceId);
            return;
        }

        var beforePositions = FindVisualChildren<ListBoxItem>(SkinList)
            .Where(item => item.IsVisible)
            .ToDictionary(item => item, item => item.TransformToAncestor(SkinList).Transform(new Point()).Y);
        var scrollViewer = FindVisualChildren<ScrollViewer>(SkinList).FirstOrDefault();
        var verticalOffset = scrollViewer?.VerticalOffset ?? 0;
        var selected = SkinList.SelectedItem;
        var sourceIndex = _skins.IndexOf(moving);
        var targetIndex = _skins.IndexOf(target);
        var sourceCardIndex = sourceIndex;
        if (sourceIndex < targetIndex)
        {
            targetIndex--;
        }

        var destinationIndex = Math.Clamp(targetIndex + (insertAfter ? 1 : 0), 0, _skins.Count);
        _skins.Move(sourceCardIndex, destinationIndex);
        SkinList.UpdateLayout();
        scrollViewer?.ScrollToVerticalOffset(verticalOffset);
        SkinList.UpdateLayout();

        if (!ReferenceEquals(SkinList.SelectedItem, selected))
        {
            SetSkinSelectionSilently(selected);
        }

        if (WindowMotion.AnimationsEnabled)
        {
            foreach (var item in FindVisualChildren<ListBoxItem>(SkinList).Where(item => item.IsVisible))
            {
                if (beforePositions.TryGetValue(item, out var previousY))
                {
                    var currentY = item.TransformToAncestor(SkinList).Transform(new Point()).Y;
                    var offset = previousY - currentY;
                    if (Math.Abs(offset) > 0.5)
                    {
                        var transform = new TranslateTransform(0, offset);
                        item.RenderTransform = transform;
                        WindowMotion.AnimateVerticalOffset(transform, 0, 190, () =>
                        {
                            if (ReferenceEquals(item.RenderTransform, transform))
                            {
                                item.RenderTransform = Transform.Identity;
                            }
                        });
                    }
                }
            }
        }

        UpdateSortStatus();
    }

    private void SkinList_DragLeave(object sender, DragEventArgs e)
    {
        if (!SkinList.IsMouseOver)
        {
            ClearSkinDropMarker();
        }
    }

    private void ShowSkinDropMarker(ListBoxItem item, bool before)
    {
        if (_skinDropTarget is { } previous && !ReferenceEquals(previous, item))
        {
            SetSkinDropMarker(previous, visible: false, before: true);
        }

        _skinDropTarget = item;
        SetSkinDropMarker(item, visible: true, before);
    }

    private void ClearSkinDropMarker()
    {
        if (_skinDropTarget is { } target)
        {
            SetSkinDropMarker(target, visible: false, before: true);
        }

        _skinDropTarget = null;
    }

    private static void SetSkinDropMarker(ListBoxItem item, bool visible, bool before)
    {
        item.ApplyTemplate();
        if (item.Template.FindName("DropMarker", item) is Border marker)
        {
            marker.VerticalAlignment = before ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            marker.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void ScrollSkinListAtDragEdge(DragEventArgs e)
    {
        var viewer = FindVisualChildren<ScrollViewer>(SkinList).FirstOrDefault();
        if (viewer is null || viewer.ScrollableHeight <= 0)
        {
            return;
        }

        const double edgeSize = 24;
        const double scrollStep = 20;
        var pointerY = e.GetPosition(SkinList).Y;
        if (pointerY < edgeSize && viewer.VerticalOffset > 0)
        {
            viewer.ScrollToVerticalOffset(Math.Max(0, viewer.VerticalOffset - scrollStep));
        }
        else if (pointerY > SkinList.ActualHeight - edgeSize && viewer.VerticalOffset < viewer.ScrollableHeight)
        {
            viewer.ScrollToVerticalOffset(Math.Min(viewer.ScrollableHeight, viewer.VerticalOffset + scrollStep));
        }
    }

}
