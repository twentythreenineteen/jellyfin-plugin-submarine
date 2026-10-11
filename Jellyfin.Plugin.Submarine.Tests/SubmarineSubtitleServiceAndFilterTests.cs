using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Submarine.Configuration;
using Jellyfin.Plugin.Submarine.Filters;
using Jellyfin.Plugin.Submarine.Models;
using Jellyfin.Plugin.Submarine.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Submarine.Tests;

/// <summary>
/// Unit and integration tests for <see cref="SubmarineSubtitleService"/> and <see cref="PlaybackInfoSubtitleFilter"/>.
/// </summary>
public class SubmarineSubtitleServiceAndFilterTests
{
    private const string SampleRemoteSrt = """
        1
        00:00:02,000 --> 00:00:05,000
        Submarine test subtitle line one.

        2
        00:00:06,000 --> 00:00:09,500
        <i>Second cue</i> with formatting.
        """;

    public SubmarineSubtitleServiceAndFilterTests()
    {
        var testConfig = new PluginConfiguration
        {
            EnableUniversalVirtualStreams = true,
            EnableWebPlayerModal = true,
            PreferredLanguages = "eng,spa",
            MaxVirtualStreamsPerLanguage = 2,
            DefaultTargetFormat = "srt",
            EnabledVirtualFormats = "srt,vtt,ass",
            PersistDownloadedSubtitlesToLibrary = true,
            IncludeHearingImpaired = true,
            SearchTimeoutSeconds = 5
        };

        _ = new Plugin(testConfig);
    }

    [Fact]
    public async Task SearchSubtitlesAsync_ReturnsVirtualSubtitleTracksWithDeterministicIndicesAndDeliveryUrls()
    {
        var itemId = Guid.NewGuid();
        var movie = new Movie { Id = itemId, Name = "Das Boot", Path = "/media/movies/Das.Boot.1981.mkv", ProductionYear = 1981 };

        var remoteResults = new[]
        {
            new RemoteSubtitleInfo
            {
                Id = "opensub-eng-101",
                Name = "Das.Boot.1981.1080p.BluRay.srt",
                ProviderName = "OpenSubtitles",
                ThreeLetterISOLanguageName = "eng",
                Format = "srt",
                IsHashMatch = true,
                CommunityRating = 9.5f,
                DownloadCount = 1200
            },
            new RemoteSubtitleInfo
            {
                Id = "subdl-eng-202",
                Name = "Das.Boot.1981.WEBRip.SDH.ass",
                ProviderName = "Subdl",
                ThreeLetterISOLanguageName = "eng",
                Format = "ass",
                IsHashMatch = false,
                Comment = "SDH Hearing Impaired",
                CommunityRating = 8.0f,
                DownloadCount = 450
            }
        };

        var subtitleManagerMock = CreateSubtitleManagerMock(remoteResults);
        var libraryManagerMock = new Mock<ILibraryManager>();
        libraryManagerMock.Setup(m => m.GetItemById(itemId)).Returns(movie);

        var converter = new SubtitleFormatConverter();
        var service = new SubmarineSubtitleService(
            subtitleManagerMock.Object,
            libraryManagerMock.Object,
            converter,
            NullLogger<SubmarineSubtitleService>.Instance);

        var tracks = await service.SearchSubtitlesAsync(itemId, "eng", targetFormat: "vtt");

        Assert.Equal(2, tracks.Count);

        var first = tracks[0];
        Assert.Equal("opensub-eng-101", first.RemoteSubtitleId);
        Assert.True(first.VirtualIndex >= SubmarineSubtitleService.VirtualIndexBase);
        Assert.Equal("vtt", first.TargetFormat);
        Assert.Contains("OpenSubtitles", first.DisplayTitle);
        Assert.Contains("★Match", first.DisplayTitle);
        Assert.Equal($"/Plugins/Submarine/Stream/{itemId}/{first.VirtualIndex}/Stream.vtt", first.DeliveryUrl);

        var second = tracks[1];
        Assert.Equal("subdl-eng-202", second.RemoteSubtitleId);
        Assert.True(second.VirtualIndex > first.VirtualIndex);
        Assert.Contains("[SDH]", second.DisplayTitle);

        // Searching again for the same item/remoteId/format returns the exact same deterministic VirtualIndex
        var repeatTracks = await service.SearchSubtitlesAsync(itemId, "eng", targetFormat: "vtt");
        Assert.Equal(first.VirtualIndex, repeatTracks[0].VirtualIndex);
        Assert.Equal(second.VirtualIndex, repeatTracks[1].VirtualIndex);
    }

