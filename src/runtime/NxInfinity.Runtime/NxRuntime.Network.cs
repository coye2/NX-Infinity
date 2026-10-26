using Nx.Networking;

namespace NxInfinity.Runtime;

public sealed partial class NxRuntime
{
    private bool _ldnConfigured;

    /// <summary>Configure real native Local Wireless before initialization. A fresh non-default profile is required.</summary>
    public async Task<LdnSessionState> ConfigureLdnAsync(LdnSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        try { NetworkPolicy.Validate(options.Provider, options.Server, options.Passphrase); }
        catch (ArgumentException error) { throw new NxException("invalid_argument", error.Message); }
        await _operations.WaitAsync(cancellationToken);
        try
        {
            Require(RuntimeState.Created);
            var state = ReadLdnState(NxJson.Read<LdnSessionState>(await CallSupportedAsync("network.configure", options, cancellationToken)));
            if (state.Provider != options.Provider || !string.Equals(state.Server, options.Server, StringComparison.OrdinalIgnoreCase) ||
                state.PrivateSession != (options.Passphrase.Length > 0))
                throw new NxException("protocol_mismatch", "Native networking did not acknowledge the requested configuration.");
            _ldnConfigured = options.Provider == "ryuldn";
            return state;
        }
        finally { _operations.Release(); }
    }

    public async Task<LdnSessionState> GetLdnStateAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            Require(RuntimeState.Created, RuntimeState.Initialized, RuntimeState.Loaded, RuntimeState.Running, RuntimeState.Paused, RuntimeState.Stopped);
            return ReadLdnState(NxJson.Read<LdnSessionState>(await CallSupportedAsync("network.get", null, cancellationToken)));
        }
        finally { _operations.Release(); }
    }

    private static LdnSessionState ReadLdnState(LdnSessionState state)
    {
        if (state.Provider is not ("offline" or "ryuldn") || state.Port != NetworkPolicy.Port || !state.RelayOnly ||
            state.GuestInternetEnabled || state.GameplayVerified || state.Stage is not
            ("disabled" or "configured" or "awaiting_game" or "connecting" or "relay_connected" or "disconnected" or "error") ||
            state.ErrorCode is not (null or "dns_failed" or "connection_failed"))
            throw new NxException("protocol_mismatch", "Invalid native Local Wireless state.");
        return state;
    }
}
