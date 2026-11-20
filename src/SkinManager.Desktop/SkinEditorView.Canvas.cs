using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SkinManager.Core;

namespace SkinManager.Desktop;

public partial class SkinEditorView
{
    private ResolvedSkin? _canvasPreview;
    private CanvasSurfaceStyle? _canvasStyle;
    private bool _updatingCanvas;
    private string CanvasScope => (CanvasScopeInput.SelectedItem as ComboBoxItem)?.Tag as string ?? "base";

    internal static bool IsCanvasComponent(string? id) => id is "composer" or "sidebar" or "message.user" or "message.assistant";

    private void SetCanvasSelection(string? componentId)
    {
        SelectedCanvasComponent = componentId;
        _canvasPreview = null;
        _canvasStyle = null;
        CanvasComponentTab.Visibility = IsCanvasComponent(componentId) ? Visibility.Visible : Visibility.Collapsed;
        CanvasColorButton.IsEnabled = false;
        CanvasTransparencySlider.IsEnabled = false;
        CanvasRadiusSlider.IsEnabled = false;
        RefreshCanvasInspector();
    }

    internal void UpdateCanvasInspector(ResolvedSkin preview, CanvasSurfaceStyle? style)
    {
        _canvasPreview = preview;
        _canvasStyle = style;
        if (!_historyGestureActive && !_colorPickerSessionActive)
        {
            RefreshCanvasInspector();
        }
    }

    private void RefreshCanvasInspector()
    {
        if (_draft is null || !IsCanvasComponent(SelectedCanvasComponent) ||
            !SkinDefinitionCatalog.TryGetComponent(SelectedCanvasComponent!, out var definition))
        {
            return;
        }

        CanvasComponentTab.Header = AppLocalization.Get(definition.Label);
        if (_canvasPreview is not { } preview)
        {
            return;
        }

        var appearance = preview.Appearance == SkinAppearance.Light ? "light" : "dark";
        var scene = preview.Scene == SkinScene.Home ? "home" : "conversation";
        var activeScope = CanvasScope == "base" || CanvasScope == appearance || CanvasScope == scene;
        CanvasScopeNotice.Visibility = activeScope && _canvasStyle is not null ? Visibility.Collapsed : Visibility.Visible;
        LocalizedUiText.SetText(CanvasScopeNotice, activeScope
            ? "当前预览未显示此组件。"
            : "此范围在当前预览中不生效。可在右侧切换查看。");

        var background = GetCanvasField("background", CanvasColorStatus, CanvasColorReset, appearance, scene);
        var opacity = GetCanvasField("fillOpacity", CanvasOpacityStatus, CanvasOpacityReset, appearance, scene);
        var radius = GetCanvasField("radius", CanvasRadiusStatus, CanvasRadiusReset, appearance, scene);
        CanvasColorButton.IsEnabled = true;
        CanvasTransparencySlider.IsEnabled = true;
        CanvasRadiusSlider.IsEnabled = true;
        var backgroundColor = ReadCanvasColor(background) ?? _canvasStyle?.Background;
        CanvasColorSwatch.Background = CssColorParser.TryParse(backgroundColor, out var color)
            ? new SolidColorBrush(ColorPickerMath.ToMediaColor(color))
            : Brushes.Transparent;

        _updatingCanvas = true;
        try
        {
            var fill = ReadCanvasNumber(opacity) ?? _canvasStyle?.FillOpacity ?? 1;
            CanvasTransparencySlider.Value = Math.Round((1 - fill) * 100);
            CanvasTransparencyValue.Text = $"{CanvasTransparencySlider.Value:0}%";
            var corners = ReadCanvasNumber(radius) ?? _canvasStyle?.Radius ?? 0;
            CanvasRadiusSlider.Maximum = Math.Max(32, Math.Ceiling(corners));
            CanvasRadiusSlider.Value = Math.Round(corners);
            CanvasRadiusValue.Text = $"{CanvasRadiusSlider.Value:0} px";
        }
        finally
        {
            _updatingCanvas = false;
        }
    }

    private JsonNode? GetCanvasField(string property, TextBlock status, Button reset, string appearance, string scene)
    {
        var scope = CanvasScope;
        var local = GetNode(Draft.Manifest, CanvasPath(scope, property));
        var appearanceValue = GetNode(Draft.Manifest, CanvasPath(appearance, property));
        var sceneValue = GetNode(Draft.Manifest, CanvasPath(scene, property));
        var isAppearanceScope = scope is "light" or "dark";
        var isScopeActive = scope == "base" || scope == appearance || scope == scene;
        var coveringScope = !isScopeActive ? null : scope != scene && sceneValue is not null
            ? scene
            : scope == "base" && appearanceValue is not null ? appearance : null;
        var inherited = isAppearanceScope || scope == "base" ? null : appearanceValue;
        inherited ??= GetNode(Draft.Manifest, CanvasPath("base", property));
        var value = local ?? inherited;
        var source = IsCanvasTokenReference(ReadCanvasString(value)) || value is null ? "跟随整体"
            : local is null ? "沿用组件默认" : "单独设置";
        if (coveringScope is null)
        {
            LocalizedUiText.SetText(status, source);
        }
        else
        {
            LocalizedUiText.SetText(status, "{0} · 当前预览使用「{1}」的覆盖", AppLocalization.Get(source), AppLocalization.Get(CanvasScopeName(coveringScope)));
        }

        reset.IsEnabled = local is not null;
        return value;
    }

