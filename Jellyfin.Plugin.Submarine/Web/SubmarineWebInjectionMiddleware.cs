using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Submarine.Web;

/// <summary>
/// Middleware that intercepts requests for Jellyfin's <c>index.html</c> and injects the Submarine
/// stylesheet and client script tags before <c>&lt;/body&gt;</c> (or <c>&lt;/head&gt;</c>).
/// All non-index.html requests pass through with zero overhead.
/// </summary>
public sealed class SubmarineWebInjectionMiddleware
{
    private const string ScriptMarker = "/Plugins/Submarine/ClientScript";
    private const string InjectionSnippet =
        "<link rel=\"stylesheet\" href=\"/Plugins/Submarine/ClientStyles\" />" +
        "<script src=\"/Plugins/Submarine/ClientScript\" defer></script>";

    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubmarineWebInjectionMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    public SubmarineWebInjectionMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    /// <summary>
    /// Processes an HTTP request and injects Submarine web client assets into <c>index.html</c> when enabled.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!ShouldIntercept(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Disable response compression for index.html so we can safely inspect and inject the HTML stream.
        var originalAcceptEncoding = context.Request.Headers.AcceptEncoding;
        context.Request.Headers.Remove("Accept-Encoding");

        var originalBodyStream = context.Response.Body;
        using var bufferStream = new MemoryStream();
        context.Response.Body = bufferStream;

        try
        {
            await _next(context).ConfigureAwait(false);

            bufferStream.Seek(0, SeekOrigin.Begin);

            var contentType = context.Response.ContentType ?? string.Empty;
            if (context.Response.StatusCode == StatusCodes.Status200OK &&
                contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase) &&
                bufferStream.Length > 0)
            {
                var html = await ReadResponseTextAsync(bufferStream, context.Response.Headers.ContentEncoding).ConfigureAwait(false);
                var injectedHtml = InjectSnippet(html);

                var outputBytes = Encoding.UTF8.GetBytes(injectedHtml);
                context.Response.Headers.Remove("Content-Encoding");
                context.Response.ContentLength = outputBytes.Length;

                await originalBodyStream.WriteAsync(outputBytes, context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                await bufferStream.CopyToAsync(originalBodyStream, context.RequestAborted).ConfigureAwait(false);
            }
        }
        finally
        {
            context.Response.Body = originalBodyStream;
            if (originalAcceptEncoding.Count > 0)
            {
                context.Request.Headers.AcceptEncoding = originalAcceptEncoding;
            }
        }
    }

    /// <summary>
    /// Injects the Submarine client stylesheet and script into an HTML document if not already present.
    /// </summary>
    /// <param name="html">The source HTML document.</param>
    /// <returns>The transformed HTML document containing the Submarine client snippet.</returns>
    public static string InjectSnippet(string html)
    {
        if (string.IsNullOrEmpty(html) || html.Contains(ScriptMarker, StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var bodyIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyIndex >= 0)
        {
            return html.Insert(bodyIndex, InjectionSnippet);
        }

        var headIndex = html.LastIndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headIndex >= 0)
        {
            return html.Insert(headIndex, InjectionSnippet);
        }

        return html + InjectionSnippet;
    }

    private static bool ShouldIntercept(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        if (!(Plugin.Instance?.Configuration.EnableWebPlayerModal ?? true))
        {
            return false;
        }

        var path = request.Path.Value;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/index.html", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadResponseTextAsync(Stream stream, string? contentEncoding)
    {
        Stream readStream = stream;
        if (string.Equals(contentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
        {
            readStream = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        }
        else if (string.Equals(contentEncoding, "br", StringComparison.OrdinalIgnoreCase))
        {
            readStream = new BrotliStream(stream, CompressionMode.Decompress, leaveOpen: true);
        }

        using var reader = new StreamReader(readStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }
}
