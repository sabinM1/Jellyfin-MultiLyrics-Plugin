using System;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.MultiLyrics.Alignment;
using Jellyfin.Plugin.MultiLyrics.Lyrics;
using Jellyfin.Plugin.MultiLyrics.Sources;
using Jellyfin.Plugin.MultiLyrics.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MultiLyrics;

/// <summary>
/// Registers all plugin services in the host DI container. API controllers in this assembly
/// are discovered automatically by the host (it calls AddApplicationPart for every plugin).
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        RegisterHttpClients(serviceCollection);

        // Storage. IApplicationPaths is resolvable once the host container is built.
        serviceCollection.AddSingleton<IResultCache>(sp => new ResultCache(
            Path.Combine(sp.GetRequiredService<MediaBrowser.Common.Configuration.IApplicationPaths>().PluginConfigurationsPath, "Multi-Lyrics", "cache.json")));

        // Lyrics live in memory only: no sidecar file is written to the library or metadata folder.
        serviceCollection.AddSingleton<EphemeralLyricsStore>();
        serviceCollection.AddSingleton<AlignmentClient>();

        // Sources (order is decided at runtime from configuration, not registration order)
        serviceCollection.AddSingleton<ILyricSource, LrcLibSource>();
        serviceCollection.AddSingleton<ILyricSource, LyricsOvhSource>();
        serviceCollection.AddSingleton<ILyricSource, TekstowoSource>();
        serviceCollection.AddSingleton<ILyricSource, GeniusSource>();

        // Queue + Jellyfin integration
        serviceCollection.AddSingleton<ILyricsQueue, LyricsSearchService>();
        serviceCollection.AddSingleton<MediaBrowser.Controller.Lyrics.ILyricProvider, MultiLyricProvider>();

        // GET /Audio/{id}/Lyrics: wrap the host lyric manager so a track fetched on demand is
        // served straight from memory.
        LyricManagerRegistration.Install(serviceCollection);
    }

    private static void RegisterHttpClients(IServiceCollection services)
    {
        services.AddHttpClient(Constants.HttpClientNames.LrcLib, c =>
        {
            c.BaseAddress = new Uri("https://lrclib.net/");
            c.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.UserAgent);
            // Bounded so one unresponsive source cannot consume the whole on-demand budget.
            c.Timeout = TimeSpan.FromSeconds(8);
        });

        services.AddHttpClient(Constants.HttpClientNames.LyricsOvh, c =>
        {
            c.BaseAddress = new Uri("https://api.lyrics.ovh/");
            c.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.UserAgent);
            c.Timeout = TimeSpan.FromSeconds(8);
        });

        services.AddHttpClient(Constants.HttpClientNames.Tekstowo, c =>
        {
            c.BaseAddress = new Uri("https://www.tekstowo.pl/");
            c.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.UserAgent);
            c.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddHttpClient(Constants.HttpClientNames.Genius, c =>
        {
            c.BaseAddress = new Uri("https://api.genius.com/");
            c.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.UserAgent);
            c.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddHttpClient(Constants.HttpClientNames.GeniusPage, c =>
        {
            // Song pages are fetched only in scrape mode, which requires explicit acknowledgement.
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
            c.Timeout = TimeSpan.FromSeconds(12);
        });

        services.AddHttpClient(Constants.HttpClientNames.Alignment, c =>
        {
            // Per-request timeouts are set by the client code: submit/poll calls are short,
            // the synchronous fallback call may legitimately run for minutes.
            c.Timeout = Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.HttpClientHandler
        {
            // A redirect could hop to an internal address after validation. Disabled.
            AllowAutoRedirect = false
        });
    }
}
