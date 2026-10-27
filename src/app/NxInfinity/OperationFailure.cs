using System;

namespace NxInfinity;

public sealed record OperationFailure(DateTimeOffset Utc, long OperationSequence, string OperationId, string Stage, string? BackendId, string? BackendRevision, string Code, string ReasonCode, string RecoveryAction);
