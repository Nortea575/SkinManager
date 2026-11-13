using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;
using SkinManager.Core;

namespace SkinManager.Desktop;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string source)
    {
        Source = source;
    }

    [ConstructorArgument("source")]
    public string Source { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var source = new LocalizedText(Source);
        var binding = new Binding(nameof(LocalizedText.Value))
        {
            Source = source,
            Mode = BindingMode.OneWay,
            FallbackValue = Source,
            TargetNullValue = Source
        };
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class LocalizedText : INotifyPropertyChanged
    {
        private static readonly object Gate = new();
        private static readonly List<WeakReference<LocalizedText>> Instances = [];

        static LocalizedText()
        {
            AppLocalization.LanguageChanged += OnLanguageChanged;
        }

        public LocalizedText(string source)
        {
            Source = source;
            lock (Gate)
            {
                Instances.Add(new WeakReference<LocalizedText>(this));
            }
        }

        public string Source { get; }

        public string Value => AppLocalization.Get(Source);

        public event PropertyChangedEventHandler? PropertyChanged;

        private static void OnLanguageChanged(object? sender, EventArgs e)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(RefreshAll, DispatcherPriority.DataBind);
                return;
            }

            RefreshAll();
        }

        private static void RefreshAll()
        {
            LocalizedText[] liveInstances;
            lock (Gate)
            {
                liveInstances = Instances
                    .Select(reference => reference.TryGetTarget(out var instance) ? instance : null)
                    .Where(instance => instance is not null)
                    .Cast<LocalizedText>()
                    .ToArray();
                Instances.RemoveAll(reference => !reference.TryGetTarget(out _));
            }

            foreach (var instance in liveInstances)
            {
                instance.PropertyChanged?.Invoke(instance, new PropertyChangedEventArgs(nameof(Value)));
            }
        }
    }
}
