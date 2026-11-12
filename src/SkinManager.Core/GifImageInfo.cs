namespace SkinManager.Core;

public readonly record struct GifImageInfo(
    int Width,
    int Height,
    int FirstFrameLeft,
    int FirstFrameTop,
    int FirstFrameWidth,
    int FirstFrameHeight,
    bool HasGlobalColorTable,
    byte BackgroundColorIndex,
    byte BackgroundRed,
    byte BackgroundGreen,
    byte BackgroundBlue,
    bool FirstFrameHasTransparency,
    byte FirstFrameTransparentIndex);