    private string? ReadCanvasColor(JsonNode? node) => ReadCanvasString(ResolveCanvasToken(node));

    private double? ReadCanvasNumber(JsonNode? node)
    {
        var value = ReadNumber(ResolveCanvasToken(node), double.NaN);
        return double.IsFinite(value) ? value : null;
    }

    private JsonNode? ResolveCanvasToken(JsonNode? node)
    {
        var tokenValue = ReadCanvasString(node);
        if (!IsCanvasTokenReference(tokenValue))
        {
            return node;
        }

        var token = tokenValue![1..^1];
        if (CanvasScope is "base" or "light" or "dark")
        {
            var scoped = GetNode(Draft.Manifest, ScopedPathFor(CanvasScope, ["tokens", token])) ?? GetNode(Draft.Manifest, ["tokens", token]);
            if (scoped is not null && !IsCanvasTokenReference(ReadCanvasString(scoped)))
            {
                return scoped;
            }
        }

        if (_canvasPreview?.Tokens.TryGetValue(token, out var value) == true)
        {
            return value.Number is { } number ? JsonValue.Create(number) : JsonValue.Create(value.Text);
        }

        return null;
    }

    private static bool IsCanvasTokenReference(string? value) => value is { Length: > 2 } && value[0] == '{' && value[^1] == '}';

    private static string? ReadCanvasString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private IReadOnlyList<string> CanvasPath(string scope, string property) =>
        ScopedPathFor(scope, ["components", SelectedCanvasComponent!, property]);

    private static string CanvasScopeName(string scope) => scope switch
    {
        "light" => "仅浅色",
        "dark" => "仅深色",
        "home" => "仅首页",
        "conversation" => "仅对话",
        _ => "整体默认"
    };

    private void CanvasScopeInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _draft is null)
        {
            return;
        }

        CancelColorPickerSession();
        EndBackgroundFocusGesture();
        RefreshCanvasInspector();
    }

    private void CanvasResetButton_Click(object sender, RoutedEventArgs e)
    {
        var property = (string)((Button)sender).Tag;
        CancelColorPickerSession();
        EndBackgroundFocusGesture();
        SetManifestValue(CanvasPath(CanvasScope, property), null, resourcesChanged: false);
        RefreshCanvasInspector();
        PreviewFlushRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CanvasColorButton_Click(object sender, RoutedEventArgs e)
    {
        var path = CanvasPath(CanvasScope, "background");
        var value = ReadCanvasString(GetNode(Draft.Manifest, path)) ?? string.Empty;
        var field = new SkinEditorField(path, "表面颜色", value, string.Empty, isColor: true, isNumeric: false,
            SkinFieldDisplayTransform.None, OnFieldValueChanged);
        var brush = CanvasColorSwatch.Background as SolidColorBrush;
        var color = brush is not null && brush.Color.A > 0
            ? new CssRgba(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A)
            : (CssRgba?)null;
        OpenColorPicker(CanvasColorButton, field, color);
    }

    private void CanvasSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _updatingCanvas || !IsCanvasComponent(SelectedCanvasComponent))
        {
            return;
        }

        var value = Math.Round(e.NewValue);
        var property = (string)((Slider)sender).Tag;
        if (property == "fillOpacity")
        {
            CanvasTransparencyValue.Text = $"{value:0}%";
            value = (100 - value) / 100;
        }
        else
        {
            CanvasRadiusValue.Text = $"{value:0} px";
        }

        SetManifestValue(CanvasPath(CanvasScope, property), JsonValue.Create(value), resourcesChanged: false);
    }

    private void CanvasAdvancedButton_Click(object sender, RoutedEventArgs e)
    {
        CancelColorPickerSession();
        _loading = true;
        try
        {
            SelectCanvasScope(ScopeInput, CanvasScope);
            ComponentInput.SelectedValue = SelectedCanvasComponent;
            ComponentStateInput.SelectedIndex = 0;
            LoadTokenFields();
            LoadBackgroundFields();
            LoadComponentFields();
            EditorModeTabs.SelectedIndex = 1;
            EditorTabs.SelectedItem = ComponentsTab;
        }
        finally
        {
            _loading = false;
        }
    }

    private static void SelectCanvasScope(ComboBox input, string scope) =>
        input.SelectedItem = input.Items.OfType<ComboBoxItem>().First(item => Equals(item.Tag, scope));
}
