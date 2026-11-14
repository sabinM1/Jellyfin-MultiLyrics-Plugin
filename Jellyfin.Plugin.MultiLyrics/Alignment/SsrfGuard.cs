using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MultiLyrics.Alignment;

/// <summary>
/// Decision returned by the SSRF guard.
/// </summary>
/// <param name="Allowed">True when the request may proceed.</param>
/// <param name="Reason">Refusal reason for logs (never contains secrets).</param>
public sealed record SsrfDecision(bool Allowed, string? Reason = null);

/// <summary>
/// Guards the optional alignment-service client against SSRF: only http(s) URLs with a
/// global (or, when explicitly allowed by the admin, private/loopback) destination are
/// accepted. Cloud metadata endpoints are refused even when private addresses are allowed.
/// The transport disables redirects, so validation cannot be bypassed by a redirect hop.
/// </summary>
public static class SsrfGuard
{
    private static readonly string[] MetadataHostnames =
    [
        "metadata.google.internal",
        "metadata",
        "instance-data",
        "169.254.169.254",
        "metadata.goog"
    ];

    /// <summary>
    /// Validates a URL and all addresses its host resolves to.
    /// </summary>
    /// <param name="rawUrl">Configured alignment service base URL.</param>
    /// <param name="allowPrivate">Admin toggle permitting private/loopback destinations.</param>
    /// <returns>The decision.</returns>
    public static async Task<SsrfDecision> ValidateAsync(string rawUrl, bool allowPrivate)
    {
        if (!Uri.TryCreate(rawUrl?.Trim(), UriKind.Absolute, out var uri))
        {
            return new SsrfDecision(false, "not an absolute URL");
        }

        var urlCheck = CheckUrl(uri, allowPrivate);
        if (!urlCheck.Allowed)
        {
            return urlCheck;
        }

        if (Uri.CheckHostName(uri.Host) == UriHostNameType.Dns)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(uri.IdnHost).ConfigureAwait(false);
                foreach (var address in addresses)
                {
                    var addrCheck = CheckAddress(address, allowPrivate);
                    if (!addrCheck.Allowed)
                    {
                        return addrCheck;
                    }
                }

                if (addresses.Length == 0)
                {
                    return new SsrfDecision(false, "host does not resolve");
                }
            }
            catch (SocketException)
            {
                return new SsrfDecision(false, "host does not resolve");
            }
        }

        return new SsrfDecision(true);
    }

    /// <summary>
    /// Validates the URL itself (scheme, userinfo, host-name blocklist) without DNS.
    /// Internal for unit tests.
    /// </summary>
    /// <param name="uri">Parsed URL.</param>
    /// <param name="allowPrivate">Admin toggle.</param>
    /// <returns>The decision.</returns>
    internal static SsrfDecision CheckUrl(Uri uri, bool allowPrivate)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            return new SsrfDecision(false, $"scheme {uri.Scheme} not allowed");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return new SsrfDecision(false, "userinfo in URL not allowed");
        }

        var host = uri.IdnHost;
        if (host.Length == 0)
        {
            return new SsrfDecision(false, "empty host");
        }

        // Cloud metadata endpoints are refused regardless of the private-address toggle.
        if (MetadataHostnames.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            return new SsrfDecision(false, "cloud metadata endpoint refused");
        }

        // Literal IPs go through the address classifier too.
        if (IPAddress.TryParse(host, out var ip))
        {
            return CheckAddress(ip, allowPrivate);
        }

        return new SsrfDecision(true);
    }

    /// <summary>
    /// Classifies one resolved address. Internal for unit tests.
    /// </summary>
    /// <param name="address">Resolved address.</param>
    /// <param name="allowPrivate">Admin toggle.</param>
    /// <returns>The decision.</returns>
    internal static SsrfDecision CheckAddress(IPAddress address, bool allowPrivate)
    {
        if (IsMetadataAddress(address) || IsCgnat(address))
        {
            // 100.64/10 hosts cloud metadata endpoints (e.g. Alibaba 100.100.100.200) on some
            // platforms; refused regardless of the private-host toggle.
            return new SsrfDecision(false, "cloud metadata endpoint refused");
        }

        if (IsGlobal(address))
        {
            return new SsrfDecision(true);
        }

        return allowPrivate
            ? new SsrfDecision(true)
            : new SsrfDecision(false, $"non-global address {address} refused (enable 'allow private alignment host' for LAN/Docker deployments)");
    }

    private static bool IsCgnat(IPAddress address)
    {
        return address.AddressFamily == AddressFamily.InterNetwork
            && address.GetAddressBytes() is var b
            && b[0] == 100
            && b[1] >= 64
            && b[1] <= 127;
    }

    private static bool IsMetadataAddress(IPAddress address)
    {
        // 169.254.169.254 (AWS/GCP/Azure/Alibaba instance metadata).
        if (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is var v4)
        {
            return v4[0] == 169 && v4[1] == 254 && v4[2] == 169 && v4[3] == 254;
        }

        return false;
    }

    private static bool IsGlobal(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv4MappedToIPv6)
        {
            return address.IsIPv4MappedToIPv6 && IsGlobalV4(address.MapToIPv4());
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsGlobalV4(address),
            AddressFamily.InterNetworkV6 => IsGlobalV6(address),
            _ => false
        };
    }

    private static bool IsGlobalV4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (b[0], b[1]) switch
        {
            (0, _) => false,                          // 0.0.0.0/8 "this network"
            (10, _) => false,                         // 10/8 private
            (100, >= 64 and <= 127) => false,         // 100.64/10 CGNAT
            (127, _) => false,                        // loopback
            (169, 254) => false,                      // link-local (incl. cloud metadata)
            (172, >= 16 and <= 31) => false,          // 172.16/12 private
            (192, 0) => false,                        // 192.0.0/24 + 192.0.2/24 test
            (192, 168) => false,                      // 192.168/16 private
            (198, >= 18 and <= 19) => false,          // 198.18/15 benchmark
            (198, 51) => false,                       // 198.51.100/24 test
            (203, 0) => false,                        // 203.0.113/24 test
            (224, _) => false,                        // multicast
            (>= 240, _) => false,                     // reserved / broadcast
            _ => true
        };
    }

    private static bool IsGlobalV6(IPAddress address)
    {
        var b = address.GetAddressBytes();
        // fc00::/7 unique-local, ::1 loopback, fe80::/10 link-local, ::/128 unspecified,
        // 2001:db8::/32 documentation, ff00::/8 multicast.
        if (b[0] == 0xfc || b[0] == 0xfd || b[0] == 0xff)
        {
            return false;
        }

        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8)
        {
            return false;
        }

        var allZero = b.All(static x => x == 0);
        if (allZero)
        {
            return false;
        }

        if (b[0] == 0xfe && (b[1] & 0xf0) == 0x80)
        {
            return false;
        }

        var isLoopback = b[..15].All(static x => x == 0) && b[15] == 1;
        return !isLoopback;
    }
}
