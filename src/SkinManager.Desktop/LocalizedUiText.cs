using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using SkinManager.Core;

namespace SkinManager.Desktop;

internal static class LocalizedUiText
{
    private static readonly ConditionalWeakTable<DependencyObject, TargetEntries> EntriesByTarget = new();
    private static readonly List<WeakReference<Entry>> Entries = [];

    static LocalizedUiText() => AppLocalization.LanguageChanged += OnLanguageChanged;

    public static void SetText(TextBlock target, string source, params object?[] arguments) =>
        Set(target, TextBlock.TextProperty, source, arguments);

    public static void SetContent(ContentControl target, string source, params object?[] arguments) =>
        Set(target, ContentControl.ContentProperty, source, arguments);

    public static void SetToolTip(FrameworkElement target, string source, params object?[] arguments) =>
        Set(target, ToolTipService.ToolTipProperty, source, arguments);

    public static void SetAutomationName(DependencyObject target, string source, params object?[] arguments) =>
        Set(target, AutomationProperties.NameProperty, source, arguments);

    public static void SetRawText(TextBlock target, string value) => SetRaw(target, TextBlock.TextProperty, value);

    public static string Get(string source) => AppLocalization.Get(source);

    public static string Format(string source, params object?[] arguments) => AppLocalization.Format(source, arguments);

    private static void Set(DependencyObject target, DependencyProperty property, string source, object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(source);

        var targetEntries = EntriesByTarget.GetOrCreateValue(target);
        if (targetEntries.Entries.TryGetValue(property, out var existing))
        {
            existing.Update(source, arguments);
            existing.Apply();
            return;
        }

        var entry = new Entry(target, property, source, arguments);
        targetEntries.Entries.Add(property, entry);
        lock (Entries)
        {
            Entries.Add(new WeakReference<Entry>(entry));
        }

        entry.Apply();
    }

    private static void SetRaw(DependencyObject target, DependencyProperty property, object? value)
    {
        if (EntriesByTarget.TryGetValue(target, out var targetEntries) &&
            targetEntries.Entries.TryGetValue(property, out var existing))
        {
            targetEntries.Entries.Remove(property);
            if (targetEntries.Entries.Count == 0)
            {
                EntriesByTarget.Remove(target);
            }

            lock (Entries)
            {
                Entries.RemoveAll(reference =>
                    !reference.TryGetTarget(out var entry) || ReferenceEquals(entry, existing));
            }
        }

        target.SetValue(property, value);
    }

    private static void OnLanguageChanged(object? sender, EventArgs e)
    {
        Entry[] entries;
        lock (Entries)
        {
            entries = Entries
                .Select(reference => reference.TryGetTarget(out var entry) ? entry : null)
                .Where(entry => entry is not null)
                .Cast<Entry>()
                .ToArray();
            Entries.RemoveAll(reference => !reference.TryGetTarget(out _));
        }

        foreach (var entry in entries)
        {
            entry.ApplyOnDispatcher();
        }
    }

    private sealed class TargetEntries
    {
        public Dictionary<DependencyProperty, Entry> Entries { get; } = [];
    }

    private sealed class Entry(DependencyObject target, DependencyProperty property, string source, object?[] arguments)
    {
        private readonly WeakReference<DependencyObject> _target = new(target);
        private readonly DependencyProperty _property = property;
        private string _source = source;
        private object?[] _arguments = arguments;

        public void Update(string source, object?[] arguments)
        {
            _source = source;
            _arguments = arguments;
        }

        public void ApplyOnDispatcher()
        {
            if (!_target.TryGetTarget(out var target))
            {
                return;
            }

            if (target.Dispatcher.CheckAccess())
            {
                Apply();
            }
            else if (!target.Dispatcher.HasShutdownStarted)
            {
                _ = target.Dispatcher.BeginInvoke(new Action(Apply), DispatcherPriority.DataBind);
            }
        }

        public void Apply()
        {
            if (_target.TryGetTarget(out var target))
            {
                target.SetValue(_property, AppLocalization.Format(_source, _arguments));
            }
        }
    }
}
