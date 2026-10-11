using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Plugin.Submarine.Models;

namespace Jellyfin.Plugin.Submarine.Services;

/// <summary>
/// Zero-dependency, high-reliability converter supporting SRT, WebVTT, ASS/SSA, MicroDVD/SubViewer SUB, and TTML/DFXP.
/// </summary>
public sealed class SubtitleFormatConverter : ISubtitleFormatConverter
{
    private static readonly Regex SrtVttTimingRegex = new(
        @"^\s*(?:(?<sh>\d{1,2}):)?(?<sm>\d{1,2}):(?<ss>\d{1,2})[,\.](?<sms>\d{1,3})\s*-->\s*(?:(?<eh>\d{1,2}):)?(?<em>\d{1,2}):(?<es>\d{1,2})[,\.](?<ems>\d{1,3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MicroDvdLineRegex = new(
        @"^\s*\{(?<start>\d+)\}\{(?<end>\d+)\}(?<text>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SubViewerTimingRegex = new(
        @"^\s*(?<sh>\d{1,2}):(?<sm>\d{2}):(?<ss>\d{2})[,\.](?<sms>\d{1,3})\s*,\s*(?<eh>\d{1,2}):(?<em>\d{2}):(?<es>\d{2})[,\.](?<ems>\d{1,3})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AssOverrideTagRegex = new(
        @"\{[^}]*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HtmlTagRegex = new(
        @"<[^>]+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public string GetContentType(string format) => SubtitleFormat.GetMimeType(format);

    /// <inheritdoc />
    public string DetectFormat(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return SubtitleFormat.Srt;
        }

        var trimmed = StripBom(content).TrimStart();

        if (trimmed.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
        {
            return SubtitleFormat.Vtt;
        }

        if (trimmed.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("[V4+ Styles]", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("\nDialogue:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmed.Contains("[V4 Styles]", StringComparison.OrdinalIgnoreCase)
                && !trimmed.Contains("[V4+ Styles]", StringComparison.OrdinalIgnoreCase))
            {
                return SubtitleFormat.Ssa;
            }

            return SubtitleFormat.Ass;
        }

        if (trimmed.Contains("<tt", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("http://www.w3.org/ns/ttml", StringComparison.OrdinalIgnoreCase))
        {
            return SubtitleFormat.Ttml;
        }

        var firstLines = trimmed.Split('\n', 10, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in firstLines)
        {
            var line = rawLine.Trim();
            if (MicroDvdLineRegex.IsMatch(line) || SubViewerTimingRegex.IsMatch(line))
            {
                return SubtitleFormat.Sub;
            }
        }

        return SubtitleFormat.Srt;
    }

    /// <inheritdoc />
    public IReadOnlyList<SubtitleCue> Parse(string content, string? sourceFormatHint = null, double fps = 23.976)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<SubtitleCue>();
        }

        if (fps <= 0)
        {
            fps = 23.976;
        }

        var normalizedInput = NormalizeLineEndings(StripBom(content));
        var detectedFormat = DetectFormat(normalizedInput);
        var format = string.IsNullOrWhiteSpace(sourceFormatHint) || string.Equals(sourceFormatHint, "auto", StringComparison.OrdinalIgnoreCase)
            ? detectedFormat
            : SubtitleFormat.NormalizeFormat(sourceFormatHint);

        var cues = ParseByFormat(normalizedInput, format, fps);
        if (cues.Count == 0 && !string.Equals(format, detectedFormat, StringComparison.OrdinalIgnoreCase))
        {
            cues = ParseByFormat(normalizedInput, detectedFormat, fps);
        }

        return cues;
    }

    /// <inheritdoc />
    public string Serialize(IEnumerable<SubtitleCue> cues, string targetFormat, double fps = 23.976)
    {
        ArgumentNullException.ThrowIfNull(cues);

        if (fps <= 0)
        {
            fps = 23.976;
        }

        var cueList = cues.OrderBy(c => c.StartTime).ThenBy(c => c.Index).ToList();
        var normalizedTarget = SubtitleFormat.NormalizeFormat(targetFormat);

        return normalizedTarget switch
        {
            SubtitleFormat.Srt => SerializeSrt(cueList),
            SubtitleFormat.Vtt => SerializeVtt(cueList),
            SubtitleFormat.Ass => SerializeAssOrSsa(cueList, isSsa: false),
            SubtitleFormat.Ssa => SerializeAssOrSsa(cueList, isSsa: true),
            SubtitleFormat.Sub => SerializeMicroDvd(cueList, fps),
            SubtitleFormat.Ttml => SerializeTtml(cueList),
            _ => SerializeSrt(cueList)
        };
    }

    /// <inheritdoc />
    public string Convert(string content, string? sourceFormat, string targetFormat, int offsetMs = 0, double fps = 23.976)
    {
        var cues = Parse(content, sourceFormat, fps);
        if (offsetMs != 0 && cues.Count > 0)
        {
            var delta = TimeSpan.FromMilliseconds(offsetMs);
            var shifted = new List<SubtitleCue>(cues.Count);
            foreach (var cue in cues)
            {
                var newStart = cue.StartTime + delta;
                if (newStart < TimeSpan.Zero)
                {
                    newStart = TimeSpan.Zero;
                }

                var newEnd = cue.EndTime + delta;
                if (newEnd < newStart)
                {
                    newEnd = newStart + TimeSpan.FromMilliseconds(500);
                }

                shifted.Add(new SubtitleCue
                {
                    Index = cue.Index,
                    StartTime = newStart,
                    EndTime = newEnd,
                    Text = cue.Text,
                    SpeakerOrStyle = cue.SpeakerOrStyle
                });
            }

            cues = shifted;
        }

        return Serialize(cues, targetFormat, fps);
    }

    private static List<SubtitleCue> ParseByFormat(string content, string format, double fps)
    {
        return format switch
        {
            SubtitleFormat.Srt or SubtitleFormat.Vtt => ParseSrtOrVtt(content),
            SubtitleFormat.Ass or SubtitleFormat.Ssa => ParseAssOrSsa(content),
            SubtitleFormat.Sub => ParseSub(content, fps),
            SubtitleFormat.Ttml => ParseTtml(content),
            _ => ParseSrtOrVtt(content)
        };
    }

    private static List<SubtitleCue> ParseSrtOrVtt(string content)
    {
        var lines = content.Split('\n');
        var cues = new List<SubtitleCue>();
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i].Trim();

            if (string.IsNullOrEmpty(line)
                || line.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            if (line.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("REGION", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                {
                    i++;
                }

                continue;
            }

            var timingMatch = SrtVttTimingRegex.Match(line);
            if (!timingMatch.Success && i + 1 < lines.Length)
            {
                var nextTimingMatch = SrtVttTimingRegex.Match(lines[i + 1].Trim());
                if (nextTimingMatch.Success)
                {
                    timingMatch = nextTimingMatch;
                    i++;
                }
            }

            if (!timingMatch.Success)
            {
                i++;
                continue;
            }

            var startTime = ParseRegexTimeSpan(timingMatch, "sh", "sm", "ss", "sms");
            var endTime = ParseRegexTimeSpan(timingMatch, "eh", "em", "es", "ems");

            i++;
            var textLines = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                textLines.Add(lines[i].TrimEnd());
                i++;
            }

            var text = string.Join("\n", textLines).Trim();
            if (!string.IsNullOrEmpty(text))
            {
                cues.Add(new SubtitleCue
                {
                    Index = cues.Count + 1,
                    StartTime = startTime,
                    EndTime = endTime >= startTime ? endTime : startTime + TimeSpan.FromSeconds(1),
                    Text = text
                });
            }
        }

        return cues;
    }

