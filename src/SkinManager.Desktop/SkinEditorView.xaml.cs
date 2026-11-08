using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SkinManager.Core;

namespace SkinManager.Desktop;

public partial class SkinEditorView : UserControl
{
    private readonly ObservableCollection<SkinEditorField> _tokenFields = [];
    private readonly ObservableCollection<SkinEditorField> _backgroundFields = [];
    private readonly ObservableCollection<SkinEditorField> _componentFields = [];
    private readonly ObservableCollection<EditorOption> _componentOptions = [];
    private readonly ObservableCollection<EditorOption> _componentStateOptions = [];
    private readonly ObservableCollection<EditorOption> _componentPropertyOptions = [];
    private readonly Stack<JsonObject> _undo = new();
    private readonly Stack<JsonObject> _redo = new();
    private SkinDraft? _draft;
    private SkinEditorField? _activeColorField;
    private SkinEditorField? _creationAccentField;
    private SkinEditorField? _creationBackgroundField;
    private string _baseline = "{}";
    private bool _loading;
    private bool _isNew;
    private bool _historyGestureActive;
    private bool _gestureSnapshotPushed;

    public SkinEditorView()
    {
        InitializeComponent();
        Loaded += SkinEditorView_Loaded;
        TokenFields.ItemsSource = _tokenFields;
        BackgroundFields.ItemsSource = _backgroundFields;
        ComponentFields.ItemsSource = _componentFields;
        ComponentInput.ItemsSource = _componentOptions;
        ComponentStateInput.ItemsSource = _componentStateOptions;
        AddComponentPropertyInput.ItemsSource = _componentPropertyOptions;
        foreach (var component in SkinDefinitionCatalog.Components.Where(item => item.EditorVisible))
        {
            _componentOptions.Add(new EditorOption(component.Id, component.Label));
        }

        _componentStateOptions.Add(new EditorOption("default", "默认"));
        foreach (var state in SkinDefinitionCatalog.States.OrderBy(item => item.EditorOrder))
        {
            _componentStateOptions.Add(new EditorOption(state.Id, state.Label));
        }

        foreach (var property in SkinDefinitionCatalog.DefaultComponentEditorFields)
        {
            _componentPropertyOptions.Add(new EditorOption(property.Key, property.Label));
        }
    }

