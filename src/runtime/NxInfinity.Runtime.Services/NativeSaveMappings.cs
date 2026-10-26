namespace NxInfinity.Runtime.Services;

/// <summary>Copy of an actual adapter query, bound by its caller to the selected runtime revision.</summary>
public sealed record NativeSaveObservation(string BackendId, string BackendRevision, string TitleId, string UserId,
    string SaveDataId, string Type, string FormatId, string FormatVersion, string RelativeDirectory,
    bool Existing, bool AdapterVerified, string Provenance);

public static class NativeSaveMappings
{
    /// <summary>
    /// Converts an existing adapter-discovered save into the stopped-only store contract.
    /// This factory validates the supplied observation; it cannot authenticate a caller or query a core.
    /// </summary>
    public static SaveDirectoryMapping FromNativeObservation(NativeSaveObservation observation,
        string expectedBackendId, string expectedBackendRevision)
    {
        if (observation is null) throw new ServiceException(ServiceError.InvalidInput, "A native save observation is required.");
        expectedBackendId = Ids.Slug(expectedBackendId);
        expectedBackendRevision = Ids.Hex(expectedBackendRevision, 40, "Expected backend revision");
        var backend = Ids.Slug(observation.BackendId);
        var revision = Ids.Hex(observation.BackendRevision, 40, "Observed backend revision");
        if (backend != expectedBackendId || revision != expectedBackendRevision || revision.All(c => c == '0'))
            throw new ServiceException(ServiceError.Conflict, "The save observation belongs to a different or unknown backend revision.");
        if (!observation.Existing || !observation.AdapterVerified)
            throw new ServiceException(ServiceError.UnverifiedSaveMapping, "Only an existing save directory resolved by the actual native adapter can become a backup mapping.");
        var identity = new SaveIdentity(observation.TitleId, observation.UserId, backend, observation.FormatId, observation.FormatVersion).Canonical();
        if (identity.TitleId.All(c => c == '0') || observation.Type is not ("account" or "device") ||
            (observation.Type == "account" && identity.UserId.All(c => c == '0')))
            throw new ServiceException(ServiceError.InvalidInput, "The native save title/user/type identity is invalid or unsupported.");
        var saveId = Ids.Hex(observation.SaveDataId, 16, "Native save data ID");
        var relative = Paths.Relative(observation.RelativeDirectory);
        if (string.IsNullOrWhiteSpace(observation.Provenance) || observation.Provenance.Length > 512 || observation.Provenance.Any(char.IsControl))
            throw new ServiceException(ServiceError.InvalidInput, "Native save discovery provenance is required.");
        var evidence = $"native-query backend={backend} revision={revision} saveDataId={saveId} type={observation.Type}; {observation.Provenance.Trim()}; gameplayVerified=false";
        return new(identity, relative, SaveMappingVerification.AdapterVerified, evidence);
    }
}
