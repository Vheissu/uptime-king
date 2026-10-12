using System.Net;
using System.Net.Sockets;

namespace UptimeKing.Api.Monitoring;

public static class PublicEndpointPolicy
{
    public static bool IsValidUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment)
        && (
            IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)
                ? IsPublic(address)
                : uri.Host.Contains('.')
        );

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            // Only globally routable unicast; exclude documentation, Teredo and 6to4.
            return (b[0] & 0xe0) == 0x20
                && !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || b[2] == 0x0d && b[3] == 0xb8))
                && !(b[0] == 0x20 && b[1] == 0x02)
                && !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0);
        return b[0] is not (0 or 10 or 127)
            && b[0] < 224
            // Azure WireServer is a platform endpoint despite its public-looking address.
            && !(b[0] == 168 && b[1] == 63 && b[2] == 129 && b[3] == 16)
            && !(b[0] == 100 && b[1] is >= 64 and <= 127)
            && !(b[0] == 169 && b[1] == 254)
            && !(b[0] == 172 && b[1] is >= 16 and <= 31)
            && !(b[0] == 192 && (b[1] == 168 || b[1] == 0 || b[1] == 88 && b[2] == 99))
            && !(b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100))
            && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }

    public static SocketsHttpHandler CreateHandler() =>
        new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a)))
                    throw new HttpRequestException(
                        "The target does not resolve exclusively to public addresses."
                    );
                // Connect to the validated address, never resolve the hostname a second time.
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
}