    private void SkinEditorView_Loaded(object sender, RoutedEventArgs e) => WindowMotion.AttachBehaviors(this);

    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.OriginalSource, EditorTabs) ||
            EditorTabs.SelectedItem is not TabItem)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => WindowMotion.AttachBehaviors(EditorTabs));
    }

    private void EditorModeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !ReferenceEquals(e.OriginalSource, EditorModeTabs))
        {
            return;
        }

        if (EditorModeTabs.SelectedIndex != 0 && BackgroundFocusModeCheckBox.IsChecked == true)
        {
            BackgroundFocusModeCheckBox.IsChecked = false;
        }

        EditorModeChanged?.Invoke(this, EventArgs.Empty);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => WindowMotion.AttachBehaviors(EditorModeTabs));
    }

    public event EventHandler<SkinEditorChangedEventArgs>? DraftChanged;
    public event EventHandler? ScopeChanged;
    public event EventHandler? SaveRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler? SaveAsRequested;
    public event EventHandler? BackgroundFocusModeChanged;
    public event EventHandler? EditorModeChanged;

    public SkinDraft Draft => _draft ?? throw new InvalidOperationException("皮肤草稿尚未初始化。");
    public string SelectedScope => (ScopeInput.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "base";
    public bool IsDirty => _draft is not null && !string.Equals(_baseline, Draft.Manifest.ToJsonString(), StringComparison.Ordinal);
    public bool IsNew => _isNew;
    public bool IsCreationMode => EditorModeTabs.SelectedIndex == 0;
    public bool IsBackgroundFocusEditing => EditorModeTabs.SelectedIndex == 0 && BackgroundFocusModeCheckBox.IsChecked == true;

    public void SetDraft(SkinDraft draft, bool isNew)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _draft = draft;
        _isNew = isNew;
        _historyGestureActive = false;
        _gestureSnapshotPushed = false;
        _undo.Clear();
        _redo.Clear();
        _baseline = draft.Manifest.ToJsonString();
        _loading = true;
        try
        {
            EditorTitle.Text = isNew ? "新建皮肤" : "编辑皮肤";
            SaveButton.Content = isNew ? "创建皮肤" : "保存修改";
            SetSaveActionsLayout(isNew);
            EditorModeTabs.SelectedIndex = isNew ? 0 : 1;
            BackgroundFocusModeCheckBox.IsChecked = false;
            ScopeInput.SelectedIndex = 0;
            LoadMetadata();
            LoadTokenFields();
            LoadBackgroundFields();
            LoadCreationControls();
            ComponentInput.SelectedValue = "window";
            ComponentStateInput.SelectedIndex = 0;
            AddComponentPropertyInput.SelectedIndex = 0;
            LoadComponentFields();
            ClearValidationError();
        }
        finally
        {
            _loading = false;
        }

        UpdateHistoryControls();
        UpdateDirtyState();
        PruneDraftResources();
    }

    public void MarkSaved()
    {
        _baseline = Draft.Manifest.ToJsonString();
        _undo.Clear();
        _redo.Clear();
        _historyGestureActive = false;
        _gestureSnapshotPushed = false;
        _isNew = false;
        EditorTitle.Text = "编辑皮肤";
        SaveButton.Content = "保存修改";
        SetSaveActionsLayout(isNew: false);
        PruneDraftResources();
        UpdateHistoryControls();
        UpdateDirtyState();
    }

    public void BeginBackgroundFocusGesture()
    {
        if (_historyGestureActive)
        {
            return;
        }

        _historyGestureActive = true;
        _gestureSnapshotPushed = false;
    }

    public void SetBackgroundFocus(double focusX, double focusY)
    {
        if (_draft is null)
        {
            return;
        }

        SetManifestValue(["background", "focusX"], JsonValue.Create(Math.Clamp(focusX, 0, 1)), resourcesChanged: false);
        SetManifestValue(["background", "focusY"], JsonValue.Create(Math.Clamp(focusY, 0, 1)), resourcesChanged: false);
    }

    public void EndBackgroundFocusGesture()
    {
        _historyGestureActive = false;
        _gestureSnapshotPushed = false;
    }

    public void SetBackgroundFocusEditing(bool enabled)
    {
        if (!enabled && BackgroundFocusModeCheckBox.IsChecked == true)
        {
            BackgroundFocusModeCheckBox.IsChecked = false;
        }
    }

    public void SetSkinId(string value)
    {
        if (_draft is null)
        {
            return;
        }

        IdInput.Text = value;
        SetMetadata("id", value);
    }

    public void SelectComponent(string componentId)
    {
        if (SkinDefinitionCatalog.Components.Any(item => item.Id == componentId && item.EditorVisible))
        {
            ComponentInput.SelectedValue = componentId;
            EditorModeTabs.SelectedIndex = 1;
            var componentsTab = EditorTabs.Items.OfType<TabItem>()
                .FirstOrDefault(item => item.Name == "ComponentsTab");
            if (componentsTab is not null)
            {
                EditorTabs.SelectedItem = componentsTab;
            }
        }
    }

    public void SetValidationError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            ClearValidationError();
            return;
        }

        ValidationErrorText.Text = message;
        ValidationErrorText.Visibility = Visibility.Visible;
    }

    private void LoadMetadata()
    {
        IdInput.Text = ReadString(Draft.Manifest, "id") ?? string.Empty;
        NameInput.Text = ReadString(Draft.Manifest, "name") ?? string.Empty;
        VersionInput.Text = ReadString(Draft.Manifest, "version") ?? "1.0.0";
        AuthorInput.Text = ReadString(Draft.Manifest, "author") ?? string.Empty;
        var appearance = ReadString(Draft.Manifest, "appearance") ?? "system";
        AppearanceInput.SelectedItem = AppearanceInput.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), appearance, StringComparison.OrdinalIgnoreCase))
            ?? AppearanceInput.Items[0];
        UpdateMetadataSummary();
    }

    private void LoadCreationControls()
    {
        if (_draft is null)
        {
            return;
        }

        var tokens = Draft.Manifest["tokens"] as JsonObject;
        var background = Draft.Manifest["background"] as JsonObject;
        var appearance = ReadString(Draft.Manifest, "appearance") == "dark" ? "dark" : "light";
        CreationAppearanceInput.SelectedItem = CreationAppearanceInput.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), appearance, StringComparison.OrdinalIgnoreCase))
            ?? CreationAppearanceInput.Items[0];

        var accent = ReadString(tokens, "color.accent") ?? (appearance == "dark" ? "#9AB3DF" : "#2563EB");
        var windowColor = ReadString(tokens, "color.surface.window") ?? (appearance == "dark" ? "#202735" : "#F7F9FC");
        var backgroundColor = ResolveCreationBackgroundColor(ReadString(background, "color"), tokens, windowColor);
        _creationAccentField = new SkinEditorField(["tokens", "color.accent"], "主色", accent, string.Empty,
            isColor: true, isNumeric: false, SkinFieldDisplayTransform.None, OnFieldValueChanged);
        _creationBackgroundField = new SkinEditorField(["background", "color"], "背景底色", backgroundColor, string.Empty,
            isColor: true, isNumeric: false, SkinFieldDisplayTransform.None, OnFieldValueChanged);
        CreationAccentPickerButton.Tag = _creationAccentField;
        CreationBackgroundPickerButton.Tag = _creationBackgroundField;
        CreationAccentHexInput.Text = accent;
        CreationBackgroundHexInput.Text = backgroundColor;
        CreationAccentSwatch.Background = _creationAccentField.SwatchBrush;
        CreationBackgroundSwatch.Background = _creationBackgroundField.SwatchBrush;

        var fillOpacity = ReadNumber(tokens?["material.fillOpacity"], appearance == "dark" ? 0.40 : 0.28);
        PanelTransparencySlider.Value = Math.Clamp((1 - fillOpacity) * 100, 0, 100);
        PanelTransparencyValue.Text = $"{Math.Round(PanelTransparencySlider.Value):0}%";
        var panelRadius = ReadNumber(tokens?["radius.control"], ReadNumber(tokens?["radius.panel"], 16));
        CornerRadiusSlider.Value = Math.Clamp(panelRadius, 0, 32);
        CornerRadiusValue.Text = $"{Math.Round(CornerRadiusSlider.Value):0} px";
        var overlayOpacity = ReadNumber(background?["overlayOpacity"],
            SkinCreation.GetDefaultOverlayOpacity(appearance == "dark" ? SkinAppearance.Dark : SkinAppearance.Light));
        BackgroundDarknessSlider.Value = Math.Clamp(overlayOpacity * 100, 0, 100);
        BackgroundDarknessValue.Text = $"{Math.Round(BackgroundDarknessSlider.Value):0}%";

        var imagePath = ReadString(background, "image");
        var hasImage = !string.IsNullOrWhiteSpace(imagePath);
        CreationBackgroundPathText.Text = hasImage ? "已选择背景图片" : "可拖入 PNG 或 JPG，也可使用纯色背景";
        CreationBackgroundPathText.ToolTip = hasImage
            ? "背景图片已添加，可调整背景明暗与位置。"
            : "可拖入 PNG 或 JPG，也可使用纯色背景";
        CreationBackgroundHexInput.IsEnabled = !hasImage;
        CreationBackgroundPickerButton.IsEnabled = !hasImage;
        CreationBackgroundColorPanel.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
        CreationUseColorButton.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        BackgroundTreatmentPanel.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        BackgroundFocusPanel.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        if (!hasImage)
        {
            BackgroundFocusModeCheckBox.IsChecked = false;
        }
    }

    private static string ResolveCreationBackgroundColor(string? value, JsonObject? tokens, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.StartsWith('{') && value.EndsWith('}'))
        {
            return ReadString(tokens, value[1..^1]) ?? fallback;
        }

        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static double ReadNumber(JsonNode? node, double fallback)
    {
        var value = ReadScalar(node, string.Empty, out _);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : fallback;
    }

    private void LoadTokenFields()
    {
        _tokenFields.Clear();
        var tokens = GetScopedObject("tokens");
        var definitions = SkinDefinitionCatalog.DefaultTokenEditorFields.ToDictionary(item => item.Key, StringComparer.Ordinal);
        foreach (var source in new[] { Draft.Manifest["tokens"] as JsonObject, tokens })
        {
            if (source is null)
            {
                continue;
            }

            foreach (var pair in source)
            {
                if (!definitions.ContainsKey(pair.Key) &&
                    SkinDefinitionCatalog.TryGetToken(pair.Key, out var definition) &&
                    definition.EditorKind != SkinEditorFieldKind.Unsupported &&
                    TryReadScalar(pair.Value, out _, out _))
                {
                    definitions[pair.Key] = definition;
                }
            }
        }

        foreach (var definition in definitions.Values)
        {
            var path = ScopedPath("tokens", definition.Key);
            var node = GetNode(Draft.Manifest, path);
            var value = ReadScalar(node, string.Empty, out var numeric);
            var percentMode = GetDisplayTransform(path);
            var isNumeric = numeric || definition.EditorKind == SkinEditorFieldKind.Number;
            var placeholder = node is not null
                ? string.Empty
                : SelectedScope == "base"
                    ? (_isNew ? definition.DefaultValue ?? string.Empty : "继承原版")
                    : "继承基础范围";
            _tokenFields.Add(new SkinEditorField(
                path, node is not null ? definition.Label : $"{definition.Label} · {(SelectedScope == "base" ? "继承原版" : "继承基础范围")}",
                ToDisplayValue(value, isNumeric, percentMode), placeholder,
                definition.EditorKind == SkinEditorFieldKind.Color, isNumeric, percentMode, OnFieldValueChanged));
        }
    }

    private void LoadBackgroundFields()
    {
        _backgroundFields.Clear();
        var background = GetScopedObject("background");
        var imagePath = ReadString(background, "image");
        BackgroundImagePathText.Text = imagePath ?? (SelectedScope == "base" ? "未选择背景图" : "沿用基础背景图");
        BackgroundImagePathText.ToolTip = imagePath ?? (SelectedScope == "base" ? "未选择背景图" : "当前范围未覆盖图片，将沿用基础背景图");
        RemoveBackgroundButton.IsEnabled = !string.IsNullOrWhiteSpace(imagePath);
        var fit = ReadString(background, "fit") ?? (SelectedScope == "base" ? "cover" : "inherit");
        BackgroundFitInput.SelectedItem = BackgroundFitInput.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), fit, StringComparison.OrdinalIgnoreCase))
            ?? BackgroundFitInput.Items.Cast<ComboBoxItem>().First();

        foreach (var definition in SkinDefinitionCatalog.DefaultBackgroundEditorFields)
        {
            var path = ScopedPath("background", definition.Key);
            var node = GetNode(Draft.Manifest, path);
            var value = ReadScalar(node, string.Empty, out var numeric);
            var percentMode = GetDisplayTransform(path);
            var isNumeric = numeric || definition.EditorKind == SkinEditorFieldKind.Number;
            var placeholder = node is not null
                ? string.Empty
                : SelectedScope == "base"
                    ? (_isNew ? definition.DefaultValue ?? string.Empty : "继承原版")
                    : "继承基础范围";
            _backgroundFields.Add(new SkinEditorField(
                path, node is not null ? definition.Label : $"{definition.Label} · {(SelectedScope == "base" ? "继承原版" : "继承基础范围")}",
                ToDisplayValue(value, isNumeric, percentMode), placeholder,
                definition.EditorKind == SkinEditorFieldKind.Color, isNumeric, percentMode, OnFieldValueChanged));
        }
    }

    private void LoadComponentFields()
    {
        _componentFields.Clear();
        if (_draft is null)
        {
            return;
        }

        if (ComponentInput.SelectedValue is not string componentId)
        {
            ComponentScopeHint.Visibility = Visibility.Collapsed;
            return;
        }

        var component = GetNode(Draft.Manifest, ScopedPath("components", componentId)) as JsonObject;
        var state = SelectedState();
        var propertyObject = state == "default"
            ? component
            : (component?["states"] as JsonObject)?[state] as JsonObject;
        if (propertyObject is null)
        {
            ComponentScopeHint.Text = SelectedScope == "base"
                ? "基础范围尚无此组件覆盖；添加属性即可创建样式。"
                : "当前范围尚无此组件覆盖，预览沿用基础值；添加属性即可创建覆盖。";
            ComponentScopeHint.Visibility = Visibility.Visible;
            return;
        }

        foreach (var definition in SkinDefinitionCatalog.ExistingComponentEditorFields)
        {
            var node = propertyObject[definition.Key];
            if (!TryReadScalar(node, out var value, out var numeric))
            {
                continue;
            }

            var path = ScopedPath(ComponentPath(componentId, state, definition.Key).ToArray());
            var percentMode = GetDisplayTransform(path);
            var isNumeric = numeric || definition.EditorKind == SkinEditorFieldKind.Number;
            _componentFields.Add(new SkinEditorField(
                path, definition.Label, ToDisplayValue(value, isNumeric, percentMode), string.Empty,
                definition.EditorKind == SkinEditorFieldKind.Color, isNumeric, percentMode, OnFieldValueChanged));
        }

        ComponentScopeHint.Text = SelectedScope == "base"
            ? "此状态尚无自定义属性；基础样式由原版界面提供。"
            : "此范围尚无自定义属性，预览沿用已有样式；添加属性即可覆盖。";
        ComponentScopeHint.Visibility = _componentFields.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Metadata_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _draft is null)
        {
            return;
        }

        var input = (TextBox)sender;
        var key = input.Name switch
        {
            nameof(IdInput) => "id",
            nameof(NameInput) => "name",
            nameof(VersionInput) => "version",
            nameof(AuthorInput) => "author",
            _ => string.Empty
        };
        if (key.Length > 0)
        {
            var value = input.Text.Trim();
            if (key == "author" && value.Length == 0)
            {
                SetManifestValue([key], null, resourcesChanged: false);
            }
            else
            {
                SetMetadata(key, value);
            }
        }
    }

    private void AppearanceInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && _draft is not null && AppearanceInput.SelectedItem is ComboBoxItem item && item.Tag is string appearance)
        {
            SetManifestValue(["appearance"], JsonValue.Create(appearance), resourcesChanged: false);
        }
    }

    private void CreationAppearanceInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _draft is null || CreationAppearanceInput.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string appearance)
        {
            return;
        }

        var target = appearance == "dark" ? SkinAppearance.Dark : SkinAppearance.Light;
        if (string.Equals(ReadString(Draft.Manifest, "appearance"), appearance, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        PushUndoSnapshot();
        SkinCreation.ChangeAppearance(Draft, target);
        _redo.Clear();
        _loading = true;
        try
        {
            LoadMetadata();
            LoadTokenFields();
            LoadBackgroundFields();
            LoadCreationControls();
        }
        finally
        {
            _loading = false;
        }

        UpdateHistoryControls();
        UpdateDirtyState();
        DraftChanged?.Invoke(this, new SkinEditorChangedEventArgs(resourcesChanged: false));
    }

    private void CreationAccentHexInput_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateCreationColor(_creationAccentField, CreationAccentHexInput.Text);

    private void CreationBackgroundHexInput_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateCreationColor(_creationBackgroundField, CreationBackgroundHexInput.Text);

    private void CreationAccentPickerButton_Click(object sender, RoutedEventArgs e) =>
        OpenCreationColorPicker(CreationAccentPickerButton, _creationAccentField);

    private void CreationBackgroundPickerButton_Click(object sender, RoutedEventArgs e) =>
        OpenCreationColorPicker(CreationBackgroundPickerButton, _creationBackgroundField);

    private void OpenCreationColorPicker(Button button, SkinEditorField? field)
    {
        if (field is null)
        {
            return;
        }

        _activeColorField = field;
        PaletteHexInput.Text = field.Value;
        ColorPalettePopup.PlacementTarget = button;
        ColorPalettePopup.IsOpen = true;
    }

    private void UpdateCreationColor(SkinEditorField? field, string value)
    {
        if (_loading || _draft is null || field is null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!CssColorParser.TryParse(value.Trim(), out _))
        {
            SetValidationError("请输入有效的 HEX 或 RGB 颜色，例如 #3B82F6。");
            return;
        }

        field.Value = value.Trim();
        if (ReferenceEquals(field, _creationAccentField))
        {
            CreationAccentSwatch.Background = field.SwatchBrush;
        }
        else if (ReferenceEquals(field, _creationBackgroundField))
        {
            CreationBackgroundSwatch.Background = field.SwatchBrush;
        }
    }

    private void PanelTransparencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _draft is null)
        {
            return;
        }

        var value = (int)Math.Round(PanelTransparencySlider.Value);
        PanelTransparencyValue.Text = $"{value}%";
        SetManifestValue(["tokens", "material.fillOpacity"], JsonValue.Create((100 - value) / 100d), resourcesChanged: false);
    }

    private void CornerRadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _draft is null)
        {
            return;
        }

        var value = Math.Round(CornerRadiusSlider.Value);
        CornerRadiusValue.Text = $"{value:0} px";
        SetManifestValue(["tokens", "radius.panel"], JsonValue.Create(value), resourcesChanged: false);
        SetManifestValue(["tokens", "radius.control"], JsonValue.Create(value), resourcesChanged: false);
    }

    private void BackgroundDarknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _draft is null)
        {
            return;
        }

        var value = (int)Math.Round(BackgroundDarknessSlider.Value);
        BackgroundDarknessValue.Text = $"{value}%";
        SetManifestValue(["background", "overlayOpacity"], JsonValue.Create(value / 100d), resourcesChanged: false);
    }

    private void CreationSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginBackgroundFocusGesture();
    private void CreationSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndBackgroundFocusGesture();
    private void CreationSlider_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            BeginBackgroundFocusGesture();
        }
    }

    private void CreationSlider_PreviewKeyUp(object sender, KeyEventArgs e) => EndBackgroundFocusGesture();
    private void CreationSlider_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => EndBackgroundFocusGesture();
    private void CreationSlider_LostMouseCapture(object sender, MouseEventArgs e) => EndBackgroundFocusGesture();

    private void BackgroundFocusModeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading || BackgroundFocusModeCheckBox.IsChecked != true)
        {
            BackgroundFocusModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void BackgroundFitInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && _draft is not null && BackgroundFitInput.SelectedItem is ComboBoxItem item && item.Tag is string fit)
        {
            SetManifestValue(ScopedPath("background", "fit"),
                fit == "inherit" ? null : JsonValue.Create(fit), resourcesChanged: false);
        }
    }

    private void ScopeInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _draft is null)
        {
            return;
        }

        LoadTokenFields();
        LoadBackgroundFields();
        LoadComponentFields();
        ClearValidationError();
        ScopeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ComponentInput_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadComponentFields();
    private void ComponentStateInput_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadComponentFields();

    private string SelectedState() => ComponentStateInput.SelectedValue as string ?? "default";

    private void SetMetadata(string key, string value)
    {
        SetManifestValue([key], JsonValue.Create(value), resourcesChanged: false);
        if (key is "name" or "version")
        {
            UpdateMetadataSummary();
        }
    }

    private void UpdateMetadataSummary()
    {
        var name = string.IsNullOrWhiteSpace(NameInput.Text) ? "新皮肤" : NameInput.Text.Trim();
        var version = string.IsNullOrWhiteSpace(VersionInput.Text) ? "未设版本" : VersionInput.Text.Trim();
        MetadataSummary.Text = $"{name} · {version}";
    }

    private void OnFieldValueChanged(SkinEditorField field)
    {
        if (_loading || _draft is null)
        {
            return;
        }

        if (ReferenceEquals(field, _creationAccentField))
        {
            CreationAccentHexInput.Text = field.Value;
            CreationAccentSwatch.Background = field.SwatchBrush;
        }
        else if (ReferenceEquals(field, _creationBackgroundField))
        {
            CreationBackgroundHexInput.Text = field.Value;
            CreationBackgroundSwatch.Background = field.SwatchBrush;
        }

        if (string.IsNullOrWhiteSpace(field.Value))
        {
            SetManifestValue(field.Path, null, resourcesChanged: false);
            return;
        }

        JsonNode? node;
        if (field.IsNumeric)
        {
            var enteredValue = field.Value.Trim();
            if (enteredValue.StartsWith('{') && enteredValue.EndsWith('}'))
            {
                node = JsonValue.Create(enteredValue);
            }
            else if (!double.TryParse(enteredValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            {
                SetManifestValue(field.Path, JsonValue.Create(enteredValue), resourcesChanged: false);
                SetValidationError(field.DisplayTransform == SkinFieldDisplayTransform.None
                    ? "请输入有效数字，或输入同类型令牌引用。"
                    : "请输入 0 到 100 的有效百分比，或输入同类型令牌引用。");
                return;
            }
            else
            {
                if (field.DisplayTransform != SkinFieldDisplayTransform.None)
                {
                    if (number is < 0 or > 100)
                    {
                        SetManifestValue(field.Path, JsonValue.Create(enteredValue), resourcesChanged: false);
                        SetValidationError("百分比应介于 0 和 100 之间。");
                        return;
                    }

                    number = field.DisplayTransform == SkinFieldDisplayTransform.TransparencyPercent
                        ? (100 - number) / 100
                        : number / 100;
                }

                node = JsonValue.Create(number);
            }
        }
        else
        {
            node = JsonValue.Create(field.Value.Trim());
        }

        SetManifestValue(field.Path, node, resourcesChanged: false);
    }

    private void SetManifestValue(IReadOnlyList<string> path, JsonNode? value, bool resourcesChanged)
    {
        if (_draft is null)
        {
            return;
        }

        var current = GetNode(Draft.Manifest, path);
        if (JsonNode.DeepEquals(current, value))
        {
            return;
        }

        if (_historyGestureActive)
        {
            if (!_gestureSnapshotPushed)
            {
                PushUndoSnapshot();
                _gestureSnapshotPushed = true;
            }
        }
        else
        {
            PushUndoSnapshot();
        }
        if (value is null)
        {
            RemoveNode(Draft.Manifest, path);
        }
        else
        {
            SetNode(Draft.Manifest, path, value.DeepClone());
        }
        _redo.Clear();
        PruneDraftResources();
        ClearValidationError();
        UpdateHistoryControls();
        UpdateDirtyState();
        DraftChanged?.Invoke(this, new SkinEditorChangedEventArgs(resourcesChanged));
    }

    private void PushUndoSnapshot()
    {
        if (_draft is null)
        {
            return;
        }

        _undo.Push(Draft.Manifest.DeepClone().AsObject());
        while (_undo.Count > 40)
        {
            var keepNewest = _undo.Take(39).Reverse().ToArray();
            _undo.Clear();
            foreach (var snapshot in keepNewest)
            {
                _undo.Push(snapshot);
            }
        }
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null || _undo.Count == 0)
        {
            return;
        }

        var previousImages = GetImageReferences(Draft.Manifest);
        _redo.Push(Draft.Manifest.DeepClone().AsObject());
        RestoreManifest(_undo.Pop());
        FinishHistoryMove(previousImages);
    }

    private void RedoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null || _redo.Count == 0)
        {
            return;
        }

        var previousImages = GetImageReferences(Draft.Manifest);
        _undo.Push(Draft.Manifest.DeepClone().AsObject());
        RestoreManifest(_redo.Pop());
        FinishHistoryMove(previousImages);
    }

    private void RestoreManifest(JsonObject snapshot)
    {
        var restored = (JsonObject)snapshot.DeepClone();
        Draft.Manifest.Clear();
        Draft.RestoreManifest(restored);

        _loading = true;
        try
        {
            LoadMetadata();
            LoadTokenFields();
            LoadBackgroundFields();
            LoadCreationControls();
            LoadComponentFields();
            ClearValidationError();
        }
        finally
        {
            _loading = false;
        }

        PruneDraftResources();
    }

    private void FinishHistoryMove(IReadOnlySet<string> previousImages)
    {
        var currentImages = GetImageReferences(Draft.Manifest);
        UpdateHistoryControls();
        UpdateDirtyState();
        DraftChanged?.Invoke(this, new SkinEditorChangedEventArgs(
            !previousImages.SetEquals(currentImages)));
    }

    private void UpdateHistoryControls()
    {
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
    }

    private void UpdateDirtyState()
    {
        var dirty = IsDirty;
        DirtyStateText.Text = dirty ? "未保存的修改" : _isNew ? "尚未保存到皮肤库" : "草稿已保存";
        DirtyStateText.Foreground = dirty
            ? (Brush)FindResource("WarningText")
            : (Brush)FindResource("SecondaryText");
    }

    private void SetSaveActionsLayout(bool isNew)
    {
        if (isNew)
        {
            SaveAsButton.Visibility = Visibility.Collapsed;
            SaveActionsGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            SaveActionsGrid.ColumnDefinitions[1].Width = new GridLength(7);
            SaveActionsGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            SaveActionsGrid.ColumnDefinitions[3].Width = new GridLength(0);
            SaveActionsGrid.ColumnDefinitions[4].Width = new GridLength(0);
            Grid.SetColumn(CancelButton, 0);
            Grid.SetColumn(SaveButton, 2);
        }
        else
        {
            SaveAsButton.Visibility = Visibility.Visible;
            SaveActionsGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            SaveActionsGrid.ColumnDefinitions[1].Width = new GridLength(7);
            SaveActionsGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            SaveActionsGrid.ColumnDefinitions[3].Width = new GridLength(7);
            SaveActionsGrid.ColumnDefinitions[4].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(SaveAsButton, 0);
            Grid.SetColumn(CancelButton, 2);
            Grid.SetColumn(SaveButton, 4);
        }
    }

    private void ChooseBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        SetBackgroundImageFromFile(dialog.FileName);
    }

    private void BackgroundDropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void BackgroundDropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            SetBackgroundImageFromFile(paths[0]);
        }

        e.Handled = true;
    }

    internal void SetBackgroundImageFromFile(string path)
    {
        if (_draft is null)
        {
            return;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length == 0 || info.Length > 50L * 1024 * 1024)
            {
                throw new InvalidDataException("背景图片必须小于 50 MB 且不能为空。");
            }

            var bytes = File.ReadAllBytes(path);
            _ = PreviewImageCropper.ReadDimensions(bytes);
            PushUndoSnapshot();
            SetBackgroundImageInScope(bytes, Path.GetFileName(path));
            _redo.Clear();
            PruneDraftResources();
            _loading = true;
            try
            {
                LoadBackgroundFields();
                LoadCreationControls();
            }
            finally { _loading = false; }
            UpdateHistoryControls();
            UpdateDirtyState();
            ClearValidationError();
            DraftChanged?.Invoke(this, new SkinEditorChangedEventArgs(resourcesChanged: true));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            SetValidationError(exception.Message);
        }
    }

    private void SetBackgroundImageInScope(byte[] bytes, string originalName)
    {
        if (EditorModeTabs.SelectedIndex == 0 || SelectedScope == "base")
        {
            Draft.SetBackgroundImage(bytes, originalName);
            return;
        }

        var rootBackground = Draft.Manifest["background"] as JsonObject;
        var rootImagePath = ReadString(rootBackground, "image");
        Draft.SetBackgroundImage(bytes, originalName);
        var imagePath = ReadString(Draft.Manifest["background"] as JsonObject, "image")
            ?? throw new InvalidDataException("无法保存背景图资源路径。");
        if (rootImagePath is null)
        {
            RemoveNode(Draft.Manifest, ["background", "image"]);
        }
        else
        {
            (Draft.Manifest["background"] as JsonObject)!["image"] = rootImagePath;
        }

        SetNode(Draft.Manifest, ScopedPath("background", "image"), JsonValue.Create(imagePath));
    }

    private void RemoveBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        var imagePath = ScopedPath("background", "image");
        if (GetNode(Draft.Manifest, imagePath) is null)
        {
            return;
        }

        var previousImages = GetImageReferences(Draft.Manifest);
        PushUndoSnapshot();
        RemoveNode(Draft.Manifest, imagePath);
        _redo.Clear();
        PruneDraftResources();
        _loading = true;
        try
        {
            LoadBackgroundFields();
            LoadCreationControls();
        }
        finally { _loading = false; }
        UpdateHistoryControls();
        UpdateDirtyState();
        DraftChanged?.Invoke(this, new SkinEditorChangedEventArgs(!previousImages.SetEquals(GetImageReferences(Draft.Manifest))));
    }

    private void CreationUseColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null || _creationBackgroundField is null)
        {
            return;
        }

        BeginBackgroundFocusGesture();
        SetManifestValue(["background", "image"], null, resourcesChanged: true);
        SetManifestValue(["background", "color"], JsonValue.Create(_creationBackgroundField.Value), resourcesChanged: false);
        EndBackgroundFocusGesture();
        _loading = true;
        try
        {
            LoadBackgroundFields();
            LoadCreationControls();
        }
        finally
        {
            _loading = false;
        }
        PruneDraftResources();
    }

    private void AddComponentPropertyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null || ComponentInput.SelectedValue is not string componentId ||
            AddComponentPropertyInput.SelectedValue is not string property)
        {
            return;
        }

        var state = SelectedState();
        var path = ScopedPath(ComponentPath(componentId, state, property).ToArray());
        if (GetNode(Draft.Manifest, path) is not null)
        {
            SetValidationError("当前组件和状态已经有这个属性。");
            return;
        }

        if (!SkinDefinitionCatalog.TryGetComponentProperty(property, out var definition))
        {
            return;
        }
        var value = definition.DefaultValue ?? string.Empty;
        if (definition.DisplayTransform == SkinFieldDisplayTransform.TransparencyPercent &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var transparency))
        {
            value = ((100 - transparency) / 100).ToString("0.###", CultureInfo.InvariantCulture);
        }

        var node = definition.EditorKind == SkinEditorFieldKind.Number
            ? JsonValue.Create(double.Parse(value, CultureInfo.InvariantCulture))
            : JsonValue.Create(value);
        SetManifestValue(path, node, resourcesChanged: false);
        LoadComponentFields();
    }

    private void ColorPickerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not SkinEditorField field)
        {
            return;
        }

        _activeColorField = field;
        PaletteHexInput.Text = field.Value;
        if (string.IsNullOrWhiteSpace(PaletteHexInput.Text) && field.IsColor &&
            CssColorParser.TryParse(field.Placeholder, out _))
        {
            PaletteHexInput.Text = field.Placeholder;
        }

        ColorPalettePopup.PlacementTarget = button;
        ColorPalettePopup.IsOpen = true;
    }

    private void PaletteColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color } && _activeColorField is { } field)
        {
            field.Value = color;
            ColorPalettePopup.IsOpen = false;
        }
    }

    private void ApplyPaletteHexButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeColorField is null)
        {
            return;
        }

        if (!CssColorParser.TryParse(PaletteHexInput.Text.Trim(), out _))
        {
            SetValidationError("请输入有效的 HEX 或 RGB 颜色，例如 #3B82F6。");
            return;
        }

        _activeColorField.Value = PaletteHexInput.Text.Trim();
        ColorPalettePopup.IsOpen = false;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this, EventArgs.Empty);
    private void CancelButton_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
    private void SaveAsButton_Click(object sender, RoutedEventArgs e) => SaveAsRequested?.Invoke(this, EventArgs.Empty);

    private void ClearValidationError()
    {
        ValidationErrorText.Text = string.Empty;
        ValidationErrorText.Visibility = Visibility.Collapsed;
    }

    public void PruneDraftResources()
    {
        _draft?.PruneUnreferencedResources(_undo.Concat(_redo));
    }

    private static string? ReadString(JsonObject? parent, string key) =>
        parent?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static HashSet<string> GetImageReferences(JsonObject manifest)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddImageReference(manifest["background"] as JsonObject, result);
        foreach (var sectionName in new[] { "variants", "scenes" })
        {
            if (manifest[sectionName] is not JsonObject section)
            {
                continue;
            }

            foreach (var patch in section.Select(pair => pair.Value).OfType<JsonObject>())
            {
                AddImageReference(patch["background"] as JsonObject, result);
            }
        }

        return result;
    }

    private static void AddImageReference(JsonObject? background, ISet<string> result)
    {
        var path = ReadString(background, "image");
        if (!string.IsNullOrWhiteSpace(path))
        {
            result.Add(path);
        }
    }

    private static string ReadScalar(JsonNode? node, string fallback, out bool numeric)
    {
        if (TryReadScalar(node, out var value, out numeric))
        {
            return value;
        }

        numeric = false;
        return fallback;
    }

    private static bool TryReadScalar(JsonNode? node, out string value, out bool numeric)
    {
        value = string.Empty;
        numeric = false;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue<string>(out var text))
        {
            value = text ?? string.Empty;
            return true;
        }

        if (jsonValue.TryGetValue<double>(out var number))
        {
            value = number.ToString("0.###", CultureInfo.InvariantCulture);
            numeric = true;
            return true;
        }

        if (jsonValue.TryGetValue<int>(out var integer))
        {
            value = integer.ToString(CultureInfo.InvariantCulture);
            numeric = true;
            return true;
        }

        return false;
    }

    private static JsonNode? GetNode(JsonObject root, IReadOnlyList<string> path)
    {
        JsonNode? current = root;
        foreach (var segment in path)
        {
            current = (current as JsonObject)?[segment];
            if (current is null)
            {
                break;
            }
        }

        return current;
    }

    private JsonObject? GetScopedObject(params string[] path) =>
        _draft is null ? null : GetNode(Draft.Manifest, ScopedPath(path)) as JsonObject;

    private IReadOnlyList<string> ScopedPath(params string[] path)
    {
        var prefix = SelectedScope switch
        {
            "light" or "dark" => new[] { "variants", SelectedScope },
            "home" or "conversation" => new[] { "scenes", SelectedScope },
            _ => []
        };
        return prefix.Concat(path).ToArray();
    }

    private static void SetNode(JsonObject root, IReadOnlyList<string> path, JsonNode? value)
    {
        var parent = root;
        for (var index = 0; index < path.Count - 1; index++)
        {
            if (parent[path[index]] is not JsonObject child)
            {
                child = new JsonObject();
                parent[path[index]] = child;
            }

            parent = child;
        }

        parent[path[^1]] = value;
    }

    private static void RemoveNode(JsonObject root, IReadOnlyList<string> path)
    {
        var parents = new List<(JsonObject Parent, string Key)>();
        JsonObject? current = root;
        foreach (var segment in path.Take(path.Count - 1))
        {
            if (current?[segment] is not JsonObject child)
            {
                return;
            }

            parents.Add((current, segment));
            current = child;
        }

        current?.Remove(path[^1]);
        for (var index = parents.Count - 1; index >= 0 && current?.Count == 0; index--)
        {
            parents[index].Parent.Remove(parents[index].Key);
            current = index == 0 ? root : parents[index - 1].Parent[parents[index - 1].Key] as JsonObject;
        }
    }

    private static IReadOnlyList<string> ComponentPath(string componentId, string state, string property) =>
        state == "default"
            ? ["components", componentId, property]
            : ["components", componentId, "states", state, property];

    private static SkinFieldDisplayTransform GetDisplayTransform(IReadOnlyList<string> path) =>
        SkinDefinitionCatalog.GetEditorField(path)?.DisplayTransform ?? SkinFieldDisplayTransform.None;

    private static string ToDisplayValue(string value, bool numeric, SkinFieldDisplayTransform transform)
    {
        if (!numeric || transform == SkinFieldDisplayTransform.None ||
            !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var storedValue))
        {
            return value;
        }

        var percent = transform == SkinFieldDisplayTransform.TransparencyPercent
            ? (1 - storedValue) * 100
            : storedValue * 100;
        return percent.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private sealed record EditorOption(string Id, string Label);

    public sealed class SkinEditorField : INotifyPropertyChanged
    {
        private readonly Action<SkinEditorField> _changed;
        private string _value;

        internal SkinEditorField(IReadOnlyList<string> path, string label, string value, string placeholder, bool isColor, bool isNumeric,
            SkinFieldDisplayTransform displayTransform,
            Action<SkinEditorField> changed)
        {
            Path = path;
            Label = label;
            _value = value;
            Placeholder = placeholder;
            IsColor = isColor;
            IsNumeric = isNumeric;
            DisplayTransform = displayTransform;
            _changed = changed;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public IReadOnlyList<string> Path { get; }
        public string Label { get; }
        public string Placeholder { get; }
        public bool IsColor { get; }
        public bool IsNumeric { get; }
        public SkinFieldDisplayTransform DisplayTransform { get; }

        public string Value
        {
            get => _value;
            set
            {
                if (string.Equals(_value, value, StringComparison.Ordinal))
                {
                    return;
                }

                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SwatchBrush)));
                _changed(this);
            }
        }

        public Brush SwatchBrush
        {
            get
            {
                if (!CssColorParser.TryParse(Value, out var color))
                {
                    return Brushes.Transparent;
                }

                return new SolidColorBrush(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
            }
        }
    }
}

public sealed class SkinEditorChangedEventArgs(bool resourcesChanged) : EventArgs
{
    public bool ResourcesChanged { get; } = resourcesChanged;
}
