using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkinManager.Core;

namespace SkinManager.Desktop;

public sealed class ColorPickerControl : FrameworkElement
{
    private const double HueStartAngleOffset = -90;
    private HsvColor _hsv;
    private byte _alpha = byte.MaxValue;
    private PointerPart _pointerPart;
    private bool _keyboardGestureActive;
    private BitmapSource? _hueRingBitmap;
    private RingBitmapKey? _hueRingBitmapKey;

    public ColorPickerControl()
    {
        Focusable = true;
        Width = 224;
        Height = 224;
        Cursor = Cursors.Cross;
        AutomationProperties.SetName(this, "色相与饱和度、明度调色盘");
    }

    public event EventHandler? ColorChanged;
    public event EventHandler? InteractionCompleted;

    public CssRgba SelectedColor => ColorPickerMath.ToRgba(_hsv, _alpha);

    public double Hue => _hsv.Hue;

    public void SetColor(CssRgba color, bool notify = false)
    {
        var nextHsv = ColorPickerMath.FromRgba(color, _hsv.Hue);
        var changed = nextHsv != _hsv || _alpha != color.Alpha;
        _hsv = nextHsv;
        _alpha = color.Alpha;
        InvalidateVisual();
        if (notify && changed)
        {
            ColorChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetAlpha(byte alpha, bool notify = false)
    {
        if (_alpha == alpha)
        {
            return;
        }

        _alpha = alpha;
        InvalidateVisual();
        if (notify)
        {
            ColorChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var outerRadius = size * 0.48;
        var ringThickness = Math.Max(12, size * 0.11);
        var innerRadius = outerRadius - ringThickness;
        DrawHueRing(drawingContext, center, outerRadius, innerRadius);

        var square = GetSquareBounds(center, innerRadius);
        DrawSaturationValueSquare(drawingContext, square);
        DrawHueMarker(drawingContext, center, (outerRadius + innerRadius) / 2, ringThickness * 0.36);
        DrawSquareMarker(drawingContext, square);

        if (IsKeyboardFocused)
        {
            var focusPen = new Pen(SystemColors.HighlightBrush, 1) { DashStyle = DashStyles.Dash };
            drawingContext.DrawRoundedRectangle(null, focusPen,
                new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)), 4, 4);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _pointerPart = HitTestPart(e.GetPosition(this));
        if (_pointerPart == PointerPart.None)
        {
            return;
        }

        CaptureMouse();
        UpdateFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pointerPart == PointerPart.None || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_pointerPart == PointerPart.None)
        {
            return;
        }

        _pointerPart = PointerPart.None;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        CompleteInteraction();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        var hadPointer = _pointerPart != PointerPart.None;
        _pointerPart = PointerPart.None;
        base.OnLostMouseCapture(e);
        if (hadPointer)
        {
            CompleteInteraction();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))
        {
            return;
        }

        var hue = _hsv.Hue;
        var saturation = _hsv.Saturation;
        var value = _hsv.Value;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.Left or Key.Right)
        {
            hue = ColorPickerMath.WrapHue(hue + (e.Key == Key.Right ? 1 : -1));
        }
        else if (e.Key is Key.Left or Key.Right)
        {
            saturation = Math.Clamp(saturation + (e.Key == Key.Right ? 0.01 : -0.01), 0, 1);
        }
        else
        {
            value = Math.Clamp(value + (e.Key == Key.Up ? 0.01 : -0.01), 0, 1);
        }

