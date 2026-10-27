using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NxInfinity.Runtime;

namespace NxInfinity;

public sealed class InteractionConversation(InteractionRequest initial, ChannelReader<InteractionRequest> updates, Func<InteractionResponse, CancellationToken, Task> respond)
{
    public InteractionRequest Initial { get; } = initial;

    public ChannelReader<InteractionRequest> Updates { get; } = updates;

    public Task RespondAsync(InteractionResponse response, CancellationToken ct = default(CancellationToken))
    {
        return respond(response, ct);
    }
}
