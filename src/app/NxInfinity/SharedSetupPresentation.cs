using System;
using System.Collections.Generic;
using System.Linq;
using NxInfinity.Runtime.Services;

namespace NxInfinity;

public static class SharedSetupPresentation
{
    public static IReadOnlyList<SharedPrerequisiteOutcome> LatestForCore(SharedPrerequisiteStatus setup, string backendId, string revision, string profileId = "default")
    {
        ArgumentNullException.ThrowIfNull(setup, "setup");
        Dictionary<string, SharedPrerequisiteRevision> currentSources = setup.Sources.ToDictionary((SharedPrerequisiteRevision source) => source.Id, StringComparer.Ordinal);
        return (from outcome in setup.Outcomes
                where outcome.BackendId == backendId && string.Equals(outcome.BackendRevision, revision, StringComparison.OrdinalIgnoreCase) && outcome.ProfileId == profileId && currentSources.TryGetValue(outcome.SourceRevisionId, out var value) && outcome.Kind == value.Kind && outcome.Slot == value.Slot
                group outcome by (Kind: outcome.Kind, Slot: outcome.Slot) into @group
                select @group.OrderByDescending((SharedPrerequisiteOutcome outcome) => outcome.ObservedUtc ?? DateTimeOffset.MinValue).ThenBy((SharedPrerequisiteOutcome outcome) => outcome.InstallationFingerprint, StringComparer.Ordinal).First()).OrderBy((SharedPrerequisiteOutcome outcome) => outcome.Kind, StringComparer.Ordinal).ThenBy((SharedPrerequisiteOutcome outcome) => outcome.Slot, StringComparer.Ordinal).ToArray();
    }
}
