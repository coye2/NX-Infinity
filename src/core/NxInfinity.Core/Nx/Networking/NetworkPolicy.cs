using System;
using System.Linq;
using System.Net;
using System.Text;

namespace Nx.Networking;

internal static class NetworkPolicy
{
    internal const int Port = 30456;

    internal static string Validate(string provider, string server, string passphrase)
    {
        if (provider == null || server == null || passphrase == null)
        {
            throw new ArgumentException("Network configuration fields must be strings.");
        }
        if ((!(provider == "offline") && !(provider == "ryuldn")) || 1 == 0)
        {
            throw new ArgumentException("This adapter supports offline or RyuLDN Local Wireless only.");
        }
        if (provider == "offline")
        {
            if (server.Length != 0 || passphrase.Length != 0)
            {
                throw new ArgumentException("Offline mode must not contain a server or invitation secret.");
            }
            return "";
        }
        int length = server.Length;
        bool flag = ((length < 1 || length > 253) ? true : false);
        if (flag || server != server.Trim() || server.Any(char.IsControl))
        {
            throw new ArgumentException("Enter a server hostname or IP address without a URL, port or credentials.");
        }
        if (IPAddress.TryParse(server, out IPAddress address))
        {
            byte[] addressBytes = address.GetAddressBytes();
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast) || address.IsIPv6Multicast || (addressBytes.Length == 4 && addressBytes[0] >= 224))
            {
                throw new ArgumentException("A multicast, broadcast or unspecified server address is not allowed.");
            }
        }
        else if (server.Split('.').Any((string label) =>
        {
            int length2 = label.Length;
            bool flag2 = ((length2 < 1 || length2 > 63) ? true : false);
            if (!flag2 && label[0] != '-')
            {
                if (label[label.Length - 1] != '-')
                {
                    return label.Any((char c) =>
                    {
                        bool flag3;
                        switch (c)
                        {
                            case '-':
                            case '0':
                            case '1':
                            case '2':
                            case '3':
                            case '4':
                            case '5':
                            case '6':
                            case '7':
                            case '8':
                            case '9':
                            case 'A':
                            case 'B':
                            case 'C':
                            case 'D':
                            case 'E':
                            case 'F':
                            case 'G':
                            case 'H':
                            case 'I':
                            case 'J':
                            case 'K':
                            case 'L':
                            case 'M':
                            case 'N':
                            case 'O':
                            case 'P':
                            case 'Q':
                            case 'R':
                            case 'S':
                            case 'T':
                            case 'U':
                            case 'V':
                            case 'W':
                            case 'X':
                            case 'Y':
                            case 'Z':
                            case 'a':
                            case 'b':
                            case 'c':
                            case 'd':
                            case 'e':
                            case 'f':
                            case 'g':
                            case 'h':
                            case 'i':
                            case 'j':
                            case 'k':
                            case 'l':
                            case 'm':
                            case 'n':
                            case 'o':
                            case 'p':
                            case 'q':
                            case 'r':
                            case 's':
                            case 't':
                            case 'u':
                            case 'v':
                            case 'w':
                            case 'x':
                            case 'y':
                            case 'z':
                                flag3 = true;
                                break;
                            default:
                                flag3 = false;
                                break;
                        }
                        return !flag3;
                    });
                }
            }
            return true;
        }))
        {
            throw new ArgumentException("Enter a server hostname or IP address without a URL, port or credentials.");
        }
        if (Encoding.UTF8.GetByteCount(passphrase) > 96 || passphrase.Any(char.IsControl))
        {
            throw new ArgumentException("The private session secret must contain at most 96 UTF-8 bytes and no control characters.");
        }
        return server.ToLowerInvariant();
    }
}
