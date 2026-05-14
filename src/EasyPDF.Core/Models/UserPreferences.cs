namespace EasyPDF.Core.Models;

public enum ZoomMode
{
    Manual,
    FitToWidth,
    FitToPage,
}

/// <summary>
/// User-controlled, session-persistent preferences. Persisted as JSON to
/// %APPDATA%\EasyPDF\settings.json by <c>IPreferencesRepository</c>.
///
/// Backwards compatible with the legacy schema written by the old theme service
/// (<c>{ "Theme": 1 }</c> with numeric enum) — extra fields just take their defaults.
/// </summary>
public sealed record UserPreferences
{
    public AppTheme Theme { get; init; } = AppTheme.Dark;

    /// Default zoom behavior on document open. Manual = keep the last numeric scale,
    /// FitToWidth = recompute to viewport width, FitToPage = recompute to viewport height.
    public ZoomMode DefaultZoomMode { get; init; } = ZoomMode.FitToWidth;

    /// Last color the user picked when highlighting text. Future "quick highlight" shortcut
    /// will use this; for now it's just tracked so a future feature can read it.
    public AnnotationColor DefaultHighlightColor { get; init; } = AnnotationColor.Yellow;

    /// Hex ARGB string (e.g. "#FF2563EB"). Applied next time the user enters Ink mode.
    public string DefaultInkColor { get; init; } = "#FF2563EB";

    /// Stroke thickness for ink annotations, in PDF points.
    public double DefaultInkThickness { get; init; } = 3.0;

    /// Whether the search panel's case-sensitive toggle starts checked.
    public bool SearchCaseSensitive { get; init; } = false;
}
