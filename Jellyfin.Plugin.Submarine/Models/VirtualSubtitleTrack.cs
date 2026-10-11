using System;

namespace Jellyfin.Plugin.Submarine.Models;

/// <summary>
/// Represents an on-demand remote subtitle track exposed as a virtual subtitle stream or search result.
/// </summary>
public sealed class VirtualSubtitleTrack
{
    /// <summary>
    /// Gets or sets the deterministic virtual stream index (&gt;= 10000) used in MediaSourceInfo.MediaStreams.
    /// </summary>
    public int VirtualIndex { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin media item ID associated with this subtitle track.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the provider-specific remote subtitle identifier.
    /// </summary>
    public string RemoteSubtitleId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the release or subtitle file name returned by the provider.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the subtitle provider name (e.g., OpenSubtitles, Subdl, Podnapisi).
    /// </summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ISO 639-2 three-letter or two-letter language code.
    /// </summary>
    public string Language { get; set; } = "eng";

    /// <summary>
    /// Gets or sets the original subtitle format reported by the provider.
    /// </summary>
    public string SourceFormat { get; set; } = SubtitleFormat.Srt;

    /// <summary>
    /// Gets or sets the target subtitle format in which Submarine will deliver the stream.
    /// </summary>
    public string TargetFormat { get; set; } = SubtitleFormat.Srt;

    /// <summary>
    /// Gets or sets the community rating of the subtitle if available.
    /// </summary>
    public float? CommunityRating { get; set; }

    /// <summary>
    /// Gets or sets the download count of the subtitle if available.
    /// </summary>
    public int? DownloadCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the subtitle matches the media file hash.
    /// </summary>
    public bool IsHashMatch { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the subtitle is for the hearing impaired (SDH).
    /// </summary>
    public bool IsHearingImpaired { get; set; }

    /// <summary>
    /// Gets or sets the relative Jellyfin URL from which any client can stream this converted subtitle on the fly.
    /// </summary>
    public string DeliveryUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user-facing display title injected into player subtitle selectors.
    /// </summary>
    public string DisplayTitle { get; set; } = string.Empty;
}
