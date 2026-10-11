namespace Jellyfin.Plugin.Submarine.Models;

/// <summary>
/// Request payload to download, convert, and optionally persist a remote subtitle track for an item.
/// </summary>
public sealed class SubmarineDownloadRequest
{
    /// <summary>
    /// Gets or sets the remote subtitle ID to download.
    /// </summary>
    public string RemoteSubtitleId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the desired target subtitle format (srt, vtt, ass, ssa, sub, ttml).
    /// </summary>
    public string TargetFormat { get; set; } = SubtitleFormat.Srt;

    /// <summary>
    /// Gets or sets an optional timing offset in milliseconds (positive delays, negative advances).
    /// </summary>
    public int OffsetMs { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the subtitle should also be persisted to the Jellyfin media library.
    /// </summary>
    public bool PersistToLibrary { get; set; } = true;

    /// <summary>
    /// Gets or sets the optional language code.
    /// </summary>
    public string? Language { get; set; } = "eng";
}

/// <summary>
/// Result returned after downloading and converting a remote subtitle track.
/// </summary>
public sealed class SubmarineDownloadResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the download and conversion succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets a human-readable status message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the instant stream delivery URL for immediate playback injection.
    /// </summary>
    public string DeliveryUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the normalized target subtitle format.
    /// </summary>
    public string Format { get; set; } = SubtitleFormat.Srt;

    /// <summary>
    /// Gets or sets the number of parsed subtitle cues in the converted file.
    /// </summary>
    public int CueCount { get; set; }

    /// <summary>
    /// Gets or sets the virtual or saved subtitle stream index.
    /// </summary>
    public int? SavedStreamIndex { get; set; }
}

/// <summary>
/// Request payload for converting raw subtitle text between formats.
/// </summary>
public sealed class SubmarineConvertRequest
{
    /// <summary>
    /// Gets or sets the raw source subtitle content.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source subtitle format hint (optional; auto-detected if omitted).
    /// </summary>
    public string SourceFormat { get; set; } = SubtitleFormat.Srt;

    /// <summary>
    /// Gets or sets the target subtitle format (srt, vtt, ass, ssa, sub, ttml).
    /// </summary>
    public string TargetFormat { get; set; } = SubtitleFormat.Vtt;

    /// <summary>
    /// Gets or sets an optional timing offset in milliseconds.
    /// </summary>
    public int OffsetMs { get; set; }
}

/// <summary>
/// Response payload containing converted subtitle text and metadata.
/// </summary>
public sealed class SubmarineConvertResult
{
    /// <summary>
    /// Gets or sets the converted subtitle text.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the detected or specified source subtitle format.
    /// </summary>
    public string SourceFormat { get; set; } = SubtitleFormat.Srt;

    /// <summary>
    /// Gets or sets the target subtitle format.
    /// </summary>
    public string TargetFormat { get; set; } = SubtitleFormat.Vtt;

    /// <summary>
    /// Gets or sets the number of cues converted.
    /// </summary>
    public int CueCount { get; set; }
}
