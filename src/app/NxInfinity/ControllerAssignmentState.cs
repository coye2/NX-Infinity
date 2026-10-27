namespace NxInfinity;

public sealed record ControllerAssignmentState(ControllerAssignmentPhase Phase, string? PreferredName, string? DeviceId, int ConnectedCount, int MatchingNameCount, bool PreferenceSaved);