    [Theory]
    [InlineData("Roku")]
    [InlineData("Android TV")]
    [InlineData("Swiftfin")]
    [InlineData("Kodi")]
    [InlineData("Jellyfin Web")]
    public async Task PlaybackInfoSubtitleFilter_InjectsExternalVirtualSubtitleStreamsForAllNativeClients(string clientName)
    {
        var itemId = Guid.NewGuid();
        var movie = new Movie { Id = itemId, Name = "The Hunt for Red October", Path = "/movies/RedOctober.mkv" };

        var remoteResults = new[]
        {
            new RemoteSubtitleInfo
            {
                Id = "remote-sub-1",
                Name = "Red.October.1990.srt",
                ProviderName = "OpenSubtitles",
                ThreeLetterISOLanguageName = "eng",
                Format = "srt",
                IsHashMatch = true
            }
        };

        var subtitleManagerMock = CreateSubtitleManagerMock(remoteResults);
        var libraryManagerMock = new Mock<ILibraryManager>();
        libraryManagerMock.Setup(m => m.GetItemById(itemId)).Returns(movie);

        var service = new SubmarineSubtitleService(
            subtitleManagerMock.Object,
            libraryManagerMock.Object,
            new SubtitleFormatConverter(),
            NullLogger<SubmarineSubtitleService>.Instance);

        var filter = new PlaybackInfoSubtitleFilter(service, NullLogger<PlaybackInfoSubtitleFilter>.Instance);

        var playbackInfo = new PlaybackInfoResponse
        {
            MediaSources = new List<MediaSourceInfo>
            {
                new MediaSourceInfo
                {
                    Id = itemId.ToString("N"),
                    MediaStreams = new List<MediaStream>
                    {
                        new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                        new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac" }
                    }
                }
            }
        };

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Emby-Client"] = clientName;

        var routeData = new RouteData();
        routeData.Values["itemId"] = itemId.ToString();

        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
        var objectResult = new ObjectResult(playbackInfo);
        var executingContext = new ResultExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            objectResult,
            controller: new object());

        var nextCalled = false;
        ResultExecutionDelegate next = () =>
        {
            nextCalled = true;
            var executedContext = new ResultExecutedContext(actionContext, new List<IFilterMetadata>(), objectResult, controller: new object());
            return Task.FromResult(executedContext);
        };

        await filter.OnResultExecutionAsync(executingContext, next);

        Assert.True(nextCalled);
        var streams = playbackInfo.MediaSources[0].MediaStreams;
        var virtualSubtitleStreams = streams.Where(s => s.Type == MediaStreamType.Subtitle && s.Index >= SubmarineSubtitleService.VirtualIndexBase).ToList();

        Assert.NotEmpty(virtualSubtitleStreams);
        Assert.Contains(virtualSubtitleStreams, s => s.Codec == "srt");
        Assert.Contains(virtualSubtitleStreams, s => s.Codec == "vtt");
        Assert.Contains(virtualSubtitleStreams, s => s.Codec == "ass");

