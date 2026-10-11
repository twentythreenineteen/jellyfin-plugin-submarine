using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Submarine.Models;

/// <summary>
/// Supported subtitle formats and format normalization/MIME-type helpers.
/// </summary>
public static class SubtitleFormat
{
    /// <summary>
    /// SubRip (.srt) subtitle format.
    /// </summary>
    public const string Srt = "srt";

    /// <summary>
    /// WebVTT (.vtt) subtitle format.
    /// </summary>
    public const string Vtt = "vtt";

    /// <summary>
    /// Advanced SubStation Alpha (.ass) subtitle format.
    /// </summary>
    public const string Ass = "ass";

    /// <summary>
    /// SubStation Alpha (.ssa) subtitle format.
    /// </summary>
    public const string Ssa = "ssa";

    /// <summary>
    /// MicroDVD / SubViewer (.sub) subtitle format.
    /// </summary>
    public const string Sub = "sub";

    /// <summary>
    /// Timed Text Markup Language (.ttml) subtitle format.
    /// </summary>
    public const string Ttml = "ttml";

    /// <summary>
    /// Distribution Format Exchange Profile (.dfxp) alias for TTML.
    /// </summary>
    public const string Dfxp = "dfxp";

    /// <summary>
    /// All canonical supported formats.
    /// </summary>
    public static IReadOnlyList<string> SupportedFormats { get; } = new[]
    {
        Srt,
        Vtt,
        Ass,
        Ssa,
        Sub,
        Ttml
    };

    /// <summary>
    /// Normalizes a format string or file extension to a canonical lowercase format identifier.
    /// </summary>
    /// <param name="format">The raw format or extension string.</param>
    /// <returns>The normalized format identifier (defaults to "srt").</returns>
    public static string NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return Srt;
        }

        var cleaned = format.Trim().TrimStart('.').ToLowerInvariant();
        return cleaned switch
        {
            "srt" or "subrip" => Srt,
            "vtt" or "webvtt" => Vtt,
            "ass" => Ass,
            "ssa" => Ssa,
            "sub" or "microdvd" or "subviewer" => Sub,
            "ttml" or "dfxp" or "xml" => Ttml,
            _ => Srt
        };
    }

    /// <summary>
    /// Gets the HTTP MIME content type for the specified subtitle format.
    /// </summary>
    /// <param name="format">The subtitle format.</param>
    /// <returns>The MIME type string.</returns>
    public static string GetMimeType(string? format)
    {
        return NormalizeFormat(format) switch
        {
            Srt => "application/x-subrip; charset=utf-8",
            Vtt => "text/vtt; charset=utf-8",
            Ass or Ssa => "text/x-ssa; charset=utf-8",
            Sub => "text/plain; charset=utf-8",
            Ttml => "application/ttml+xml; charset=utf-8",
            _ => "text/plain; charset=utf-8"
        };
    }

    /// <summary>
    /// Gets the Jellyfin codec identifier corresponding to a subtitle format.
    /// </summary>
    /// <param name="format">The subtitle format.</param>
    /// <returns>The Jellyfin subtitle codec name.</returns>
    public static string GetJellyfinCodec(string? format)
    {
        return NormalizeFormat(format) switch
        {
            Srt => "srt",
            Vtt => "vtt",
            Ass => "ass",
            Ssa => "ssa",
            Sub => "sub",
            Ttml => "ttml",
            _ => "srt"
        };
    }
}
