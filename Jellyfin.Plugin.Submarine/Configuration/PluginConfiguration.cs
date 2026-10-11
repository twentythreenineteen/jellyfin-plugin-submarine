using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Submarine.Configuration;

/// <summary>
/// Configuration settings for the Submarine Jellyfin plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether virtual subtitle streams are injected into PlaybackInfo responses
    /// for universal native client support (Roku, Android TV, Swiftfin, Kodi, Web, Desktop, Mobile).
    /// </summary>
    public bool EnableUniversalVirtualStreams { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the interactive Plex-style Subtitle Search &amp; Download modal
    /// is injected into Jellyfin Web, Jellyfin Media Player, and Mobile WebViews.
    /// </summary>
    public bool EnableWebPlayerModal { get; set; } = true;

    /// <summary>
    /// Gets or sets comma-separated ISO 639-2/B or 639-1 language codes to search automatically for virtual subtitle tracks.
    /// </summary>
    public string PreferredLanguages { get; set; } = "eng,spa,fre,ger,jpn";

    /// <summary>
    /// Gets or sets the maximum number of virtual remote subtitle streams injected per language.
    /// </summary>
    public int MaxVirtualStreamsPerLanguage { get; set; } = 4;

    /// <summary>
    /// Gets or sets the default target subtitle format for downloads and streams (srt, vtt, ass, ssa, sub, ttml).
    /// </summary>
    public string DefaultTargetFormat { get; set; } = "srt";

    /// <summary>
    /// Gets or sets the comma-separated list of subtitle formats exposed in universal virtual tracks.
    /// </summary>
    public string EnabledVirtualFormats { get; set; } = "srt,vtt,ass";

    /// <summary>
    /// Gets or sets a value indicating whether subtitles streamed or downloaded on-the-fly are also saved
    /// permanently to the media item in the Jellyfin library.
    /// </summary>
    public bool PersistDownloadedSubtitlesToLibrary { get; set; } = true;

    /// <summary>
    /// Gets or sets the timeout in seconds for remote subtitle provider searches during PlaybackInfo interception.
    /// </summary>
    public int SearchTimeoutSeconds { get; set; } = 6;

    /// <summary>
    /// Gets or sets a value indicating whether hearing-impaired (SDH) subtitles are included in results.
    /// </summary>
    public bool IncludeHearingImpaired { get; set; } = true;
}
