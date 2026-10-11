using Jellyfin.Plugin.Submarine.Filters;
using Jellyfin.Plugin.Submarine.Services;
using Jellyfin.Plugin.Submarine.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Submarine;

/// <summary>
/// Registers Submarine services, MVC result filters, and web player startup middleware with the Jellyfin DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ISubtitleFormatConverter, SubtitleFormatConverter>();
        serviceCollection.AddSingleton<ISubmarineSubtitleService, SubmarineSubtitleService>();
        serviceCollection.AddScoped<PlaybackInfoSubtitleFilter>();

        serviceCollection.PostConfigure<MvcOptions>(options =>
        {
            options.Filters.AddService<PlaybackInfoSubtitleFilter>();
        });

        serviceCollection.AddTransient<IStartupFilter, SubmarineStartupFilter>();
    }
}
