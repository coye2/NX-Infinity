namespace NxInfinity.Runtime.Services;

public enum BackendQualification { Qualified, Experimental, Unavailable }
public enum BackendSelectionMode { QualifiedOnly, ExperimentalOptIn }
public sealed record BackendDefinition(string Id, string Version, BackendQualification Qualification);
public sealed record BackendSelection(string BackendId, BackendSelectionMode Mode = BackendSelectionMode.QualifiedOnly);
public sealed record ResolvedBackendRoute(BackendDefinition Backend, BackendSelection Selection, string Source);
public sealed record ProfileSnapshot(int SchemaVersion, BackendSelection? Global, IReadOnlyDictionary<string, BackendSelection> Titles);

/// <summary>Persists explicit stable backend IDs; resolution never substitutes another backend.</summary>
public sealed class ProfileRouter
{
    private readonly NxWorkspace _workspace;
    private readonly IReadOnlyDictionary<string, BackendDefinition> _catalog;
    private readonly string _path;

    public ProfileRouter(NxWorkspace workspace, IEnumerable<BackendDefinition> availableBackends)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        var catalog = new Dictionary<string, BackendDefinition>(StringComparer.Ordinal);
        foreach (var backend in availableBackends)
        {
            Ids.Slug(backend.Id);
            if (string.IsNullOrWhiteSpace(backend.Version) || !Enum.IsDefined(backend.Qualification) || !catalog.TryAdd(backend.Id, backend))
                throw new ServiceException(ServiceError.InvalidInput, "Backend catalog entries require a version, valid qualification, and unique stable ID.");
        }
        _catalog = catalog;
        _path = Paths.Under(_workspace.DataPath, "profiles.json");
    }

    public ProfileSnapshot Read()
    {
        using var lease = _workspace.Acquire("profiles");
        return ReadUnlocked();
    }

    /// <summary>Retires emulator routing while retaining the previous route document.</summary>
    public void UseNxiCore()
    {
        if (!_catalog.TryGetValue("nxi", out var core)) return;
        var selection = new BackendSelection("nxi", core.Qualification == BackendQualification.Qualified
            ? BackendSelectionMode.QualifiedOnly : BackendSelectionMode.ExperimentalOptIn);
        using var lease = _workspace.Acquire("profiles");
        var state = ReadUnlocked();
        if (state.Global == selection && state.Titles.Count == 0) return;
        string backup = _path + ".before-nxi.json";
        Paths.RejectLinks(backup);
        if (File.Exists(_path) && !File.Exists(backup)) File.Copy(_path, backup);
        StoreIo.AtomicJson(_path, new ProfileSnapshot(1, selection, new Dictionary<string, BackendSelection>()));
    }

    public void SetGlobal(BackendSelection selection)
    {
        ValidateSelection(selection, resolve: true);
        using var lease = _workspace.Acquire("profiles");
        var state = ReadUnlocked();
        StoreIo.AtomicJson(_path, state with { Global = selection });
    }

    public void SetTitleOverride(string titleId, BackendSelection selection)
    {
        titleId = Ids.Hex(titleId, 16, "Title ID");
        ValidateSelection(selection, resolve: true);
        using var lease = _workspace.Acquire("profiles");
        var state = ReadUnlocked();
        var titles = new Dictionary<string, BackendSelection>(state.Titles, StringComparer.Ordinal) { [titleId] = selection };
        StoreIo.AtomicJson(_path, state with { Titles = titles });
    }

    public bool ClearTitleOverride(string titleId)
    {
        titleId = Ids.Hex(titleId, 16, "Title ID");
        using var lease = _workspace.Acquire("profiles");
        var state = ReadUnlocked();
        var titles = new Dictionary<string, BackendSelection>(state.Titles, StringComparer.Ordinal);
        if (!titles.Remove(titleId)) return false;
        StoreIo.AtomicJson(_path, state with { Titles = titles });
        return true;
    }

    public ResolvedBackendRoute Resolve(string titleId)
    {
        titleId = Ids.Hex(titleId, 16, "Title ID");
        using var lease = _workspace.Acquire("profiles");
        var state = ReadUnlocked();
        var hasTitle = state.Titles.TryGetValue(titleId, out var selection);
        selection ??= state.Global;
        if (selection is null)
            throw new ServiceException(ServiceError.NotFound, "No backend is explicitly selected for this title or globally.");
        var backend = ValidateSelection(selection, resolve: true)!;
        return new ResolvedBackendRoute(backend, selection, hasTitle ? "title" : "global");
    }

    private ProfileSnapshot ReadUnlocked()
    {
        if (!File.Exists(_path)) return new ProfileSnapshot(1, null, new Dictionary<string, BackendSelection>());
        var state = StoreIo.ReadJson<ProfileSnapshot>(_path);
        if (state.SchemaVersion != 1 || state.Titles is null || state.Titles.Count > 100000)
            throw new ServiceException(ServiceError.CorruptData, "The profile store schema or title map is invalid.");
        if (state.Global is not null) ValidateSelection(state.Global, resolve: false);
        foreach (var entry in state.Titles)
        {
            if (Ids.Hex(entry.Key, 16, "Title ID") != entry.Key)
                throw new ServiceException(ServiceError.CorruptData, "Stored title IDs must be canonical uppercase hexadecimal.");
            ValidateSelection(entry.Value, resolve: false);
        }
        return state;
    }

    private BackendDefinition? ValidateSelection(BackendSelection? selection, bool resolve)
    {
        if (selection is null || !Enum.IsDefined(selection.Mode))
            throw new ServiceException(ServiceError.CorruptData, "A backend selection is invalid.");
        Ids.Slug(selection.BackendId);
        if (!resolve) return null;
        if (!_catalog.TryGetValue(selection.BackendId, out var backend) || backend.Qualification == BackendQualification.Unavailable)
            throw new ServiceException(ServiceError.Unavailable, $"Selected backend '{selection.BackendId}' is unavailable; no fallback was selected.");
        if (backend.Qualification == BackendQualification.Experimental && selection.Mode != BackendSelectionMode.ExperimentalOptIn)
            throw new ServiceException(ServiceError.ExperimentalOptInRequired, $"Backend '{selection.BackendId}' requires an explicit experimental selection.");
        return backend;
    }
}
