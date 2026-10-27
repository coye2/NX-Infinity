using System;

namespace NxInfinity;

public sealed record SaveMigrationCoreStatus(string BackendId, string BackendRevision, string Status, int EligibleTargets, int ImportedCount, string Message, DateTimeOffset ObservedUtc);
