using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Submarine.Models;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Submarine.Services;

/// <summary>
/// Orchestrates on-the-fly remote subtitle searching, virtual stream injection into PlaybackInfo,
/// real-time subtitle format conversion, and optional media library persistence.
/// </summary>
public interface ISubmarineSubtitleService
{
    /// <summary>
    /// Searches all configured Jellyfin subtitle providers for the specified item and language,
    /// returning virtual subtitle tracks mapped to deterministic stream indices.
    /// </summary>
    /// <param name="itemId">The Jellyfin media item ID.</param>
    /// <param name="language">The ISO 639-2 or 639-1 language code.</param>
    /// <param name="customQuery">Optional free-text filter to match release name, provider, or format.</param>
    /// <param name="targetFormat">Optional target subtitle format override.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A read-only list of virtual subtitle tracks.</returns>
    Task<IReadOnlyList<VirtualSubtitleTrack>> SearchSubtitlesAsync(
        Guid itemId,
        string language,
        string? customQuery = null,
        string? targetFormat = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Dynamically injects virtual remote subtitle streams into a <see cref="PlaybackInfoResponse"/>
    /// so every Jellyfin client (Roku, Android TV, Swiftfin, Kodi, Web, Desktop, Mobile) can select
    /// remote subtitles directly from its native subtitle picker.
    /// </summary>
    /// <param name="itemId">The Jellyfin media item ID.</param>
    /// <param name="playbackInfo">The playback info response to enrich.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task InjectVirtualStreamsAsync(
        Guid itemId,
        PlaybackInfoResponse playbackInfo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a remote subtitle track on-the-fly and converts it to the requested format and timing offset.
    /// </summary>
    /// <param name="itemId">The Jellyfin media item ID.</param>
    /// <param name="virtualIndex">Optional virtual stream index from PlaybackInfo.</param>
    /// <param name="remoteSubtitleId">Optional remote provider subtitle ID.</param>
    /// <param name="targetFormat">Target subtitle format (srt, vtt, ass, ssa, sub, ttml).</param>
    /// <param name="offsetMs">Optional timing offset in milliseconds.</param>
    /// <param name="persistToLibrary">Whether to also save the subtitle to the Jellyfin media library.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The UTF-8 encoded subtitle bytes, MIME content type, and file extension, or null if not found.</returns>
    Task<(byte[] Content, string ContentType, string FileExtension)?> GetAndConvertSubtitleStreamAsync(
        Guid itemId,
        int? virtualIndex,
        string? remoteSubtitleId,
        string targetFormat,
        int offsetMs = 0,
        bool persistToLibrary = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads, converts, and prepares a remote subtitle track for immediate playback injection and optional persistence.
    /// </summary>
    /// <param name="itemId">The Jellyfin media item ID.</param>
    /// <param name="request">The download and conversion request parameters.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="SubmarineDownloadResult"/> containing the stream URL and conversion metadata.</returns>
    Task<SubmarineDownloadResult> DownloadAndApplySubtitleAsync(
        Guid itemId,
        SubmarineDownloadRequest request,
        CancellationToken cancellationToken = default);
}
