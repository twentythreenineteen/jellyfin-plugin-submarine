using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.Submarine.Web;

/// <summary>
/// Startup filter that registers <see cref="SubmarineWebInjectionMiddleware"/> early in the ASP.NET Core pipeline
/// so that Jellyfin's web index.html automatically loads the Submarine in-player subtitle search modal.
/// </summary>
public sealed class SubmarineStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<SubmarineWebInjectionMiddleware>();
            next(app);
        };
    }
}
