using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Submarine.Configuration;
using Jellyfin.Plugin.Submarine.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using SubtitleFormat = Jellyfin.Plugin.Submarine.Models.SubtitleFormat;

namespace Jellyfin.Plugin.Submarine.Services;

/// <summary>
/// Default implementation of <see cref="ISubmarineSubtitleService"/> that coordinates remote subtitle searches,
/// deterministic virtual stream indexing, on-the-fly format conversion, and library persistence.
/// </summary>
public sealed class SubmarineSubtitleService : ISubmarineSubtitleService
{
    /// <summary>
    /// Starting stream index for Submarine virtual subtitle tracks to avoid colliding with physical streams.
    /// </summary>
    public const int VirtualIndexBase = 10000;

    private static readonly Regex HearingImpairedHeuristicRegex = new(
        @"(\bSDH\b|\(HI\)|\[HI\]|\bHearing[\s\-_]*Impaired\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ISubtitleManager _subtitleManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ISubtitleFormatConverter _formatConverter;
    private readonly ILogger<SubmarineSubtitleService> _logger;

    private readonly ConcurrentDictionary<int, VirtualSubtitleTrack> _tracksByIndex = new();
    private readonly ConcurrentDictionary<(Guid ItemId, string RemoteId, string Format), int> _indexByKey = new();
    private int _nextVirtualIndex = VirtualIndexBase - 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubmarineSubtitleService"/> class.
    /// </summary>
    /// <param name="subtitleManager">The Jellyfin subtitle manager.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="formatConverter">The Submarine subtitle format converter.</param>
    /// <param name="logger">The logger instance.</param>
    public SubmarineSubtitleService(
        ISubtitleManager subtitleManager,
        ILibraryManager libraryManager,
        ISubtitleFormatConverter formatConverter,
        ILogger<SubmarineSubtitleService> logger)
    {
        _subtitleManager = subtitleManager ?? throw new ArgumentNullException(nameof(subtitleManager));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _formatConverter = formatConverter ?? throw new ArgumentNullException(nameof(formatConverter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VirtualSubtitleTrack>> SearchSubtitlesAsync(
        Guid itemId,
        string language,
        string? customQuery = null,
        string? targetFormat = null,
        CancellationToken cancellationToken = default)
    {
        var config = GetConfiguration();
        var effectiveLanguage = string.IsNullOrWhiteSpace(language) ? "eng" : language.Trim().ToLowerInvariant();
        var effectiveTargetFormat = SubtitleFormat.NormalizeFormat(
            string.IsNullOrWhiteSpace(targetFormat) ? config.DefaultTargetFormat : targetFormat);

        var item = _libraryManager.GetItemById(itemId);
        RemoteSubtitleInfo[]? remoteResults = null;

        if (item is Video video)
        {
            remoteResults = await _subtitleManager
                .SearchSubtitles(video, effectiveLanguage, null, true, cancellationToken)
                .ConfigureAwait(false);
        }

        if (remoteResults is null || remoteResults.Length == 0)
        {
            var searchRequest = new SubtitleSearchRequest
            {
                Language = effectiveLanguage,
                MediaPath = item?.Path ?? string.Empty,
                Name = !string.IsNullOrWhiteSpace(customQuery) ? customQuery : (item?.Name ?? string.Empty),
                ProductionYear = item?.ProductionYear,
                SearchAllProviders = true,
                IsPerfectMatch = false,
                ContentType = item is Episode ? VideoContentType.Episode : VideoContentType.Movie
            };

            if (item is Episode episode)
            {
                searchRequest.SeriesName = episode.SeriesName;
                searchRequest.ParentIndexNumber = episode.ParentIndexNumber;
                searchRequest.IndexNumber = episode.IndexNumber;
            }

            remoteResults = await _subtitleManager
                .SearchSubtitles(searchRequest, cancellationToken)
                .ConfigureAwait(false);
        }

        if (remoteResults is null || remoteResults.Length == 0)
        {
            return Array.Empty<VirtualSubtitleTrack>();
        }

        IEnumerable<RemoteSubtitleInfo> filtered = remoteResults;

        if (!config.IncludeHearingImpaired)
        {
            filtered = filtered.Where(r => !IsHearingImpairedSubtitle(r));
        }

        if (!string.IsNullOrWhiteSpace(customQuery))
        {
            var q = customQuery.Trim();
            filtered = filtered.Where(r =>
                (r.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.ProviderName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.Format?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.Comment?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var ordered = filtered
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .OrderByDescending(r => r.IsHashMatch == true)
            .ThenByDescending(r => r.CommunityRating ?? 0f)
            .ThenByDescending(r => r.DownloadCount ?? 0)
            .ToList();

        var tracks = new List<VirtualSubtitleTrack>(ordered.Count);
        foreach (var info in ordered)
        {
            var track = CreateAndCacheVirtualTrack(itemId, info, effectiveLanguage, effectiveTargetFormat);
            tracks.Add(track);
        }

        return tracks;
    }

    /// <inheritdoc />
    public async Task InjectVirtualStreamsAsync(
        Guid itemId,
        PlaybackInfoResponse playbackInfo,
        CancellationToken cancellationToken = default)
    {
        if (playbackInfo?.MediaSources is null || playbackInfo.MediaSources.Count == 0)
        {
            return;
        }

        var config = GetConfiguration();
        if (!config.EnableUniversalVirtualStreams)
        {
            return;
        }

        var preferredLanguages = ParseCsvList(config.PreferredLanguages, "eng");
        var enabledFormats = ParseCsvList(config.EnabledVirtualFormats, config.DefaultTargetFormat)
            .Select(SubtitleFormat.NormalizeFormat)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var maxPerLanguage = Math.Max(1, config.MaxVirtualStreamsPerLanguage);
        var timeoutSeconds = Math.Clamp(config.SearchTimeoutSeconds, 1, 30);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var primaryFormat = enabledFormats[0];
        var injectedStreams = new List<MediaStream>();

        foreach (var lang in preferredLanguages)
        {
            IReadOnlyList<VirtualSubtitleTrack> searchTracks;
            try
            {
                searchTracks = await SearchSubtitlesAsync(itemId, lang, null, primaryFormat, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Timed out searching virtual subtitles for item {ItemId} language {Language}.", itemId, lang);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error searching virtual subtitles for item {ItemId} language {Language}.", itemId, lang);
                continue;
            }

            foreach (var baseTrack in searchTracks.Take(maxPerLanguage))
            {
                foreach (var fmt in enabledFormats)
                {
                    var fmtTrack = EnsureFormatVariantTrack(baseTrack, fmt);
                    injectedStreams.Add(new MediaStream
                    {
                        Type = MediaStreamType.Subtitle,
                        Index = fmtTrack.VirtualIndex,
                        Codec = SubtitleFormat.GetJellyfinCodec(fmtTrack.TargetFormat),
                        Language = fmtTrack.Language,
                        Title = fmtTrack.DisplayTitle,
                        IsExternal = true,
                        SupportsExternalStream = true,
                        DeliveryMethod = SubtitleDeliveryMethod.External,
                        DeliveryUrl = fmtTrack.DeliveryUrl,
                        IsForced = false,
                        IsDefault = false,
                        IsHearingImpaired = fmtTrack.IsHearingImpaired
                    });
                }
            }
        }

        if (injectedStreams.Count == 0)
        {
            return;
        }

        foreach (var mediaSource in playbackInfo.MediaSources)
        {
            var streamList = mediaSource.MediaStreams?.ToList() ?? new List<MediaStream>();
            var existingIndices = streamList.Select(s => s.Index).ToHashSet();

            foreach (var virtualStream in injectedStreams)
            {
                if (existingIndices.Add(virtualStream.Index))
                {
                    streamList.Add(virtualStream);
                }
            }

            mediaSource.MediaStreams = streamList;
        }
    }

    /// <inheritdoc />
    public async Task<(byte[] Content, string ContentType, string FileExtension)?> GetAndConvertSubtitleStreamAsync(
        Guid itemId,
        int? virtualIndex,
        string? remoteSubtitleId,
        string targetFormat,
        int offsetMs = 0,
        bool persistToLibrary = false,
        CancellationToken cancellationToken = default)
    {
        VirtualSubtitleTrack? cachedTrack = null;
        if (virtualIndex.HasValue && _tracksByIndex.TryGetValue(virtualIndex.Value, out var foundTrack))
        {
            cachedTrack = foundTrack;
            if (string.IsNullOrWhiteSpace(remoteSubtitleId))
            {
                remoteSubtitleId = foundTrack.RemoteSubtitleId;
            }
        }

        if (string.IsNullOrWhiteSpace(remoteSubtitleId))
        {
            _logger.LogWarning(
                "Could not resolve remoteSubtitleId for itemId={ItemId}, virtualIndex={VirtualIndex}.",
                itemId,
                virtualIndex);
            return null;
        }

        var subtitleResponse = await _subtitleManager
            .GetRemoteSubtitles(remoteSubtitleId, cancellationToken)
            .ConfigureAwait(false);

        if (subtitleResponse?.Stream is null)
        {
            _logger.LogWarning("Provider returned null subtitle stream for remoteSubtitleId={RemoteSubtitleId}.", remoteSubtitleId);
            return null;
        }

        string rawContent;
        await using (var responseStream = subtitleResponse.Stream)
        using (var reader = new StreamReader(responseStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            rawContent = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return null;
        }

        var normalizedTarget = SubtitleFormat.NormalizeFormat(targetFormat);
        var sourceHint = !string.IsNullOrWhiteSpace(subtitleResponse.Format)
            ? subtitleResponse.Format
            : cachedTrack?.SourceFormat;

        var convertedContent = _formatConverter.Convert(rawContent, sourceHint, normalizedTarget, offsetMs);
        var config = GetConfiguration();

        if (persistToLibrary && config.PersistDownloadedSubtitlesToLibrary && itemId != Guid.Empty)
        {
            try
            {
                if (_libraryManager.GetItemById(itemId) is Video video)
                {
                    await _subtitleManager
                        .DownloadSubtitles(video, remoteSubtitleId, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Non-fatal error persisting downloaded subtitle {RemoteSubtitleId} to library for item {ItemId}.",
                    remoteSubtitleId,
                    itemId);
            }
        }

        var bytes = Encoding.UTF8.GetBytes(convertedContent);
        var mimeType = SubtitleFormat.GetMimeType(normalizedTarget);
        return (bytes, mimeType, normalizedTarget);
    }

    /// <inheritdoc />
    public async Task<SubmarineDownloadResult> DownloadAndApplySubtitleAsync(
        Guid itemId,
        SubmarineDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedFormat = SubtitleFormat.NormalizeFormat(request.TargetFormat);
        if (string.IsNullOrWhiteSpace(request.RemoteSubtitleId))
        {
            return new SubmarineDownloadResult
            {
                Success = false,
                Message = "RemoteSubtitleId is required.",
                Format = normalizedFormat
            };
        }

        var virtualTrack = EnsureDirectVirtualTrack(
            itemId,
            request.RemoteSubtitleId,
            request.Language ?? "eng",
            normalizedFormat);

        var streamTuple = await GetAndConvertSubtitleStreamAsync(
                itemId,
                virtualTrack.VirtualIndex,
                request.RemoteSubtitleId,
                normalizedFormat,
                request.OffsetMs,
                request.PersistToLibrary,
                cancellationToken)
            .ConfigureAwait(false);

        if (streamTuple is null || streamTuple.Value.Content.Length == 0)
        {
            return new SubmarineDownloadResult
            {
                Success = false,
                Message = "Subtitle provider returned an empty or unavailable subtitle stream.",
                Format = normalizedFormat
            };
        }

        var convertedText = Encoding.UTF8.GetString(streamTuple.Value.Content);
        var cueCount = _formatConverter.Parse(convertedText, normalizedFormat).Count;

        var deliveryUrl = virtualTrack.DeliveryUrl;
        if (request.OffsetMs != 0)
        {
            deliveryUrl = string.Format(
                CultureInfo.InvariantCulture,
                "{0}?offsetMs={1}",
                deliveryUrl,
                request.OffsetMs);
        }

        return new SubmarineDownloadResult
        {
            Success = true,
            Message = "Subtitle downloaded and converted on-the-fly.",
            DeliveryUrl = deliveryUrl,
            Format = normalizedFormat,
            CueCount = cueCount,
            SavedStreamIndex = virtualTrack.VirtualIndex
        };
    }

    private static bool IsHearingImpairedSubtitle(RemoteSubtitleInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.Name) && HearingImpairedHeuristicRegex.IsMatch(info.Name))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(info.Comment) && HearingImpairedHeuristicRegex.IsMatch(info.Comment))
        {
            return true;
        }

        return false;
    }

    private VirtualSubtitleTrack CreateAndCacheVirtualTrack(
        Guid itemId,
        RemoteSubtitleInfo info,
        string language,
        string targetFormat)
    {
        var remoteId = info.Id ?? string.Empty;
        var normalizedTarget = SubtitleFormat.NormalizeFormat(targetFormat);
        var sourceFormat = SubtitleFormat.NormalizeFormat(
            !string.IsNullOrWhiteSpace(info.Format) ? info.Format : Path.GetExtension(info.Name));
        var lang = !string.IsNullOrWhiteSpace(info.ThreeLetterISOLanguageName)
            ? info.ThreeLetterISOLanguageName.ToLowerInvariant()
            : language;
        var provider = !string.IsNullOrWhiteSpace(info.ProviderName) ? info.ProviderName : "Remote";
        var name = !string.IsNullOrWhiteSpace(info.Name) ? info.Name : remoteId;
        var isHashMatch = info.IsHashMatch == true;
        var isHearingImpaired = IsHearingImpairedSubtitle(info);

        var key = (itemId, remoteId, normalizedTarget);
        var virtualIndex = _indexByKey.GetOrAdd(key, _ => Interlocked.Increment(ref _nextVirtualIndex));

        var displayTitle = FormatDisplayTitle(provider, lang, normalizedTarget, name, isHashMatch, isHearingImpaired);
        var deliveryUrl = string.Format(
            CultureInfo.InvariantCulture,
            "/Plugins/Submarine/Stream/{0}/{1}/Stream.{2}",
            itemId,
            virtualIndex,
            normalizedTarget);

        var track = new VirtualSubtitleTrack
        {
            VirtualIndex = virtualIndex,
            ItemId = itemId,
            RemoteSubtitleId = remoteId,
            Name = name,
            ProviderName = provider,
            Language = lang,
            SourceFormat = sourceFormat,
            TargetFormat = normalizedTarget,
            CommunityRating = info.CommunityRating,
            DownloadCount = info.DownloadCount,
            IsHashMatch = isHashMatch,
            IsHearingImpaired = isHearingImpaired,
            DeliveryUrl = deliveryUrl,
            DisplayTitle = displayTitle
        };

        _tracksByIndex[virtualIndex] = track;
        return track;
    }

    private VirtualSubtitleTrack EnsureFormatVariantTrack(VirtualSubtitleTrack baseTrack, string targetFormat)
    {
        var normalizedTarget = SubtitleFormat.NormalizeFormat(targetFormat);
        if (string.Equals(baseTrack.TargetFormat, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return baseTrack;
        }

        var key = (baseTrack.ItemId, baseTrack.RemoteSubtitleId, normalizedTarget);
        var virtualIndex = _indexByKey.GetOrAdd(key, _ => Interlocked.Increment(ref _nextVirtualIndex));
        if (_tracksByIndex.TryGetValue(virtualIndex, out var existing))
        {
            return existing;
        }

        var displayTitle = FormatDisplayTitle(
            baseTrack.ProviderName,
            baseTrack.Language,
            normalizedTarget,
            baseTrack.Name,
            baseTrack.IsHashMatch,
            baseTrack.IsHearingImpaired);

        var deliveryUrl = string.Format(
            CultureInfo.InvariantCulture,
            "/Plugins/Submarine/Stream/{0}/{1}/Stream.{2}",
            baseTrack.ItemId,
            virtualIndex,
            normalizedTarget);

        var variant = new VirtualSubtitleTrack
        {
            VirtualIndex = virtualIndex,
            ItemId = baseTrack.ItemId,
            RemoteSubtitleId = baseTrack.RemoteSubtitleId,
            Name = baseTrack.Name,
            ProviderName = baseTrack.ProviderName,
            Language = baseTrack.Language,
            SourceFormat = baseTrack.SourceFormat,
            TargetFormat = normalizedTarget,
            CommunityRating = baseTrack.CommunityRating,
            DownloadCount = baseTrack.DownloadCount,
            IsHashMatch = baseTrack.IsHashMatch,
            IsHearingImpaired = baseTrack.IsHearingImpaired,
            DeliveryUrl = deliveryUrl,
            DisplayTitle = displayTitle
        };

        _tracksByIndex[virtualIndex] = variant;
        return variant;
    }

    private VirtualSubtitleTrack EnsureDirectVirtualTrack(
        Guid itemId,
        string remoteSubtitleId,
        string language,
        string targetFormat)
    {
        var normalizedTarget = SubtitleFormat.NormalizeFormat(targetFormat);
        var key = (itemId, remoteSubtitleId, normalizedTarget);
        var virtualIndex = _indexByKey.GetOrAdd(key, _ => Interlocked.Increment(ref _nextVirtualIndex));

        if (_tracksByIndex.TryGetValue(virtualIndex, out var existing))
        {
            return existing;
        }

        var deliveryUrl = string.Format(
            CultureInfo.InvariantCulture,
            "/Plugins/Submarine/Stream/{0}/{1}/Stream.{2}",
            itemId,
            virtualIndex,
            normalizedTarget);

        var track = new VirtualSubtitleTrack
        {
            VirtualIndex = virtualIndex,
            ItemId = itemId,
            RemoteSubtitleId = remoteSubtitleId,
            Name = remoteSubtitleId,
            ProviderName = "Submarine",
            Language = string.IsNullOrWhiteSpace(language) ? "eng" : language.Trim().ToLowerInvariant(),
            SourceFormat = SubtitleFormat.Srt,
            TargetFormat = normalizedTarget,
            DeliveryUrl = deliveryUrl,
            DisplayTitle = FormatDisplayTitle("Submarine", language, normalizedTarget, remoteSubtitleId, false, false)
        };

        _tracksByIndex[virtualIndex] = track;
        return track;
    }

    private static string FormatDisplayTitle(
        string providerName,
        string language,
        string targetFormat,
        string name,
        bool isHashMatch,
        bool isHearingImpaired)
    {
        var matchBadge = isHashMatch ? " ★Match" : string.Empty;
        var sdhBadge = isHearingImpaired ? " [SDH]" : string.Empty;
        return string.Format(
            CultureInfo.InvariantCulture,
            "⬇ [Submarine • {0}] {1} ({2}) - {3}{4}{5}",
            providerName,
            language.ToUpperInvariant(),
            targetFormat.ToUpperInvariant(),
            name,
            matchBadge,
            sdhBadge);
    }

    private static List<string> ParseCsvList(string? rawCsv, string fallbackDefault)
    {
        if (string.IsNullOrWhiteSpace(rawCsv))
        {
            return new List<string> { fallbackDefault };
        }

        var list = rawCsv
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count == 0)
        {
            list.Add(fallbackDefault);
        }

        return list;
    }

    private static PluginConfiguration GetConfiguration()
    {
        try
        {
            return Plugin.Instance?.CurrentConfiguration ?? new PluginConfiguration();
        }
        catch
        {
            return new PluginConfiguration();
        }
    }
}