        foreach (var vs in virtualSubtitleStreams)
        {
            Assert.Equal(MediaStreamType.Subtitle, vs.Type);
            Assert.True(vs.Index >= SubmarineSubtitleService.VirtualIndexBase);
            Assert.Equal(SubtitleDeliveryMethod.External, vs.DeliveryMethod);
            Assert.True(vs.IsExternal);
            Assert.True(vs.SupportsExternalStream);
            Assert.StartsWith($"/Plugins/Submarine/Stream/{itemId}/{vs.Index}/Stream.", vs.DeliveryUrl);
        }
    }

    [Fact]
    public async Task GetAndConvertSubtitleStreamAsync_DownloadsRemoteSrt_ConvertsToVttAndAssWithOffset_AndPersistsToLibrary()
    {
        var itemId = Guid.NewGuid();
        var movie = new Movie { Id = itemId, Name = "Crimson Tide", Path = "/movies/CrimsonTide.mkv" };
        const string remoteId = "opensub-crimson-999";

        var remoteResults = new[]
        {
            new RemoteSubtitleInfo
            {
                Id = remoteId,
                Name = "Crimson.Tide.1995.srt",
                ProviderName = "OpenSubtitles",
                ThreeLetterISOLanguageName = "eng",
                Format = "srt"
            }
        };

        var subtitleManagerMock = CreateSubtitleManagerMock(remoteResults);
        subtitleManagerMock
            .Setup(m => m.GetRemoteSubtitles(remoteId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SubtitleResponse
            {
                Format = "srt",
                Language = "eng",
                Stream = new MemoryStream(Encoding.UTF8.GetBytes(SampleRemoteSrt))
            });

        var libraryManagerMock = new Mock<ILibraryManager>();
        libraryManagerMock.Setup(m => m.GetItemById(itemId)).Returns(movie);

        var service = new SubmarineSubtitleService(
            subtitleManagerMock.Object,
            libraryManagerMock.Object,
            new SubtitleFormatConverter(),
            NullLogger<SubmarineSubtitleService>.Instance);

        var searchResults = await service.SearchSubtitlesAsync(itemId, "eng", targetFormat: "vtt");
        var virtualIndex = searchResults[0].VirtualIndex;

        // 1. Stream & convert to VTT with +1000ms offset and persist to library
        var vttResult = await service.GetAndConvertSubtitleStreamAsync(
            itemId,
            virtualIndex,
            remoteSubtitleId: null,
            targetFormat: "vtt",
            offsetMs: 1000,
            persistToLibrary: true);

        Assert.NotNull(vttResult);
        Assert.Equal("text/vtt; charset=utf-8", vttResult.Value.ContentType);
        Assert.Equal("vtt", vttResult.Value.FileExtension);

        var vttText = Encoding.UTF8.GetString(vttResult.Value.Content);
        Assert.StartsWith("WEBVTT", vttText);
        // Original 00:00:02,000 + 1000ms = 00:00:03.000
        Assert.Contains("00:00:03.000 --> 00:00:06.000", vttText);
        Assert.Contains("Submarine test subtitle line one.", vttText);

        // 2. Stream & convert to ASS with -500ms offset
        var assResult = await service.GetAndConvertSubtitleStreamAsync(
            itemId,
            virtualIndex,
            remoteSubtitleId: null,
            targetFormat: "ass",
            offsetMs: -500,
            persistToLibrary: false);

        Assert.NotNull(assResult);
        Assert.Equal("text/x-ssa; charset=utf-8", assResult.Value.ContentType);
        var assText = Encoding.UTF8.GetString(assResult.Value.Content);
        Assert.Contains("[V4+ Styles]", assText);
        // Original 00:00:02,000 - 500ms = 0:00:01.50
        Assert.Contains("Dialogue: 0,0:00:01.50,0:00:04.50", assText);
        Assert.Contains(@"{\i1}Second cue{\i0} with formatting.", assText);

        subtitleManagerMock.Verify(
            m => m.DownloadSubtitles(movie, remoteId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static Mock<ISubtitleManager> CreateSubtitleManagerMock(RemoteSubtitleInfo[] remoteResults)
    {
        var mock = new Mock<ISubtitleManager>();

        mock.Setup(m => m.SearchSubtitles(
                It.IsAny<Video>(),
                It.IsAny<string>(),
                It.IsAny<bool?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(remoteResults);

        mock.Setup(m => m.SearchSubtitles(
                It.IsAny<SubtitleSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(remoteResults);

        return mock;
    }
}
