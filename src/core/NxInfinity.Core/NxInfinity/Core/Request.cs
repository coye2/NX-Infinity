using System.Text.Json;

namespace NxInfinity.Core;

internal sealed record Request(long Id, string Method, JsonElement Params);
