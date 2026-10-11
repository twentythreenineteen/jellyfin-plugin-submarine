using Jellyfin.Plugin.Submarine.Services;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Submarine.Filters;

/// <summary>
/// ASP.NET Core result filter that intercepts <see cref="PlaybackInfoResponse"/> payloads
/// and injects on-demand virtual subtitle streams so native clients across all platforms
/// (Roku, Android TV, Swiftfin, Kodi, Web, Desktop, Mobile) can search, select, convert,
/// and stream remote subtitles directly from their built-in CC/Subtitle selector.
/// </summary>
public class PlaybackInfoSubtitleFilter : IAsyncResultFilter
{
    private readonly ISubmarineSubtitleService _subtitleService;
    private readonly ILogger<PlaybackInfoSubtitleFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackInfoSubtitleFilter"/> class.
    /// </summary>
    /// <param name="subtitleService">The Submarine subtitle orchestration service.</param>
    /// <param name="logger">The logger instance.</param>
    public PlaybackInfoSubtitleFilter(
        ISubmarineSubtitleService subtitleService,
        ILogger<PlaybackInfoSubtitleFilter> logger)
    {
        _subtitleService = subtitleService ?? throw new ArgumentNullException(nameof(subtitleService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (context.Result is ObjectResult { Value: PlaybackInfoResponse { MediaSources: not null } playbackInfo })
        {
            var itemId = TryExtractItemId(context);
            if (itemId != Guid.Empty)
            {
                try
                {
                    await _subtitleService
                        .InjectVirtualStreamsAsync(itemId, playbackInfo, context.HttpContext.RequestAborted)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Virtual subtitle stream injection was canceled for item {ItemId}.", itemId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Submarine failed to inject virtual subtitle streams for item {ItemId}; proceeding with standard PlaybackInfo.",
                        itemId);
                }
            }
        }

        await next().ConfigureAwait(false);
    }

    private static Guid TryExtractItemId(ResultExecutingContext context)
    {
        if (context.RouteData.Values.TryGetValue("itemId", out var rawItemId) &&
            rawItemId is not null &&
            Guid.TryParse(rawItemId.ToString(), out var parsedItemId))
        {
            return parsedItemId;
        }

        if (context.RouteData.Values.TryGetValue("id", out var rawId) &&
            rawId is not null &&
            Guid.TryParse(rawId.ToString(), out var parsedId))
        {
            return parsedId;
        }

        return Guid.Empty;
    }
}