        _keyboardGestureActive = true;
        SetHsv(new HsvColor(hue, saturation, value));
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_keyboardGestureActive && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            _keyboardGestureActive = false;
            CompleteInteraction();
        }
    }

    internal void CompleteInteraction() => InteractionCompleted?.Invoke(this, EventArgs.Empty);

    private void DrawHueRing(DrawingContext context, Point center, double outerRadius, double innerRadius)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var key = new RingBitmapKey(
            Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY);
        if (_hueRingBitmap is null || _hueRingBitmapKey != key)
        {
            var pixels = new byte[key.Width * key.Height * 4];
            for (var y = 0; y < key.Height; y++)
            {
                var dipY = (y + 0.5) * 96 / key.DpiY;
                for (var x = 0; x < key.Width; x++)
                {
                    var dipX = (x + 0.5) * 96 / key.DpiX;
                    var distance = Math.Sqrt(Math.Pow(dipX - center.X, 2) + Math.Pow(dipY - center.Y, 2));
                    if (distance < innerRadius - 0.5 || distance > outerRadius + 0.5)
                    {
                        continue;
                    }

                    var edgeAlpha = Math.Clamp(Math.Min(distance - innerRadius + 0.5, outerRadius - distance + 0.5), 0, 1);
                    var alpha = (byte)Math.Round(edgeAlpha * byte.MaxValue);
                    if (alpha == 0)
                    {
                        continue;
                    }

                    var angle = Math.Atan2(dipY - center.Y, dipX - center.X) * 180 / Math.PI - HueStartAngleOffset;
                    var color = ColorPickerMath.ToRgba(new HsvColor(angle, 1, 1), byte.MaxValue);
                    var offset = (y * key.Width + x) * 4;
                    pixels[offset] = color.Blue;
                    pixels[offset + 1] = color.Green;
                    pixels[offset + 2] = color.Red;
                    pixels[offset + 3] = alpha;
                }
            }

            _hueRingBitmap = BitmapSource.Create(
                key.Width, key.Height, key.DpiX, key.DpiY,
                PixelFormats.Bgra32, null, pixels, key.Width * 4);
            _hueRingBitmap.Freeze();
            _hueRingBitmapKey = key;
        }

        context.DrawImage(_hueRingBitmap, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    private void DrawSaturationValueSquare(DrawingContext context, Rect bounds)
    {
        var hueColor = ColorPickerMath.ToMediaColor(ColorPickerMath.ToRgba(new HsvColor(_hsv.Hue, 1, 1), byte.MaxValue));
        context.DrawRectangle(new SolidColorBrush(hueColor), null, bounds);
        var whiteOverlay = new LinearGradientBrush(Colors.White, Colors.Transparent, 0);
        context.DrawRectangle(whiteOverlay, null, bounds);
        var blackOverlay = new LinearGradientBrush(Colors.Transparent, Colors.Black, 90);
        context.DrawRectangle(blackOverlay, new Pen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), 1), bounds);
    }

    private void DrawHueMarker(DrawingContext context, Point center, double radius, double markerRadius)
    {
        var angle = (_hsv.Hue + HueStartAngleOffset) * Math.PI / 180;
        var position = new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
        context.DrawEllipse(null, new Pen(Brushes.Black, 3), position, markerRadius, markerRadius);
        context.DrawEllipse(null, new Pen(Brushes.White, 1.6), position, markerRadius, markerRadius);
    }

    private void DrawSquareMarker(DrawingContext context, Rect bounds)
    {
        var position = new Point(bounds.Left + _hsv.Saturation * bounds.Width,
            bounds.Top + (1 - _hsv.Value) * bounds.Height);
        context.DrawEllipse(null, new Pen(Brushes.Black, 3), position, 6.5, 6.5);
        context.DrawEllipse(null, new Pen(Brushes.White, 1.6), position, 6.5, 6.5);
    }

    private PointerPart HitTestPart(Point point)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var outerRadius = size * 0.48;
        var innerRadius = outerRadius - Math.Max(12, size * 0.11);
        var distance = (point - center).Length;
        if (distance >= innerRadius - 4 && distance <= outerRadius + 4)
        {
            return PointerPart.Hue;
        }

        var square = GetSquareBounds(center, innerRadius);
        return square.Contains(point) ? PointerPart.SaturationValue : PointerPart.None;
    }

    private void UpdateFromPoint(Point point)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        if (_pointerPart == PointerPart.Hue)
        {
            var delta = point - center;
            var hue = ColorPickerMath.WrapHue(Math.Atan2(delta.Y, delta.X) * 180 / Math.PI - HueStartAngleOffset);
            SetHsv(_hsv with { Hue = hue });
            return;
        }

        var size = Math.Min(ActualWidth, ActualHeight);
        var innerRadius = size * 0.48 - Math.Max(12, size * 0.11);
        var square = GetSquareBounds(center, innerRadius);
        var saturation = Math.Clamp((point.X - square.Left) / square.Width, 0, 1);
        var value = 1 - Math.Clamp((point.Y - square.Top) / square.Height, 0, 1);
        SetHsv(_hsv with { Saturation = saturation, Value = value });
    }

    private static Rect GetSquareBounds(Point center, double innerRadius)
    {
        var side = Math.Max(36, (innerRadius - 6) * Math.Sqrt(2));
        return new Rect(center.X - side / 2, center.Y - side / 2, side, side);
    }

    private void SetHsv(HsvColor hsv)
    {
        hsv = hsv with
        {
            Hue = ColorPickerMath.WrapHue(hsv.Hue),
            Saturation = Math.Clamp(hsv.Saturation, 0, 1),
            Value = Math.Clamp(hsv.Value, 0, 1)
        };
        if (_hsv == hsv)
        {
            return;
        }

        _hsv = hsv;
        InvalidateVisual();
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    private enum PointerPart
    {
        None,
        Hue,
        SaturationValue
    }

    private readonly record struct RingBitmapKey(int Width, int Height, double DpiX, double DpiY);
}