    private static List<SubtitleCue> ParseAssOrSsa(string content)
    {
        var lines = content.Split('\n');
        var cues = new List<SubtitleCue>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = line["Dialogue:".Length..].TrimStart();
            // Standard ASS format has 10 fields: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            var parts = payload.Split(',', 10);
            if (parts.Length < 10)
            {
                continue;
            }

            if (!TryParseAssTimestamp(parts[1].Trim(), out var startTime)
                || !TryParseAssTimestamp(parts[2].Trim(), out var endTime))
            {
                continue;
            }

            var style = parts[3].Trim();
            var speaker = parts[4].Trim();
            var rawText = parts[9];

            var cleanedText = ConvertAssTextToStandard(rawText);
            if (string.IsNullOrWhiteSpace(cleanedText))
            {
                continue;
            }

            cues.Add(new SubtitleCue
            {
                Index = cues.Count + 1,
                StartTime = startTime,
                EndTime = endTime >= startTime ? endTime : startTime + TimeSpan.FromSeconds(1),
                Text = cleanedText,
                SpeakerOrStyle = !string.IsNullOrEmpty(speaker) ? speaker : (!string.IsNullOrEmpty(style) ? style : null)
            });
        }

        return cues;
    }

    private static List<SubtitleCue> ParseSub(string content, double fps)
    {
        var lines = content.Split('\n');
        var cues = new List<SubtitleCue>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            var microMatch = MicroDvdLineRegex.Match(line);
            if (microMatch.Success)
            {
                if (!long.TryParse(microMatch.Groups["start"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var startFrame)
                    || !long.TryParse(microMatch.Groups["end"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var endFrame))
                {
                    continue;
                }

                var rawText = microMatch.Groups["text"].Value;
                if (startFrame <= 1 && endFrame <= 1 && double.TryParse(rawText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var embeddedFps) && embeddedFps > 1)
                {
                    fps = embeddedFps;
                    continue;
                }

                var hasItalic = rawText.Contains("{y:i}", StringComparison.OrdinalIgnoreCase);
                var cleaned = AssOverrideTagRegex.Replace(rawText, string.Empty).Replace("|", "\n", StringComparison.Ordinal).Trim();
                if (string.IsNullOrEmpty(cleaned))
                {
                    continue;
                }

                if (hasItalic)
                {
                    cleaned = $"<i>{cleaned}</i>";
                }

                cues.Add(new SubtitleCue
                {
                    Index = cues.Count + 1,
                    StartTime = TimeSpan.FromSeconds(startFrame / fps),
                    EndTime = TimeSpan.FromSeconds(Math.Max(endFrame, startFrame + 1) / fps),
                    Text = cleaned
                });
                continue;
            }

            var subViewerMatch = SubViewerTimingRegex.Match(line);
            if (subViewerMatch.Success && i + 1 < lines.Length)
            {
                var startTime = ParseRegexTimeSpan(subViewerMatch, "sh", "sm", "ss", "sms");
                var endTime = ParseRegexTimeSpan(subViewerMatch, "eh", "em", "es", "ems");
                i++;
                var textLines = new List<string>();
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                {
                    textLines.Add(lines[i].Trim().Replace("[br]", "\n", StringComparison.OrdinalIgnoreCase).Replace("|", "\n", StringComparison.Ordinal));
                    i++;
                }

                var text = string.Join("\n", textLines).Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    cues.Add(new SubtitleCue
                    {
                        Index = cues.Count + 1,
                        StartTime = startTime,
                        EndTime = endTime >= startTime ? endTime : startTime + TimeSpan.FromSeconds(1),
                        Text = text
                    });
                }
            }
        }

        return cues;
    }

    private static List<SubtitleCue> ParseTtml(string content)
    {
        var cues = new List<SubtitleCue>();
        try
        {
            var doc = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
            var pElements = doc.Descendants().Where(e => string.Equals(e.Name.LocalName, "p", StringComparison.OrdinalIgnoreCase));

            foreach (var p in pElements)
            {
                var beginAttr = p.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, "begin", StringComparison.OrdinalIgnoreCase))?.Value;
                var endAttr = p.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, "end", StringComparison.OrdinalIgnoreCase))?.Value;
                var durAttr = p.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, "dur", StringComparison.OrdinalIgnoreCase))?.Value;

                if (string.IsNullOrWhiteSpace(beginAttr) || !TryParseTtmlTimestamp(beginAttr, out var startTime))
                {
                    continue;
                }

                TimeSpan endTime;
                if (!string.IsNullOrWhiteSpace(endAttr) && TryParseTtmlTimestamp(endAttr, out var parsedEnd))
                {
                    endTime = parsedEnd;
                }
                else if (!string.IsNullOrWhiteSpace(durAttr) && TryParseTtmlTimestamp(durAttr, out var parsedDur))
                {
                    endTime = startTime + parsedDur;
                }
                else
                {
                    endTime = startTime + TimeSpan.FromSeconds(2);
                }

                var text = ExtractTtmlParagraphText(p).Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    cues.Add(new SubtitleCue
                    {
                        Index = cues.Count + 1,
                        StartTime = startTime,
                        EndTime = endTime >= startTime ? endTime : startTime + TimeSpan.FromSeconds(1),
                        Text = text
                    });
                }
            }
        }
        catch
        {
            // Fallback regex for slightly malformed TTML/DFXP XML
            var pRegex = new Regex(@"<p\b[^>]*\bbegin\s*=\s*[""'](?<begin>[^""']+)[""'][^>]*\bend\s*=\s*[""'](?<end>[^""']+)[""'][^>]*>(?<body>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match match in pRegex.Matches(content))
            {
                if (TryParseTtmlTimestamp(match.Groups["begin"].Value, out var startTime)
                    && TryParseTtmlTimestamp(match.Groups["end"].Value, out var endTime))
                {
                    var body = Regex.Replace(match.Groups["body"].Value, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
                    body = WebUtility.HtmlDecode(HtmlTagRegex.Replace(body, string.Empty)).Trim();
                    if (!string.IsNullOrEmpty(body))
                    {
                        cues.Add(new SubtitleCue
                        {
                            Index = cues.Count + 1,
                            StartTime = startTime,
                            EndTime = endTime,
                            Text = body
                        });
                    }
                }
            }
        }

        return cues;
    }

    private static string ExtractTtmlParagraphText(XElement element)
    {
        var sb = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            if (node is XText textNode)
            {
                sb.Append(textNode.Value);
            }
            else if (node is XElement childEl)
            {
                var localName = childEl.Name.LocalName;
                if (string.Equals(localName, "br", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append('\n');
                }
                else if (string.Equals(localName, "i", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("<i>").Append(ExtractTtmlParagraphText(childEl)).Append("</i>");
                }
                else if (string.Equals(localName, "b", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("<b>").Append(ExtractTtmlParagraphText(childEl)).Append("</b>");
                }
                else
                {
                    var fontStyle = childEl.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, "fontStyle", StringComparison.OrdinalIgnoreCase))?.Value;
                    var inner = ExtractTtmlParagraphText(childEl);
                    if (string.Equals(fontStyle, "italic", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append("<i>").Append(inner).Append("</i>");
                    }
                    else
                    {
                        sb.Append(inner);
                    }
                }
            }
        }

        // Collapse excessive spaces around newlines while keeping intentional line breaks
        var lines = NormalizeLineEndings(sb.ToString()).Split('\n').Select(l => l.Trim());
        return string.Join("\n", lines);
    }

    private static string SerializeSrt(List<SubtitleCue> cues)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(FormatSrtTime(cue.StartTime))
              .Append(" --> ")
              .Append(FormatSrtTime(cue.EndTime))
              .Append('\n');
            sb.Append(NormalizeLineEndings(cue.Text).Trim()).Append("\n\n");
        }

        return sb.ToString();
    }

    private static string SerializeVtt(List<SubtitleCue> cues)
    {
        var sb = new StringBuilder();
        sb.Append("WEBVTT\n\n");
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(FormatVttTime(cue.StartTime))
              .Append(" --> ")
              .Append(FormatVttTime(cue.EndTime))
              .Append('\n');
            sb.Append(NormalizeLineEndings(cue.Text).Trim()).Append("\n\n");
        }

        return sb.ToString();
    }

    private static string SerializeAssOrSsa(List<SubtitleCue> cues, bool isSsa)
    {
        var sb = new StringBuilder();
        sb.Append("[Script Info]\n");
        sb.Append("; Script generated by Submarine Jellyfin Plugin\n");
        sb.Append(isSsa ? "ScriptType: v4.00\n" : "ScriptType: v4.00+\n");
        sb.Append("PlayResX: 1920\n");
        sb.Append("PlayResY: 1080\n");
        sb.Append("WrapStyle: 0\n");
        sb.Append("ScaledBorderAndShadow: yes\n\n");

        if (isSsa)
        {
            sb.Append("[V4 Styles]\n");
            sb.Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, TertiaryColour, BackColour, Bold, Italic, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, AlphaLevel, Encoding\n");
            sb.Append("Style: Default,Arial,56,16777215,65535,0,0,0,0,1,2,2,2,20,20,30,0,1\n\n");
        }
        else
        {
            sb.Append("[V4+ Styles]\n");
            sb.Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n");
            sb.Append("Style: Default,Arial,56,&H00FFFFFF,&H0000FFFF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2.5,1.5,2,20,20,36,1\n\n");
        }

        sb.Append("[Events]\n");
        sb.Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");

        foreach (var cue in cues)
        {
            var assText = ConvertStandardTextToAss(cue.Text);
            sb.Append("Dialogue: 0,")
              .Append(FormatAssTime(cue.StartTime))
              .Append(',')
              .Append(FormatAssTime(cue.EndTime))
              .Append(",Default,,0,0,0,,")
              .Append(assText)
              .Append('\n');
        }

        return sb.ToString();
    }

    private static string SerializeMicroDvd(List<SubtitleCue> cues, double fps)
    {
        var sb = new StringBuilder();
        foreach (var cue in cues)
        {
            var startFrame = Math.Max(0, (long)Math.Round(cue.StartTime.TotalSeconds * fps));
            var endFrame = Math.Max(startFrame + 1, (long)Math.Round(cue.EndTime.TotalSeconds * fps));
            var hasItalic = cue.Text.Contains("<i>", StringComparison.OrdinalIgnoreCase);
            var cleanText = HtmlTagRegex.Replace(NormalizeLineEndings(cue.Text), string.Empty)
                .Replace("\n", "|", StringComparison.Ordinal)
                .Trim();

            sb.Append('{').Append(startFrame.ToString(CultureInfo.InvariantCulture)).Append('}')
              .Append('{').Append(endFrame.ToString(CultureInfo.InvariantCulture)).Append('}');
            if (hasItalic)
            {
                sb.Append("{y:i}");
            }

            sb.Append(cleanText).Append('\n');
        }

        return sb.ToString();
    }

    private static string SerializeTtml(List<SubtitleCue> cues)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:tts=\"http://www.w3.org/ns/ttml#styling\" xml:lang=\"en\">\n");
        sb.Append("  <body>\n");
        sb.Append("    <div>\n");

        foreach (var cue in cues)
        {
            var lines = NormalizeLineEndings(cue.Text)
                .Split('\n')
                .Select(l => WebUtility.HtmlEncode(HtmlTagRegex.Replace(l.Trim(), string.Empty)));
            var joined = string.Join("<br/>", lines);

            sb.Append("      <p begin=\"")
              .Append(FormatVttTime(cue.StartTime))
              .Append("\" end=\"")
              .Append(FormatVttTime(cue.EndTime))
              .Append("\">")
              .Append(joined)
              .Append("</p>\n");
        }

        sb.Append("    </div>\n");
        sb.Append("  </body>\n");
        sb.Append("</tt>\n");
        return sb.ToString();
    }

    private static string ConvertAssTextToStandard(string rawAss)
    {
        var s = rawAss
            .Replace(@"{\i1}", "<i>", StringComparison.OrdinalIgnoreCase)
            .Replace(@"{\i0}", "</i>", StringComparison.OrdinalIgnoreCase)
            .Replace(@"{\b1}", "<b>", StringComparison.OrdinalIgnoreCase)
            .Replace(@"{\b0}", "</b>", StringComparison.OrdinalIgnoreCase)
            .Replace(@"{\u1}", "<u>", StringComparison.OrdinalIgnoreCase)
            .Replace(@"{\u0}", "</u>", StringComparison.OrdinalIgnoreCase);

        s = AssOverrideTagRegex.Replace(s, string.Empty);
        s = s.Replace(@"\N", "\n", StringComparison.Ordinal)
             .Replace(@"\n", "\n", StringComparison.Ordinal)
             .Replace(@"\h", " ", StringComparison.Ordinal);

        return s.Trim();
    }

    private static string ConvertStandardTextToAss(string text)
    {
        var s = NormalizeLineEndings(text).Trim()
            .Replace("<i>", @"{\i1}", StringComparison.OrdinalIgnoreCase)
            .Replace("</i>", @"{\i0}", StringComparison.OrdinalIgnoreCase)
            .Replace("<b>", @"{\b1}", StringComparison.OrdinalIgnoreCase)
            .Replace("</b>", @"{\b0}", StringComparison.OrdinalIgnoreCase)
            .Replace("<u>", @"{\u1}", StringComparison.OrdinalIgnoreCase)
            .Replace("</u>", @"{\u0}", StringComparison.OrdinalIgnoreCase);

        s = HtmlTagRegex.Replace(s, string.Empty);
        return s.Replace("\n", @"\N", StringComparison.Ordinal);
    }

    private static TimeSpan ParseRegexTimeSpan(Match match, string hGroup, string mGroup, string sGroup, string msGroup)
    {
        var hours = match.Groups[hGroup].Success
            ? int.Parse(match.Groups[hGroup].Value, CultureInfo.InvariantCulture)
            : 0;
        var minutes = int.Parse(match.Groups[mGroup].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[sGroup].Value, CultureInfo.InvariantCulture);
        var msRaw = match.Groups[msGroup].Value.PadRight(3, '0')[..3];
        var milliseconds = int.Parse(msRaw, CultureInfo.InvariantCulture);

        return new TimeSpan(0, hours, minutes, seconds, milliseconds);
    }

    private static bool TryParseAssTimestamp(string input, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        var parts = input.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return false;
        }

        var secParts = parts[2].Split('.', ',');
        if (!int.TryParse(secParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var milliseconds = 0;
        if (secParts.Length > 1)
        {
            var frac = secParts[1].PadRight(3, '0')[..3];
            int.TryParse(frac, NumberStyles.Integer, CultureInfo.InvariantCulture, out milliseconds);
        }

        result = new TimeSpan(0, hours, minutes, seconds, milliseconds);
        return true;
    }

    private static bool TryParseTtmlTimestamp(string input, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        var s = input.Trim();

        if (s.EndsWith("ms", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(s[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var msVal))
        {
            result = TimeSpan.FromMilliseconds(msVal);
            return true;
        }

        if (s.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var secVal))
        {
            result = TimeSpan.FromSeconds(secVal);
            return true;
        }

        if (s.EndsWith("t", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(s[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tickVal))
        {
            result = TimeSpan.FromTicks(tickVal);
            return true;
        }

        return TryParseAssTimestamp(s, out result);
    }

    private static string FormatSrtTime(TimeSpan ts)
    {
        var totalHours = (int)ts.TotalHours;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:D2}:{1:D2}:{2:D2},{3:D3}",
            totalHours,
            ts.Minutes,
            ts.Seconds,
            ts.Milliseconds);
    }

    private static string FormatVttTime(TimeSpan ts)
    {
        var totalHours = (int)ts.TotalHours;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:D2}:{1:D2}:{2:D2}.{3:D3}",
            totalHours,
            ts.Minutes,
            ts.Seconds,
            ts.Milliseconds);
    }

    private static string FormatAssTime(TimeSpan ts)
    {
        var totalHours = (int)ts.TotalHours;
        var centiseconds = (int)Math.Round(ts.Milliseconds / 10.0);
        if (centiseconds >= 100)
        {
            centiseconds = 99;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}:{1:D2}:{2:D2}.{3:D2}",
            totalHours,
            ts.Minutes,
            ts.Seconds,
            centiseconds);
    }

    private static string StripBom(string input)
    {
        return input.Length > 0 && input[0] == '\uFEFF' ? input[1..] : input;
    }

    private static string NormalizeLineEndings(string input)
    {
        return input.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
}
