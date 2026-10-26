using System.Net;
using System.Text;

namespace Nx.Networking;

// Compiled into the SDK and both Ryu workers so raw protocol calls receive the
// same endpoint bounds as SDK calls. This performs no DNS lookup or connection.
internal static class NetworkPolicy
{
    internal const int Port = 30456;

    internal static string Validate(string provider, string server, string passphrase)
    {
        if (provider is null || server is null || passphrase is null)
            throw new ArgumentException("Network configuration fields must be strings.");
        if (provider is not ("offline" or "ryuldn"))
            throw new ArgumentException("This adapter supports offline or RyuLDN Local Wireless only.");
        if (provider == "offline")
        {
            if (server.Length != 0 || passphrase.Length != 0)
                throw new ArgumentException("Offline mode must not contain a server or invitation secret.");
            return "";
        }
        if (server.Length is < 1 or > 253 || server != server.Trim() || server.Any(char.IsControl))
            throw new ArgumentException("Enter a server hostname or IP address without a URL, port or credentials.");
        if (IPAddress.TryParse(server, out var ip))
        {
            var bytes = ip.GetAddressBytes();
            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast) ||
                ip.IsIPv6Multicast || bytes.Length == 4 && bytes[0] >= 224)
                throw new ArgumentException("A multicast, broadcast or unspecified server address is not allowed.");
        }
        else if (server.Split('.').Any(label => label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' ||
                     label.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))))
            throw new ArgumentException("Enter a server hostname or IP address without a URL, port or credentials.");
        if (Encoding.UTF8.GetByteCount(passphrase) > 96 || passphrase.Any(char.IsControl))
            throw new ArgumentException("The private session secret must contain at most 96 UTF-8 bytes and no control characters.");
        return server.ToLowerInvariant();
    }
}
