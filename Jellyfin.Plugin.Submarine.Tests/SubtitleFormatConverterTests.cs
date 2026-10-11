using System;
using Jellyfin.Plugin.Submarine.Models;
using Jellyfin.Plugin.Submarine.Services;
using Xunit;

namespace Jellyfin.Plugin.Submarine.Tests;

/// <summary>
/// Comprehensive unit tests for <see cref="SubtitleFormatConverter"/> covering format detection,
/// multi-format conversion (SRT, VTT, ASS, SSA, MicroDVD SUB, TTML/DFXP), tag conversion, and offset math.
/// </summary>
public class SubtitleFormatConverterTests
{
    private readonly SubtitleFormatConverter _converter = new();

    private const string SampleSrt = """
        1
        00:00:01,500 --> 00:00:04,000
        Hello world!
        Welcome to <i>Submarine</i>.

        2
        00:00:05,250 --> 00:00:08,750
        On-the-fly subtitle conversion.
        """;

    [Theory]
    [InlineData("1\n00:00:01,000 --> 00:00:03,000\nHello SRT", SubtitleFormat.Srt)]
    [InlineData("WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nHello VTT", SubtitleFormat.Vtt)]
    [InlineData("[Script Info]\nScriptType: v4.00+\n[V4+ Styles]\n[Events]\nDialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,Hello ASS", SubtitleFormat.Ass)]
    [InlineData("[Script Info]\nScriptType: v4.00\n[V4 Styles]\n[Events]\nDialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,Hello SSA", SubtitleFormat.Ssa)]
    [InlineData("{24}{72}Hello MicroDVD|Second line", SubtitleFormat.Sub)]
    [InlineData("<?xml version=\"1.0\"?>\n<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"00:00:01.000\" end=\"00:00:03.000\">Hello TTML</p></div></body></tt>", SubtitleFormat.Ttml)]
    public void DetectFormat_AutoDetectsAllSupportedFormats(string rawContent, string expectedFormat)
    {
        var detected = _converter.DetectFormat(rawContent);
        Assert.Equal(expectedFormat, detected);
    }

    [Fact]
    public void Convert_SrtToVtt_ProducesValidWebVttWithDotTimestamps()
    {
        var vtt = _converter.Convert(SampleSrt, SubtitleFormat.Srt, SubtitleFormat.Vtt);

        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("00:00:01.500 --> 00:00:04.000", vtt);
        Assert.Contains("Welcome to <i>Submarine</i>.", vtt);
        Assert.Contains("00:00:05.250 --> 00:00:08.750", vtt);

        var roundTripSrt = _converter.Convert(vtt, SubtitleFormat.Vtt, SubtitleFormat.Srt);
        Assert.Contains("00:00:01,500 --> 00:00:04,000", roundTripSrt);
        Assert.Contains("On-the-fly subtitle conversion.", roundTripSrt);
    }

    [Fact]
    public void Convert_SrtToAssAndSsa_ProducesValidScriptAndConvertsTagsAndNewlines()
    {
        var ass = _converter.Convert(SampleSrt, SubtitleFormat.Srt, SubtitleFormat.Ass);

        Assert.Contains("[Script Info]", ass);
        Assert.Contains("[V4+ Styles]", ass);
        Assert.Contains("Dialogue: 0,0:00:01.50,0:00:04.00,Default,,0,0,0,,Hello world!\\NWelcome to {\\i1}Submarine{\\i0}.", ass);

        var ssa = _converter.Convert(SampleSrt, SubtitleFormat.Srt, SubtitleFormat.Ssa);
        Assert.Contains("ScriptType: v4.00", ssa);
        Assert.Contains("[V4 Styles]", ssa);
        Assert.Contains("Dialogue: 0,0:00:05.25,0:00:08.75,Default,,0,0,0,,On-the-fly subtitle conversion.", ssa);
    }

    [Fact]
    public void Convert_AssToSrt_ConvertsAssNewlinesAndItalicTagsToStandardSrt()
    {
        const string assInput = """
            [Script Info]
            Title: Test ASS
            ScriptType: v4.00+

            [V4+ Styles]
            Format: Name, Fontname, Fontsize
            Style: Default,Arial,48

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:02.00,0:00:05.50,Default,Narrator,0,0,0,,{\pos(400,500)}{\i1}Captain's log{\i0}\NAll systems nominal.
            """;

        var srt = _converter.Convert(assInput, SubtitleFormat.Ass, SubtitleFormat.Srt);

        Assert.Contains("00:00:02,000 --> 00:00:05,500", srt);
        Assert.Contains("<i>Captain's log</i>\nAll systems nominal.", srt);
        Assert.DoesNotContain(@"{\pos", srt);
    }

    [Fact]
    public void Convert_SrtToMicroDvdSubAndBack_PreservesCuesAndLines()
    {
        const double fps = 25.0;
        var sub = _converter.Convert(SampleSrt, SubtitleFormat.Srt, SubtitleFormat.Sub, offsetMs: 0, fps: fps);

        // 1.5s * 25fps = 38, 4.0s * 25fps = 100
        Assert.Contains("{38}{100}{y:i}Hello world!|Welcome to Submarine.", sub);

        var backToSrt = _converter.Convert(sub, SubtitleFormat.Sub, SubtitleFormat.Srt, offsetMs: 0, fps: fps);
        var cues = _converter.Parse(backToSrt, SubtitleFormat.Srt);
        Assert.Equal(2, cues.Count);
        Assert.Contains("Hello world!", cues[0].Text);
        Assert.Contains("Welcome to Submarine.", cues[0].Text);
        Assert.Contains("On-the-fly subtitle conversion.", cues[1].Text);
    }

    [Fact]
    public void Convert_SrtToTtmlAndTtmlToVtt_PreservesTimingAndLineBreaks()
    {
        var ttml = _converter.Convert(SampleSrt, SubtitleFormat.Srt, SubtitleFormat.Ttml);

        Assert.Contains("<tt xmlns=\"http://www.w3.org/ns/ttml\"", ttml);
        Assert.Contains("<p begin=\"00:00:01.500\" end=\"00:00:04.000\">Hello world!<br/>Welcome to Submarine.</p>", ttml);

        var vtt = _converter.Convert(ttml, SubtitleFormat.Ttml, SubtitleFormat.Vtt);
        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("00:00:01.500 --> 00:00:04.000", vtt);
        Assert.Contains("Hello world!\nWelcome to Submarine.", vtt);
        Assert.Contains("00:00:05.250 --> 00:00:08.750", vtt);
    }

    [Fact]
    public void Convert_WithPositiveAndNegativeOffsets_ShiftsAndZeroClampsTimestamps()
    {
        // Positive offset: +1500ms shifts 00:00:01,500 -> 00:00:03,000 and 00:00:04,000 -> 00:00:05,500
        var delayedSrt = _converter.Convert(SampleSrt, SubtitleFormat.Srt, SubtitleFormat.Srt, offsetMs: 1500);
        Assert.Contains("00:00:03,000 --> 00:00:05,500", delayedSrt);
        Assert.Contains("00:00:06,750 --> 00:00:10,250", delayedSrt);

        // Negative offset: -2000ms clamps 00:00:01,500 to 00:00:00,000 (never negative) and 00:00:04,000 -> 00:00:02,000
        var hastenedSrt = _converter.Convert(SampleSrt, SubtitleFormat.Srt, SubtitleFormat.Srt, offsetMs: -2000);
        Assert.Contains("00:00:00,000 --> 00:00:02,000", hastenedSrt);
        Assert.Contains("00:00:03,250 --> 00:00:06,750", hastenedSrt);
    }
}
