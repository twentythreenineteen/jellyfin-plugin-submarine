using System.Collections.Generic;
using Jellyfin.Plugin.Submarine.Models;

namespace Jellyfin.Plugin.Submarine.Services;

/// <summary>
/// Provides parsing, format detection, timestamp shifting, and serialization across subtitle formats
/// (SRT, WebVTT, ASS, SSA, MicroDVD SUB, and TTML/DFXP).
/// </summary>
public interface ISubtitleFormatConverter
{
    /// <summary>
    /// Detects the subtitle format from raw subtitle content.
    /// </summary>
    /// <param name="content">The raw subtitle text.</param>
    /// <returns>The normalized subtitle format identifier (srt, vtt, ass, ssa, sub, ttml).</returns>
    string DetectFormat(string content);

    /// <summary>
    /// Gets the HTTP MIME content type for the specified subtitle format.
    /// </summary>
    /// <param name="format">The target subtitle format.</param>
    /// <returns>The MIME content type string.</returns>
    string GetContentType(string format);

    /// <summary>
    /// Parses raw subtitle text into a list of timed <see cref="SubtitleCue"/> instances.
    /// </summary>
    /// <param name="content">The raw subtitle text.</param>
    /// <param name="sourceFormatHint">Optional format hint; if null or empty, format is auto-detected.</param>
    /// <param name="fps">Frame rate used for frame-based formats such as MicroDVD (.sub).</param>
    /// <returns>A read-only list of parsed subtitle cues.</returns>
    IReadOnlyList<SubtitleCue> Parse(string content, string? sourceFormatHint = null, double fps = 23.976);

    /// <summary>
    /// Serializes a sequence of <see cref="SubtitleCue"/> objects into the specified target format.
    /// </summary>
    /// <param name="cues">The subtitle cues to serialize.</param>
    /// <param name="targetFormat">The desired output format (srt, vtt, ass, ssa, sub, ttml).</param>
    /// <param name="fps">Frame rate used for frame-based formats such as MicroDVD (.sub).</param>
    /// <returns>The formatted subtitle document string.</returns>
    string Serialize(IEnumerable<SubtitleCue> cues, string targetFormat, double fps = 23.976);

    /// <summary>
    /// Converts raw subtitle text from a source format (or auto-detected format) into a target format,
    /// applying an optional millisecond timestamp offset.
    /// </summary>
    /// <param name="content">The raw source subtitle text.</param>
    /// <param name="sourceFormat">Optional source format hint (auto-detected if null or empty).</param>
    /// <param name="targetFormat">The target subtitle format.</param>
    /// <param name="offsetMs">Timing offset in milliseconds (positive delays, negative hastens).</param>
    /// <param name="fps">Frame rate used for frame-based formats.</param>
    /// <returns>The converted subtitle text.</returns>
    string Convert(string content, string? sourceFormat, string targetFormat, int offsetMs = 0, double fps = 23.976);
}
