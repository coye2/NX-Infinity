namespace NxInfinity.Runtime;

/// <summary>Session-only Local Wireless routing. Never serialize into diagnostics.</summary>
public sealed record LdnSessionOptions
{
    public string Provider { get; init; } = "offline";
    public string Server { get; init; } = "";
    public string Passphrase { get; init; } = "";
    public override string ToString() => $"LDN options ({Provider}; secret redacted)";
}

/// <summary>Native configuration and bounded connection observations, without a passphrase or peer identity.</summary>
public sealed record LdnSessionState
{
    public string Provider { get; init; } = "offline";
    public string Server { get; init; } = "";
    public int Port { get; init; }
    public bool PrivateSession { get; init; }
    public bool RelayOnly { get; init; }
    public bool GuestInternetEnabled { get; init; }
    public bool NativeConfigurationApplied { get; init; }
    public string Stage { get; init; } = "disabled";
    public string? ErrorCode { get; init; }
    public bool GameplayVerified { get; init; }
}
