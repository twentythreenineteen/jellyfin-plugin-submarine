using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Submarine.Api;
using Jellyfin.Plugin.Submarine.Configuration;
using Jellyfin.Plugin.Submarine.Models;
using Jellyfin.Plugin.Submarine.Services;
using Jellyfin.Plugin.Submarine.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Submarine.Tests;

/// <summary>
/// Unit and integration tests for <see cref="SubmarineController"/> endpoints, embedded web assets,
/// and <see cref="SubmarineWebInjectionMiddleware"/> HTML script/style injection.
/// </summary>
public class SubmarineControllerAndWebMiddlewareTests
{
    public SubmarineControllerAndWebMiddlewareTests()
    {
        _ = new Plugin(new PluginConfiguration
        {
            EnableUniversalVirtualStreams = true,
            EnableWebPlayerModal = true
        });
    }

    [Fact]
    public async Task SearchSubtitles_Endpoint_ReturnsOkWithVirtualTracks()
    {
        var itemId = Guid.NewGuid();
        var expectedTracks = new List<VirtualSubtitleTrack>
        {
            new VirtualSubtitleTrack
            {
                VirtualIndex = 10000,
                ItemId = itemId,
                RemoteSubtitleId = "remote-1",
                Language = "eng",
                TargetFormat = "vtt",
                DeliveryUrl = $"/Plugins/Submarine/Stream/{itemId}/10000/Stream.vtt",
                DisplayTitle = "⬇ [Submarine • OpenSubtitles] ENG (VTT) - Movie.srt"
            }
        };

        var serviceMock = new Mock<ISubmarineSubtitleService>();
        serviceMock
            .Setup(s => s.SearchSubtitlesAsync(itemId, "eng", "BluRay", "vtt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedTracks);

        var controller = CreateController(serviceMock.Object);
        var actionResult = await controller.SearchSubtitles(itemId, "eng", "BluRay", "vtt", CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var returnedTracks = Assert.IsAssignableFrom<IReadOnlyList<VirtualSubtitleTrack>>(okResult.Value);
        Assert.Single(returnedTracks);
        Assert.Equal(10000, returnedTracks[0].VirtualIndex);
    }

    [Fact]
    public async Task StreamVirtualSubtitleByIndex_Endpoint_ReturnsConvertedFileWithCorsHeader()
    {
        var itemId = Guid.NewGuid();
        var vttBytes = Encoding.UTF8.GetBytes("WEBVTT\n\n1\n00:00:01.000 --> 00:00:03.000\nSubmarine Stream\n");

        var serviceMock = new Mock<ISubmarineSubtitleService>();
        serviceMock
            .Setup(s => s.GetAndConvertSubtitleStreamAsync(itemId, 10001, null, "vtt", 250, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((vttBytes, "text/vtt; charset=utf-8", "vtt"));

        var controller = CreateController(serviceMock.Object);
        var result = await controller.StreamVirtualSubtitleByIndex(itemId, 10001, "vtt", offsetMs: 250, persist: true, CancellationToken.None);

        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/vtt; charset=utf-8", fileResult.ContentType);
        Assert.Equal("subtitle.vtt", fileResult.FileDownloadName);
        Assert.Equal(vttBytes, fileResult.FileContents);
        Assert.Equal("*", controller.Response.Headers["Access-Control-Allow-Origin"].ToString());
    }

    [Fact]
    public async Task DownloadSubtitle_Endpoint_ValidatesRequestAndReturnsDownloadResult()
    {
        var itemId = Guid.NewGuid();
        var request = new SubmarineDownloadRequest
        {
            RemoteSubtitleId = "sub-id-77",
            TargetFormat = "ass",
            OffsetMs = -500,
            PersistToLibrary = true,
            Language = "eng"
        };

        var expectedResult = new SubmarineDownloadResult
        {
            Success = true,
            Message = "Subtitle downloaded and converted on-the-fly.",
            DeliveryUrl = $"/Plugins/Submarine/Stream/{itemId}/10005/Stream.ass?offsetMs=-500",
            Format = "ass",
            CueCount = 42,
            SavedStreamIndex = 10005
        };

        var serviceMock = new Mock<ISubmarineSubtitleService>();
        serviceMock
            .Setup(s => s.DownloadAndApplySubtitleAsync(itemId, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var controller = CreateController(serviceMock.Object);

        // Verify BadRequest when RemoteSubtitleId is missing
        var badAction = await controller.DownloadSubtitle(itemId, new SubmarineDownloadRequest { RemoteSubtitleId = "" }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(badAction.Result);

        // Verify Ok result for valid download request
        var okAction = await controller.DownloadSubtitle(itemId, request, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(okAction.Result);
        var actual = Assert.IsType<SubmarineDownloadResult>(okResult.Value);
        Assert.True(actual.Success);
        Assert.Equal("ass", actual.Format);
        Assert.Equal(42, actual.CueCount);
        Assert.Equal(10005, actual.SavedStreamIndex);
    }

    [Fact]
    public void ConvertSubtitle_Endpoint_ConvertsBetweenFormatsWithOffset()
    {
        var controller = CreateController(new Mock<ISubmarineSubtitleService>().Object);

        var request = new SubmarineConvertRequest
        {
            Content = "1\n00:00:01,000 --> 00:00:03,000\nConvert endpoint test",
            SourceFormat = "srt",
            TargetFormat = "vtt",
            OffsetMs = 1000
        };

        var actionResult = controller.ConvertSubtitle(request);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<SubmarineConvertResponse>(okResult.Value);

        Assert.Equal("vtt", response.TargetFormat);
        Assert.Equal("text/vtt; charset=utf-8", response.ContentType);
        Assert.StartsWith("WEBVTT", response.Content);
        Assert.Contains("00:00:02.000 --> 00:00:04.000", response.Content);
    }

    [Fact]
    public void GetClientScript_And_GetClientStyles_ReturnEmbeddedResourcesWithSubmarineHooks()
    {
        var controller = CreateController(new Mock<ISubmarineSubtitleService>().Object);

        // 1. Verify ClientScript
        var scriptResult = controller.GetClientScript();
        var scriptStreamResult = Assert.IsType<FileStreamResult>(scriptResult);
        Assert.Equal("application/javascript; charset=utf-8", scriptStreamResult.ContentType);

        using var scriptReader = new StreamReader(scriptStreamResult.FileStream, Encoding.UTF8);
        var scriptContent = scriptReader.ReadToEnd();
        Assert.False(string.IsNullOrWhiteSpace(scriptContent));
        Assert.Contains("SubmarinePlayer", scriptContent);
        Assert.Contains("Plugins/Submarine", scriptContent);

        // 2. Verify ClientStyles
        var styleResult = controller.GetClientStyles();
        var styleStreamResult = Assert.IsType<FileStreamResult>(styleResult);
        Assert.Equal("text/css; charset=utf-8", styleStreamResult.ContentType);

        using var styleReader = new StreamReader(styleStreamResult.FileStream, Encoding.UTF8);
        var styleContent = styleReader.ReadToEnd();
        Assert.False(string.IsNullOrWhiteSpace(styleContent));
        Assert.Contains("#submarine-modal-backdrop", styleContent);
        Assert.Contains(".submarine-modal", styleContent);
    }

    [Fact]
    public async Task SubmarineWebInjectionMiddleware_InjectsScriptAndStylesBeforeClosingBody_AndPassesThroughNonHtml()
    {
        const string originalHtml = "<!DOCTYPE html><html><head><title>Jellyfin</title></head><body><div id=\"app\"></div></body></html>";

        RequestDelegate nextHtml = async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(originalHtml);
            await ctx.Response.Body.WriteAsync(bytes);
        };

        var middleware = new SubmarineWebInjectionMiddleware(nextHtml);
        var htmlContext = new DefaultHttpContext();
        htmlContext.Request.Method = HttpMethods.Get;
        htmlContext.Request.Path = "/web/index.html";
        using var responseStream = new MemoryStream();
        htmlContext.Response.Body = responseStream;

        await middleware.InvokeAsync(htmlContext);

        responseStream.Seek(0, SeekOrigin.Begin);
        var transformedHtml = new StreamReader(responseStream, Encoding.UTF8).ReadToEnd();

        Assert.Contains("<link rel=\"stylesheet\" href=\"/Plugins/Submarine/ClientStyles\" />", transformedHtml);
        Assert.Contains("<script src=\"/Plugins/Submarine/ClientScript\" defer></script>", transformedHtml);
        var scriptPos = transformedHtml.IndexOf("<script src=\"/Plugins/Submarine/ClientScript\" defer></script>", StringComparison.Ordinal);
        var bodyClosePos = transformedHtml.IndexOf("</body>", StringComparison.Ordinal);
        Assert.True(scriptPos > 0 && scriptPos < bodyClosePos);

        // Verify non-HTML path passes through untouched
        const string apiJson = "{\"status\":\"ok\"}";
        RequestDelegate nextJson = async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(apiJson));
        };

        var jsonMiddleware = new SubmarineWebInjectionMiddleware(nextJson);
        var jsonContext = new DefaultHttpContext();
        jsonContext.Request.Method = HttpMethods.Get;
        jsonContext.Request.Path = "/System/Info/Public";
        using var jsonStream = new MemoryStream();
        jsonContext.Response.Body = jsonStream;

        await jsonMiddleware.InvokeAsync(jsonContext);

        jsonStream.Seek(0, SeekOrigin.Begin);
        var jsonOutput = new StreamReader(jsonStream, Encoding.UTF8).ReadToEnd();
        Assert.Equal(apiJson, jsonOutput);
        Assert.DoesNotContain("/Plugins/Submarine/ClientScript", jsonOutput);
    }

    private static SubmarineController CreateController(ISubmarineSubtitleService subtitleService)
    {
        var controller = new SubmarineController(
            subtitleService,
            new SubtitleFormatConverter(),
            NullLogger<SubmarineController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        return controller;
    }
}
