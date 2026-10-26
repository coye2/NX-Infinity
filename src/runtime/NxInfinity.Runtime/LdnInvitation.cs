using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nx.Networking;

namespace NxInfinity.Runtime;

/// <summary>Exact, conservative compatibility pool. Cross-core interoperability is not assumed.</summary>
public sealed record LdnCompatibility(string BackendId, string BackendRevision, string TitleId,
    string BuildId, string DisplayVersion, string ContentFingerprint);

/// <summary>A private invitation is a bearer secret. Keep it out of diagnostic exports.</summary>
public sealed record LdnInvitation(string Server, LdnCompatibility Compatibility, string Secret, DateTimeOffset ExpiresUtc)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private const string Prefix = "NXORBIT1-";

    public override string ToString() => "Orbit invitation (secret redacted)";

    public static LdnInvitation Create(string server, LdnCompatibility compatibility, DateTimeOffset now)
    {
        ValidateCompatibility(compatibility);
        string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        server = NetworkPolicy.Validate("ryuldn", server, secret);
        return new(server, compatibility, secret, now.AddHours(24));
    }

    public string Encode()
    {
        ValidateCompatibility(Compatibility);
        NetworkPolicy.Validate("ryuldn", Server, Secret);
        return Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this, Json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static LdnInvitation Decode(string code, DateTimeOffset now)
    {
        if (code.Length > 4096 || !code.StartsWith(Prefix, StringComparison.Ordinal))
            throw new NxException("invalid_invitation", "Use a complete NX Orbit invitation code.");
        try
        {
            string encoded = code[Prefix.Length..];
            if (encoded.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) throw new FormatException();
            encoded = encoded.Replace('-', '+').Replace('_', '/').PadRight((encoded.Length + 3) / 4 * 4, '=');
            var invitation = JsonSerializer.Deserialize<LdnInvitation>(Convert.FromBase64String(encoded), Json) ?? throw new FormatException();
            ValidateCompatibility(invitation.Compatibility);
            NetworkPolicy.Validate("ryuldn", invitation.Server, invitation.Secret);
            if (invitation.Secret.Length != 48 || !invitation.Secret.All(Uri.IsHexDigit)) throw new FormatException();
            if (invitation.ExpiresUtc <= now) throw new NxException("invitation_expired", "This invitation expired. Ask the host for a new code.");
            if (invitation.ExpiresUtc > now.AddHours(25)) throw new FormatException();
            return invitation;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException or NullReferenceException)
        { throw new NxException("invalid_invitation", "This invitation is malformed or belongs to an unsupported version."); }
    }

    public LdnSessionOptions Options() => PublicOptions(Server, Compatibility, Secret);

    public static LdnSessionOptions PublicOptions(string server, LdnCompatibility compatibility, string privateSecret = "")
    {
        ValidateCompatibility(compatibility);
        server = NetworkPolicy.Validate("ryuldn", server, privateSecret);
        // The pool includes the exact core, update/build and local selected content.
        // Different pools never share an upstream discovery passphrase.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(compatibility, Json);
        string realm = "NX1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData(bytes)) + privateSecret)));
        return new() { Provider = "ryuldn", Server = server, Passphrase = realm };
    }

    public static void ValidateCompatibility(LdnCompatibility value)
    {
        static bool Hex(string? text, int length) => text?.Length == length && text.All(Uri.IsHexDigit) && text.Any(c => c != '0');
        if (value is null || value.BackendId is not ("nxi") ||
            !Hex(value.BackendRevision, 40) || !Hex(value.TitleId, 16) || !Hex(value.BuildId, 64) || !Hex(value.ContentFingerprint, 64) ||
            string.IsNullOrWhiteSpace(value.DisplayVersion) || value.DisplayVersion.Length > 32 || value.DisplayVersion.Any(char.IsControl))
            throw new NxException("identity_unknown", "Orbit requires the exact native title, update, executable, core revision and content fingerprint.");
    }
}
