namespace NxInfinity;

internal sealed record ValidationRunGuard(long LaunchGeneration, long OperationFailureSequence)
{
    public string? Observe(long launchGeneration, long operationFailureSequence)
    {
        if (launchGeneration == LaunchGeneration)
        {
            if (operationFailureSequence == OperationFailureSequence)
            {
                return null;
            }
            return "frontend_operation_failed";
        }
        return "validation_session_changed";
    }
}
