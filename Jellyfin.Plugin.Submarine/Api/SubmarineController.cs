using System.Net.Mime;
using Jellyfin.Plugin.Submarine.Models;
using Jellyfin.Plugin.Submarine.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Submarine.Api;

/// <summary>
/// REST API controller for Submarine on-demand subtitle search, real-time format conversion,
/// virtual stream delivery, library persistence, and web player asset injection.
/// </summary>
[ApiController]
[Route("Plugins/Submarine")]
public class SubmarineController : ControllerBase
{
    private readonly ISubmarineSubtitleService _subtitleService;
    private readonly ISubtitleFormatConverter _formatConverter;
    private readonly ILogger<SubmarineController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubmarineController"/> class.
    /// </summary>
    /// <param name="subtitleService">The Submarine subtitle service.</param>
    /// <param name="formatConverter">The subtitle format converter service.</param>
    /// <param name="logger">The logger instance.</param>
    public SubmarineController(
        ISubmarineSubtitleService subtitleService,
        ISubtitleFormatConverter formatConverter,
        ILogger<SubmarineController> logger)
    {
        _subtitleService = subtitleService ?? throw new ArgumentNullException(nameof(subtitleService));
        _formatConverter = formatConverter ?? throw new ArgumentNullException(nameof(formatConverter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Searches remote subtitle providers on-the-fly for a specific media item.
    /// </summary>
    /// <param name="itemId">The Jellyfin media item identifier.</param>
    /// <param name="language">The three-letter ISO 639-2/B or two-letter ISO 639-1 language code.</param>
    /// <param name="query">Optional custom query filter (e.g. release group or title).</param>
    /// <param name="format">Optional target format override (srt, vtt, ass, ssa, sub, ttml).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of available virtual subtitle tracks.</returns>
    [HttpGet("Search/{itemId:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<VirtualSubtitleTrack>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<VirtualSubtitleTrack>>> SearchSubtitles(
        [FromRoute] Guid itemId,
        [FromQuery] string? language = "eng",
        [FromQuery] string? query = null,
        [FromQuery] string? format = null,
        CancellationToken cancellationToken = default)
    {
        var results = await _subtitleService
            .SearchSubtitlesAsync(itemId, string.IsNullOrWhiteSpace(language) ? "eng" : language, query, format, cancellationToken)
            .ConfigureAwait(false);

        return Ok(results);
    }

    /// <summary>
    /// Streams and converts a virtual subtitle track by its virtual stream index on-the-fly.
    /// Used by universal native players (Roku, Android TV, Swiftfin, Kodi, Web, Desktop, Mobile).
    /// </summary>
    /// <param name="itemId">The Jellyfin media item identifier.</param>
    /// <param name="virtualIndex">The virtual stream index injected into PlaybackInfo.</param>
    /// <param name="format">The requested subtitle format extension (srt, vtt, ass, ssa, sub, ttml).</param>
    /// <param name="offsetMs">Optional timing offset in milliseconds.</param>
    /// <param name="persist">Whether to also save the subtitle to the library.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The converted subtitle file stream.</returns>
    [HttpGet("Stream/{itemId:guid}/{virtualIndex:int}/Stream.{format}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> StreamVirtualSubtitleByIndex(
        [FromRoute] Guid itemId,
        [FromRoute] int virtualIndex,
        [FromRoute] string format,
        [FromQuery] int offsetMs = 0,
        [FromQuery] bool persist = true,
        CancellationToken cancellationToken = default)
    {
        var result = await _subtitleService
            .GetAndConvertSubtitleStreamAsync(itemId, virtualIndex, null, format, offsetMs, persist, cancellationToken)
            .ConfigureAwait(false);

        if (result is null || result.Value.Content.Length == 0)
        {
            return NotFound();
        }

        Response.Headers["Access-Control-Allow-Origin"] = "*";
        return File(result.Value.Content, result.Value.ContentType, $"subtitle.{format.TrimStart('.').ToLowerInvariant()}");
    }

    /// <summary>
    /// Streams and converts a remote subtitle track by its remote provider identifier on-the-fly.
    /// </summary>
    /// <param name="itemId">The Jellyfin media item identifier.</param>
    /// <param name="remoteSubtitleId">The remote subtitle provider identifier.</param>
    /// <param name="format">The requested subtitle format extension (srt, vtt, ass, ssa, sub, ttml).</param>
    /// <param name="offsetMs">Optional timing offset in milliseconds.</param>
    /// <param name="persist">Whether to also save the subtitle to the library.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The converted subtitle file stream.</returns>
    [HttpGet("StreamById/{itemId:guid}/{remoteSubtitleId}/Stream.{format}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> StreamVirtualSubtitleById(
        [FromRoute] Guid itemId,
        [FromRoute] string remoteSubtitleId,
        [FromRoute] string format,
        [FromQuery] int offsetMs = 0,
        [FromQuery] bool persist = true,
        CancellationToken cancellationToken = default)
    {
        var result = await _subtitleService
            .GetAndConvertSubtitleStreamAsync(itemId, null, remoteSubtitleId, format, offsetMs, persist, cancellationToken)
            .ConfigureAwait(false);

        if (result is null || result.Value.Content.Length == 0)
        {
            return NotFound();
        }

        Response.Headers["Access-Control-Allow-Origin"] = "*";
        return File(result.Value.Content, result.Value.ContentType, $"subtitle.{format.TrimStart('.').ToLowerInvariant()}");
    }

    /// <summary>
    /// Downloads, converts, and applies a remote subtitle to an active playback session and/or media library.
    /// </summary>
    /// <param name="itemId">The Jellyfin media item identifier.</param>
    /// <param name="request">The download and conversion parameters.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Details of the downloaded and converted subtitle stream.</returns>
    [HttpPost("Download/{itemId:guid}")]
    [ProducesResponseType(typeof(SubmarineDownloadResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SubmarineDownloadResult>> DownloadSubtitle(
        [FromRoute] Guid itemId,
        [FromBody] SubmarineDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RemoteSubtitleId))
        {
            return BadRequest("RemoteSubtitleId is required.");
        }

        var result = await _subtitleService
            .DownloadAndApplySubtitleAsync(itemId, request, cancellationToken)
            .ConfigureAwait(false);

        return Ok(result);
    }

    /// <summary>
    /// Converts raw subtitle text between supported formats (SRT, VTT, ASS, SSA, SUB, TTML) with optional time shifting.
    /// </summary>
    /// <param name="request">The conversion request payload.</param>
    /// <returns>The converted subtitle content and MIME type.</returns>
    [HttpPost("Convert")]
    [ProducesResponseType(typeof(SubmarineConvertResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<SubmarineConvertResponse> ConvertSubtitle([FromBody] SubmarineConvertRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest("Subtitle content is required.");
        }

        var converted = _formatConverter.Convert(
            request.Content,
            request.SourceFormat,
            request.TargetFormat,
            request.OffsetMs);

        var contentType = _formatConverter.GetContentType(request.TargetFormat);

        return Ok(new SubmarineConvertResponse
        {
            Content = converted,
            TargetFormat = request.TargetFormat.TrimStart('.').ToLowerInvariant(),
            ContentType = contentType
        });
    }

    /// <summary>
    /// Serves the embedded Submarine client-side player JavaScript bundle.
    /// </summary>
    /// <returns>The JavaScript file stream.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetClientScript()
    {
        var assembly = typeof(Plugin).Assembly;
        var resourceStream = assembly.GetManifestResourceStream("Jellyfin.Plugin.Submarine.Web.submarine-player.js");
        if (resourceStream is null)
        {
            _logger.LogWarning("Embedded resource Jellyfin.Plugin.Submarine.Web.submarine-player.js not found.");
            return NotFound();
        }

        return File(resourceStream, "application/javascript; charset=utf-8");
    }

    /// <summary>
    /// Serves the embedded Submarine client-side player stylesheet.
    /// </summary>
    /// <returns>The CSS file stream.</returns>
    [HttpGet("ClientStyles")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetClientStyles()
    {
        var assembly = typeof(Plugin).Assembly;
        var resourceStream = assembly.GetManifestResourceStream("Jellyfin.Plugin.Submarine.Web.submarine-player.css");
        if (resourceStream is null)
        {
            _logger.LogWarning("Embedded resource Jellyfin.Plugin.Submarine.Web.submarine-player.css not found.");
            return NotFound();
        }

        return File(resourceStream, "text/css; charset=utf-8");
    }
}

/// <summary>
/// Response payload for on-demand subtitle format conversion.
/// </summary>
public sealed class SubmarineConvertResponse
{
    /// <summary>
    /// Gets or sets the converted subtitle text content.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the normalized target subtitle format.
    /// </summary>
    public string TargetFormat { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the MIME content type for the converted subtitle.
    /// </summary>
    public string ContentType { get; set; } = string.Empty;
}

