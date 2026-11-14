using System;
using System.Linq;
using MediaBrowser.Controller.Lyrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MultiLyrics.Lyrics;

/// <summary>
/// Replaces Jellyfin's lyric manager with <see cref="PluginLyricManager"/>.
/// <para>
/// Jellyfin resolves a single implementation for a service type (the last registration wins) and
/// runs plugin service registrators after its own registrations, which is what makes this work.
/// The host implementation is re-registered under its concrete type so the decorator can wrap it
/// without taking a compile-time dependency on Jellyfin's provider assembly.
/// </para>
/// </summary>
internal static class LyricManagerRegistration
{
    /// <summary>
    /// Installs the decorator. Does nothing when the host registration cannot be located, so a
    /// future change in the host never breaks plugin loading.
    /// </summary>
    /// <param name="services">Service collection being built.</param>
    /// <returns>True when the decorator was installed.</returns>
    internal static bool Install(IServiceCollection services)
    {
        var host = services.LastOrDefault(
            d => d.ServiceType == typeof(ILyricManager) && d.ImplementationType is not null);

        if (host?.ImplementationType is not { } innerType)
        {
            return false;
        }

        services.Remove(host);
        services.AddSingleton(innerType, sp => ActivatorUtilities.CreateInstance(sp, innerType));

        // Only the wrapped host manager is resolved eagerly. Everything the plugin adds is resolved
        // on first use: ProviderManager depends on ILyricManager and the delivery path needs
        // IProviderManager back, so eager resolution here would deadlock the host's DI container.
        services.AddSingleton<ILyricManager>(sp => new PluginLyricManager(
            sp.GetService(innerType) as ILyricManager,
            sp,
            sp.GetRequiredService<ILogger<PluginLyricManager>>()));

        return true;
    }
}
