using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using LibHac.Common;
using LibHac.Common.FixedArrays;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Fs.Shim;
using LibHac.FsSystem;
using LibHac.Loader;
using LibHac.Ncm;
using LibHac.Ns;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Tools.Ncm;
using LibHac.Tools.Ro;
using Nx.Backends.Diagnostics;
using Nx.Backends.Performance;
using Nx.Networking;
using NxInfinity.Runtime;
using Ryujinx.Audio.Backends.OpenAL;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Multiplayer;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Common.Utilities;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.Texture;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.HLE;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService.ApplicationProxy.Types;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.HOS.Services.Hid.Types.SharedMemory.TouchScreen;
using Ryujinx.HLE.HOS.SystemState;
using Ryujinx.HLE.Loaders.Processes;
using Ryujinx.HLE.Loaders.Processes.Extensions;
using Ryujinx.HLE.Utilities;
using SDL;
using Silk.NET.Vulkan;

namespace NxInfinity.Core;

internal sealed class CoreSession : IDisposable
{
    private sealed record Issue(string Code, string Severity, string Message);

    private sealed record Artwork(string MimeType, string DataBase64);

    private sealed record Entry(string TitleId, string Kind, string Name, string ContentId, string Version);

    private sealed class InspectedContent
    {
        public string ContentPath { get; set; }

        public string Format { get; set; }

        public string Kind { get; set; }

        public string TitleId { get; set; }

        public string NacpTitleId { get; set; }

        public string RuntimeProgramId { get; set; }

        public string Name { get; set; }

        public string Publisher { get; set; }

        public string DisplayVersion { get; set; }

        public string BuildId { get; set; }

        public string BuildIdSource { get; set; }

        public bool IdentityVerified { get; set; }

        public Artwork Artwork { get; set; }

        public List<Issue> Issues { get; } = new List<Issue>();

        public List<Entry> Entries { get; } = new List<Entry>();

        public string NativeLoaderStatus { get; set; } = "not_inspected";

        internal string NativeSelectedUpdatePath { get; set; }
    }

    private sealed record ContentConfigurationProof(string Path, string Sha256, long Bytes);

    private sealed record ContentConfigurationReceipt(string TitleId, ContentConfigurationProof Update, ContentConfigurationProof[] Dlc);

    private sealed record NativeMount(string Id, string SourcePath, bool Enabled);

    private sealed record MountedFile(string RelativePath, long Bytes, string Sha256);

    private sealed record BoundMount(NativeMount Mount, string NativePath, MountedFile[] Files);

    private sealed record ModConfiguration(int Version, string TitleId, string ExpectedBuildId, string BackendRevision, BoundMount[] Mounts);

    private sealed class PhysicalGamepad
    {
        public uint Id;

        public unsafe SDL_Gamepad* Handle;

        public string LastUiState;

        public bool GuideHeld;
    }

    private string _profileRoot;

    private LibHacHorizonManager _horizon;

    private ContentManager _contentManager;

    private AccountManager _accounts;

    private readonly Dictionary<ulong, List<string>> _configurationObserved = new Dictionary<ulong, List<string>>();

    private static readonly JsonSerializerOptions NativeJson = JsonHelper.GetDefaultSerializerOptions();

    private static readonly JsonSerializerOptions HostJson = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public const string Revision = "67591deee07ab2ce86fc9b72c42e8b64c95ba1b3";

    private readonly Protocol _protocol;

    private readonly WorkerDiagnostics _diagnostics;

    private readonly WorkerTeardown _teardown = new WorkerTeardown();

    private string _teardownPhase = "teardown.not_started";

    private long _guestStartedAt;

    private readonly ConcurrentQueue<Action> _gpuActions = new ConcurrentQueue<Action>();

    private readonly ConcurrentQueue<Action> _hostActions = new ConcurrentQueue<Action>();

    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

    private readonly ManualResetEventSlim _gpuReady = new ManualResetEventSlim(initialState: false);

    private readonly ManualResetEventSlim _gpuStart = new ManualResetEventSlim(initialState: false);

    private readonly ManualResetEventSlim _gpuLoopStopped = new ManualResetEventSlim(initialState: false);

    private readonly ManualResetEventSlim _gpuMayDispose = new ManualResetEventSlim(initialState: false);

    private readonly ManualResetEventSlim _gpuDisposed = new ManualResetEventSlim(initialState: false);

    private Thread _gpuThread;

    private volatile bool _renderActive;

    private Exception _gpuError;

    private Exception _gpuDisposalError;

    private bool _constructingCore;

    private bool _sdlInitialized;

    private unsafe SDL_Window* _window;

    private Ryujinx.HLE.Switch _device;

    private ProcessResult _applicationProcess;

    private ulong? _runtimeProgramId;

    private VirtualFileSystem _vfs;

    private VulkanRenderer _renderer;

    private MeteredAudioDriver _audio;

    private FileStream _runtimeLease;

    private string _state = "created";

    private long _presentedFrames;

    private readonly PresentTimingRecorder _presentTiming = new PresentTimingRecorder();

    private long _lastInputAt;

    private readonly GamepadInput[] _input = new GamepadInput[8];

    private readonly bool[] _configuredPlayers = new bool[8];

    private readonly bool[] _remotePlayers = new bool[8];

    private readonly Dictionary<string, object> _requested = new Dictionary<string, object>();

    private volatile bool _requiresProcessExit;

    private static readonly Dictionary<string, ControllerKeys> ButtonNames = new Dictionary<string, ControllerKeys>(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = ControllerKeys.A,
        ["b"] = ControllerKeys.B,
        ["x"] = ControllerKeys.X,
        ["y"] = ControllerKeys.Y,
        ["l"] = ControllerKeys.L,
        ["r"] = ControllerKeys.R,
        ["zl"] = ControllerKeys.Zl,
        ["zr"] = ControllerKeys.Zr,
        ["plus"] = ControllerKeys.Plus,
        ["minus"] = ControllerKeys.Minus,
        ["left_stick"] = ControllerKeys.LStick,
        ["right_stick"] = ControllerKeys.RStick,
        ["dpad_up"] = ControllerKeys.DpadUp,
        ["dpad_down"] = ControllerKeys.DpadDown,
        ["dpad_left"] = ControllerKeys.DpadLeft,
        ["dpad_right"] = ControllerKeys.DpadRight
    };

    private static readonly JsonSerializerOptions NativeGamePatchJson = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly List<string> _nativeLoadEvidence = new List<string>();

    private readonly object _nativeEvidenceLock = new object();

    private bool _observingNativeLoad;

    private InspectedContent _loadIdentity;

    private string _networkProvider = "offline";

    private string _networkServer = "";

    private string _networkPassphrase = "";

    private string _networkStage = "disabled";

    private string _networkError;

    private readonly object _networkGate = new object();

    private IRenderer _coreRenderer;

    private string _gpuLoopPhase = "initializing";

    private long _gpuLoopPhaseStart;

    private ObservedRenderer _observedRenderer;

    private string _rendererThreading = "threaded_renderer";

    private bool _threadedRunLoopEntered;

    private bool _gpuResourcesDisposed;

    private readonly ShaderCompilationRecorder _shaderCompilation = new ShaderCompilationRecorder();

    private readonly object _presentationGate = new object();

    private readonly ManualResetEventSlim _windowVsyncChanged = new ManualResetEventSlim(initialState: false);

    private string _windowVsyncMode;

    private long _windowVsyncSetterCalls;

    private static readonly IReadOnlyDictionary<string, string> CompiledPerformancePolicies = ReadCompiledPerformancePolicies();

    private readonly UserChannelPersistence _programChannel;

    private readonly Dictionary<string, (string Method, JsonElement Parameters)> _programReplay = new Dictionary<string, (string, JsonElement)>();

    private JsonElement _programInitialize;

    private JsonElement _programLoad;

    private bool _suppressTransitionStates;

    private bool _programWasPaused;

    private bool _programBootstrap;

    private bool _preserveLeaseForProgramTransition;

    private bool _touchPointer;

    private bool _touchToggleHeld;

    private bool _touchDown;

    private double _touchX = 640.0;

    private double _touchY = 360.0;

    private long _touchAt;

    private readonly Dictionary<uint, PhysicalGamepad> _gamepads = new Dictionary<uint, PhysicalGamepad>();

    private readonly Dictionary<int, uint> _physicalPlayers = new Dictionary<int, uint>();

    private string _inputTarget = "ui";

    private bool _autoConnectController = true;

    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

    private int _interactionInputCaptureRequested;

    private bool? _fullscreenRequested;

    private bool? _fullscreenRequestAccepted;

    private bool? _fullscreenSynchronizationCompleted;

    private bool _windowStateSynchronized;

    private volatile bool _windowPresentationSuspended;

    private bool _windowVisible;

    private bool _windowMinimized;

    private readonly WindowSizeMailbox _windowPixelSize = new WindowSizeMailbox();

    private long _windowSizeSetterCalls;

    private long _windowEventsDispatched;

    private long _windowEventBudgetYields;

    public Interactions Interactions { get; }

    public bool Disposed => _state == "disposed";

    internal string DiagnosticState => _state;

    public bool RequiresProcessExit
    {
        get
        {
            return _requiresProcessExit;
        }
        private set
        {
            _requiresProcessExit = value;
        }
    }

    private MultiplayerMode NativeMultiplayerMode
    {
        get
        {
            if (!(_networkProvider == "ryuldn"))
            {
                return MultiplayerMode.Disabled;
            }
            return MultiplayerMode.LdnRyu;
        }
    }

    private string EffectiveRendererThreading
    {
        get
        {
            if (!(_device?.Gpu.Renderer is ThreadedRenderer))
            {
                return "single_gpu_thread";
            }
            return "threaded_renderer";
        }
    }

    public bool ProgramTransitionPending { get; private set; }

    public bool PendingWindowEvents { get; private set; }

    private static string AbsolutePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !System.IO.Path.IsPathFullyQualified(value))
        {
            throw new NxError("invalid_argument", "An absolute local path is required.");
        }
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(value));
    }

    private static void RejectLinks(string path)
    {
        string text = path;
        while (!string.IsNullOrEmpty(text))
        {
            if ((File.Exists(text) || Directory.Exists(text)) && (File.GetAttributes(text) & FileAttributes.ReparsePoint) != 0)
            {
                throw new NxError("invalid_argument", "Symbolic links and reparse points are not accepted in managed profile paths.");
            }
            text = System.IO.Path.GetDirectoryName(text);
        }
    }

    private static void CheckProfileMarkers(string root)
    {
        string[] array = new string[4] { ".nx-save-transaction.json", ".nx-firmware-transaction.json", ".nx-mod-transaction.json", ".nx-content-transaction.json" };
        foreach (string text in array)
        {
            if (File.Exists(System.IO.Path.Combine(root, text)))
            {
                throw new NxError("invalid_state", "An interrupted profile transaction requires recovery: " + text);
            }
        }
    }

    private FileStream AcquireProfileLease(string root)
    {
        RejectLinks(root);
        Directory.CreateDirectory(root);
        CheckProfileMarkers(root);
        FileStream fileStream;
        try
        {
            fileStream = new FileStream(System.IO.Path.Combine(root, ".nx-runtime.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new NxError("invalid_state", "The backend profile is already in use by a runtime or save operation.");
        }
        try
        {
            CheckProfileMarkers(root);
            return fileStream;
        }
        catch
        {
            fileStream.Dispose();
            throw;
        }
    }

    private void EnsureFilesystem(string root)
    {
        root = AbsolutePath(root);
        if (_profileRoot != null && !string.Equals(root, _profileRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new NxError("invalid_state", "This worker is bound to a different profile. Create a fresh worker for another profile.");
        }
        if (_vfs == null)
        {
            _profileRoot = root;
            AppDataManager.Initialize(root);
            if (!string.Equals(System.IO.Path.GetFullPath(AppDataManager.BaseDirPath), root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new NxError("core_error", "The core refused the isolated profile directory.");
            }
            _vfs = VirtualFileSystem.CreateInstance();
            _contentManager = new ContentManager(_vfs);
        }
    }

    private T WithProfile<T>(JsonElement p, Func<T> action)
    {
        Require("created", "initialized", "loaded", "running", "paused");
        string root = AbsolutePath(((p.TryGetProperty("dataRoot", out var value) && value.ValueKind != JsonValueKind.Null) ? Fields.RequiredString(p, "dataRoot") : _profileRoot) ?? throw new NxError("invalid_argument", "dataRoot is required before initialization for this metadata operation."));
        if (_runtimeLease != null)
        {
            EnsureFilesystem(root);
            return action();
        }
        using (AcquireProfileLease(root))
        {
            EnsureFilesystem(root);
            return action();
        }
    }

    private static ulong ReadTitle(JsonElement p)
    {
        return ParseTitle(Fields.RequiredString(p, "titleId"));
    }

    private static ulong ParseTitle(string value)
    {
        if (value.Length != 16 || !ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var result) || result == 0L)
        {
            throw new NxError("invalid_argument", "A nonzero exact 16-hex title ID is required.");
        }
        return result;
    }

    private static string Hex(ulong value)
    {
        if (value != 0L)
        {
            return value.ToString("X16", CultureInfo.InvariantCulture);
        }
        return null;
    }

    private object InspectContent(JsonElement p)
    {
        Require("created", "initialized", "loaded", "running", "paused");
        string path = AbsolutePath(Fields.RequiredString(p, "contentPath"));
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new NxError("invalid_argument", "The selected content does not exist.");
        }
        bool artwork = Fields.Boolean(p, "includeArtwork", fallback: false);
        string format = (Directory.Exists(path) ? "exefs" : System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant());
        InspectedContent inspectedContent = new InspectedContent();
        inspectedContent.ContentPath = path;
        inspectedContent.Format = format;
        InspectedContent inspectedContent2 = inspectedContent;
        string text = format;
        bool flag = ((text == "nro" || text == "nso") ? true : false);
        inspectedContent2.Kind = (flag ? "homebrew" : "unknown");
        InspectedContent result = inspectedContent;
        try
        {
            if (format == "nro")
            {
                InspectNro(path, artwork, result);
            }
            else if (format == "nso")
            {
                using LocalStorage storage = new LocalStorage(path, FileAccess.Read);
                using IFile file = storage.AsFile(OpenMode.Read);
                result.BuildId = ReadNsoBuild(file);
                result.BuildIdSource = ((result.BuildId == null) ? null : "nso_header");
                result.NativeLoaderStatus = "native_header_parsed";
                result.Issues.Add(new Issue("title_identity_unavailable", "info", "A standalone NSO does not provide a verified application title ID."));
            }
            else if (format == "exefs")
            {
                RejectLinks(path);
                using IFileSystem fs = new LocalFileSystem(path);
                InspectExeFs(fs, result);
                result.Kind = "application";
                result.NativeLoaderStatus = "native_exefs_parsed";
            }
            else
            {
                switch (format)
                {
                    case "nsp":
                    case "pfs0":
                    case "xci":
                    case "nca":
                        flag = true;
                        break;
                    default:
                        flag = false;
                        break;
                }
                if (!flag)
                {
                    throw new NxError("unsupported", "Native inspection supports NRO, NSO, ExeFS, NSP/PFS0, XCI and NCA.");
                }
                WithProfile(p, () =>
                {
                    InspectEncrypted(path, format, artwork, result, selectedUpdate: true);
                    return true;
                });
            }
        }
        catch (NxError nxError) when (nxError.Code == "missing_dependency")
        {
            result.Issues.Add(new Issue(nxError.Code, "error", nxError.Message));
            result.NativeLoaderStatus = "missing_dependency";
        }
        catch (NxError)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.NativeLoaderStatus = "native_parser_rejected";
            result.IdentityVerified = false;
            result.Issues.Add(new Issue("native_parser_rejected", "error", "The pinned native metadata parser rejected or could not decrypt this content (" + ex.GetType().Name + ")."));
        }
        result.Issues.Add(new Issue("inspection_only", "info", "Parsed metadata is not proof of gameplay compatibility, unmodified content or a successful guest boot."));
        return result;
    }

    private static void ReadAt(Stream stream, long position, Span<byte> bytes)
    {
        if (position < 0 || position > stream.Length - bytes.Length)
        {
            throw new InvalidDataException("Content section exceeds file bounds.");
        }
        stream.Position = position;
        stream.ReadExactly(bytes);
    }

    private static void InspectNro(string path, bool artwork, InspectedContent result)
    {
        using FileStream fileStream = File.OpenRead(path);
        Span<byte> bytes = stackalloc byte[128];
        ReadAt(fileStream, 0L, bytes);
        if (!bytes.Slice(16, 4).SequenceEqual("NRO0"u8))
        {
            throw new InvalidDataException("Invalid NRO header.");
        }
        uint num = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(24));
        if (num < 128 || num > fileStream.Length)
        {
            throw new InvalidDataException("Invalid NRO file size.");
        }
        for (int i = 0; i < 3; i++)
        {
            uint num2 = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(32 + i * 8));
            uint num3 = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(36 + i * 8));
            if (num2 > num || num3 > num - num2)
            {
                throw new InvalidDataException("NRO segment exceeds its file size.");
            }
        }
        new Nro(fileStream.AsStorage());
        result.NativeLoaderStatus = "native_nro_parsed";
        result.BuildIdSource = null;
        result.Issues.Add(new Issue("main_nso_identity_unavailable", "info", "NRO content has no main NSO identity. Its NRO module header is not used for exact commercial-build matching."));
        if (num > fileStream.Length - 56)
        {
            return;
        }
        Span<byte> bytes2 = stackalloc byte[56];
        ReadAt(fileStream, num, bytes2);
        if (!bytes2.Slice(0, 4).SequenceEqual("ASET"u8))
        {
            return;
        }
        for (int j = 0; j < 3; j++)
        {
            ulong num4 = BinaryPrimitives.ReadUInt64LittleEndian(bytes2.Slice(8 + j * 16));
            ulong num5 = BinaryPrimitives.ReadUInt64LittleEndian(bytes2.Slice(16 + j * 16));
            if (num4 > (ulong)(fileStream.Length - num) || num5 > (ulong)(fileStream.Length - num - (long)num4))
            {
                throw new InvalidDataException("NRO asset exceeds file bounds.");
            }
            if (((j == 0) & artwork) && num5 != 0L && num5 <= 262144)
            {
                byte[] array = new byte[(uint)num5];
                ReadAt(fileStream, checked(num + (long)num4), array);
                result.Artwork = DecodeArtwork(array);
            }
            if (j == 1 && num5 != 0L)
            {
                BlitStruct<ApplicationControlProperty> nacp = new BlitStruct<ApplicationControlProperty>(1);
                if (num5 != (ulong)nacp.ByteSpan.Length)
                {
                    throw new InvalidDataException("NACP size does not match the native structure.");
                }
                ReadAt(fileStream, checked(num + (long)num4), nacp.ByteSpan);
                ApplyNacp(nacp, result);
            }
        }
        result.IdentityVerified = false;
    }

    private static string ReadNsoBuild(IFile file)
    {
        NsoReader nsoReader = new NsoReader();
        nsoReader.Initialize(file).ThrowIfFailure();
        byte[] array = nsoReader.Header.ModuleId.AsReadOnlySpan().ToArray();
        if (!array.All((byte x) => x == 0))
        {
            return Convert.ToHexString(array);
        }
        return null;
    }

    private static void InspectExeFs(IFileSystem fs, InspectedContent result)
    {
        using UniqueRef<IFile> uniqueRef = default;
        fs.OpenFile(ref uniqueRef.Ref, "/main".ToU8Span(), OpenMode.Read).ThrowIfFailure();
        result.BuildId = ReadNsoBuild(uniqueRef.Get);
        result.BuildIdSource = ((result.BuildId == null) ? null : "main_nso");
        using UniqueRef<IFile> uniqueRef2 = default;
        if (fs.OpenFile(ref uniqueRef2.Ref, "/main.npdm".ToU8Span(), OpenMode.Read).IsSuccess())
        {
            MetaLoader metaLoader = new MetaLoader();
            uniqueRef2.Get.GetSize(out var size).ThrowIfFailure();
            if ((size <= 0 || size > 1048576) ? true : false)
            {
                throw new InvalidDataException("Invalid NPDM metadata size.");
            }
            byte[] array = new byte[(int)size];
            uniqueRef2.Get.Read(out var bytesRead, 0L, array).ThrowIfFailure();
            if (bytesRead != size)
            {
                throw new EndOfStreamException();
            }
            metaLoader.Load(array).ThrowIfFailure();
            result.TitleId = Hex(metaLoader.GetProgramId());
        }
        result.IdentityVerified = result.TitleId != null && result.BuildId != null;
        using UniqueRef<IFile> uniqueRef3 = default;
        if (fs.OpenFile(ref uniqueRef3.Ref, "/control.nacp".ToU8Span(), OpenMode.Read).IsSuccess())
        {
            BlitStruct<ApplicationControlProperty> nacp = new BlitStruct<ApplicationControlProperty>(1);
            uniqueRef3.Get.GetSize(out var size2).ThrowIfFailure();
            if (size2 != nacp.ByteSpan.Length)
            {
                throw new InvalidDataException("Invalid companion NACP size.");
            }
            uniqueRef3.Get.Read(out var bytesRead2, 0L, nacp.ByteSpan).ThrowIfFailure();
            if (bytesRead2 != size2)
            {
                throw new EndOfStreamException();
            }
            ApplyNacp(nacp, result);
        }
    }

    private void InspectEncrypted(string path, string format, bool artwork, InspectedContent result, bool selectedUpdate = false)
    {
        if (_vfs.KeySet.HeaderKey.IsZeros())
        {
            throw new NxError("missing_dependency", "User-supplied usable NCA header keys are absent from this isolated profile.");
        }
        using FileStream stream = File.OpenRead(path);
        if (format == "nca")
        {
            Nca nca = new Nca(_vfs.KeySet, stream.AsStorage());
            result.TitleId = Hex(nca.Header.TitleId);
            InspectedContent inspectedContent = result;
            string kind;
            if (nca.IsPatch())
            {
                kind = "update";
            }
            else
            {
                kind = (nca.IsProgram() ? "application" : nca.Header.ContentType.ToString().ToLowerInvariant());
            }
            inspectedContent.Kind = kind;
            if (nca.IsMain() && nca.CanOpenSection(NcaSectionType.Code))
            {
                using IFileSystem fs = nca.OpenFileSystem(NcaSectionType.Code, IntegrityCheckLevel.ErrorOnInvalid);
                InspectExeFs(fs, result);
            }
            if (nca.IsControl())
            {
                ReadControl(nca, artwork, result);
            }
            result.NativeLoaderStatus = "native_nca_parsed";
            return;
        }
        using IFileSystem pfs = PartitionFileSystemUtils.OpenApplicationFileSystem(stream, format == "xci", _vfs);
        ContentMetaData contentMetaData = null;
        ContentMetaType[] array = new ContentMetaType[3]
        {
            ContentMetaType.Application,
            ContentMetaType.Patch,
            ContentMetaType.AddOnContent
        };
        foreach (ContentMetaType contentMetaType in array)
        {
            foreach (ContentMetaData item in ReadAllMetadata(pfs, contentMetaType))
            {
                if (result.Entries.Count >= 4096)
                {
                    throw new InvalidDataException("Container has too many application entries.");
                }
                result.Entries.Add(new Entry(Hex(item.Id), contentMetaType switch
                {
                    ContentMetaType.Patch => "update",
                    ContentMetaType.Application => "application",
                    _ => "dlc",
                }, null, null, item.Version.ToString()));
                if (contentMetaData == null)
                {
                    contentMetaData = item;
                }
            }
        }
        if (contentMetaData == null)
        {
            result.Issues.Add(new Issue("application_metadata_missing", "error", "No native application/update/DLC CNMT metadata was found in the container."));
            result.NativeLoaderStatus = "native_container_without_application_metadata";
            return;
        }
        result.TitleId = Hex(contentMetaData.Id);
        InspectedContent inspectedContent2 = result;
        string kind2;
        if (contentMetaData.Type == ContentMetaType.Application)
        {
            kind2 = "application";
        }
        else
        {
            kind2 = ((contentMetaData.Type == ContentMetaType.Patch) ? "update" : "dlc");
        }
        inspectedContent2.Kind = kind2;
        Nca nca2 = contentMetaData.GetNcaByType(_vfs.KeySet, LibHac.Ncm.ContentType.Control);
        Nca nca3 = nca2;
        Nca ncaByType = contentMetaData.GetNcaByType(_vfs.KeySet, LibHac.Ncm.ContentType.Program);
        Nca nca4 = null;
        if (selectedUpdate && ncaByType != null && ncaByType.IsMain())
        {
            VerifyContentFilesUnchanged(ncaByType.GetProgramIdBase());
            Nca nca5;
            (nca4, nca5) = ncaByType.GetUpdateData(_vfs, IntegrityCheckLevel.ErrorOnInvalid, 0, out var updatePath);
            if (nca4 != null)
            {
                result.NativeSelectedUpdatePath = updatePath;
                result.Issues.Add(new Issue("configured_update_selected", "info", "Identity and display metadata include the exact configured update selected by the native loader. Guest behavior remains unverified."));
            }
            if (nca5 != null)
            {
                nca2 = nca5;
            }
        }
        if (nca2 != null)
        {
            ReadControl(nca2, artwork, result, (nca2 == nca3) ? null : nca3);
        }
        if (ncaByType != null && ncaByType.IsMain() && ncaByType.CanOpenSection(NcaSectionType.Code))
        {
            using IFileSystem fs2 = ((nca4 == null) ? ncaByType.OpenFileSystem(NcaSectionType.Code, IntegrityCheckLevel.ErrorOnInvalid) : ncaByType.OpenFileSystemWithPatch(nca4, NcaSectionType.Code, IntegrityCheckLevel.ErrorOnInvalid));
            InspectExeFs(fs2, result);
        }
        else
        {
            result.IdentityVerified = result.TitleId != null;
        }
        result.NativeLoaderStatus = "native_container_parsed";
    }

    private IEnumerable<ContentMetaData> ReadAllMetadata(IFileSystem pfs, ContentMetaType type)
    {
        _vfs.ImportTickets(pfs);
        int count = 0;
        foreach (DirectoryEntryEx item in pfs.EnumerateEntries("/", "*.cnmt.nca"))
        {
            int num = count + 1;
            count = num;
            if (num > 4096)
            {
                throw new InvalidDataException("Container metadata count exceeds the protocol limit.");
            }
            Cnmt cnmt = pfs.GetNca(_vfs.KeySet, item.FullPath).GetCnmt(IntegrityCheckLevel.ErrorOnInvalid, type);
            if (cnmt != null && cnmt.Type == type)
            {
                yield return new ContentMetaData(pfs, cnmt);
            }
        }
    }

    private InspectedContent InspectForLoad(string path)
    {
        string text = (Directory.Exists(path) ? "exefs" : System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant());
        InspectedContent result = new InspectedContent
        {
            ContentPath = path,
            Format = text
        };
        try
        {
            if (text == "nro")
            {
                InspectNro(path, artwork: false, result);
            }
            else if (text == "exefs")
            {
                using IFileSystem fs = new LocalFileSystem(path);
                InspectExeFs(fs, result);
            }
            else if (text != "nso")
            {
                InspectEncrypted(path, text, artwork: false, result, selectedUpdate: true);
            }
        }
        catch (NxError)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NxError("invalid_argument", "Native pre-load content identity could not be established (" + ex.GetType().Name + "). No guest process was started.");
        }
        return result;
    }

    private static void ReadControl(Nca control, bool artwork, InspectedContent result, Nca fallbackControl = null)
    {
        using IFileSystem fs = control.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);
        ReadControlFileSystem(fs, artwork, result, (fallbackControl == null) ? null : ((Func<IEnumerable<Func<byte[]>>>)(() => ReadControlImages(fallbackControl))));
    }

    private static void ReadControlFileSystem(IFileSystem fs, bool artwork, InspectedContent result, Func<IEnumerable<Func<byte[]>>> fallbackArtwork = null)
    {
        using UniqueRef<IFile> uniqueRef = default;
        if (fs.OpenFile(ref uniqueRef.Ref, "/control.nacp".ToU8Span(), OpenMode.Read).IsSuccess())
        {
            BlitStruct<ApplicationControlProperty> nacp = new BlitStruct<ApplicationControlProperty>(1);
            uniqueRef.Get.GetSize(out var size).ThrowIfFailure();
            if (size != nacp.ByteSpan.Length)
            {
                throw new InvalidDataException("Invalid native NACP size.");
            }
            uniqueRef.Get.Read(out var bytesRead, 0L, nacp.ByteSpan).ThrowIfFailure();
            if (bytesRead != size)
            {
                throw new EndOfStreamException();
            }
            ApplyNacp(nacp, result);
        }
        if (artwork)
        {
            NativeArtwork.Image image = NativeArtwork.Select(() => ReadControlImages(fs), fallbackArtwork);
            result.Artwork = (((object)image == null) ? null : new Artwork(image.MimeType, image.DataBase64));
        }
    }

    private static IEnumerable<Func<byte[]>> ReadControlImages(Nca control)
    {
        using IFileSystem fs = control.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);
        foreach (Func<byte[]> item in ReadControlImages(fs))
        {
            yield return item;
        }
    }

    private static IEnumerable<Func<byte[]>> ReadControlImages(IFileSystem fs)
    {
        foreach (DirectoryEntryEx item in (from x in fs.EnumerateEntries("/", "icon_*.dat").Take(64)
                                           orderby x.Name != "icon_AmericanEnglish.dat"
                                           select x).ThenBy((DirectoryEntryEx x) => x.Name, StringComparer.Ordinal))
        {
            string path = item.FullPath;
            yield return () =>
            {
                using UniqueRef<IFile> uniqueRef = default;
                fs.OpenFile(ref uniqueRef.Ref, path.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                uniqueRef.Get.GetSize(out var size).ThrowIfFailure();
                if ((size < 20 || size > 262144) ? true : false)
                {
                    return (byte[])null;
                }
                byte[] array = new byte[(int)size];
                uniqueRef.Get.Read(out var bytesRead, 0L, array).ThrowIfFailure();
                return (bytesRead == size) ? array : null;
            };
        }
    }

    private static Artwork DecodeArtwork(byte[] bytes)
    {
        NativeArtwork.Image image = NativeArtwork.Decode(bytes);
        if ((object)image != null)
        {
            return new Artwork(image.MimeType, image.DataBase64);
        }
        return null;
    }

    private static void ApplyNacp(BlitStruct<ApplicationControlProperty> nacp, InspectedContent result)
    {
        foreach (ApplicationControlProperty.ApplicationTitle applicationTitle in nacp.Value.Title)
        {
            if (result.Name == null && !applicationTitle.NameString.IsEmpty())
            {
                result.Name = applicationTitle.NameString.ToString();
            }
            if (result.Publisher == null && !applicationTitle.PublisherString.IsEmpty())
            {
                result.Publisher = applicationTitle.PublisherString.ToString();
            }
        }
        result.DisplayVersion = nacp.Value.DisplayVersionString.ToString();
        InspectedContent inspectedContent = result;
        ulong value;
        if (nacp.Value.PresenceGroupId != 0L)
        {
            value = nacp.Value.PresenceGroupId;
        }
        else if (nacp.Value.SaveDataOwnerId != 0L)
        {
            value = nacp.Value.SaveDataOwnerId;
        }
        else
        {
            value = ((nacp.Value.AddOnContentBaseId > 4096) ? (nacp.Value.AddOnContentBaseId - 4096) : 0);
        }
        inspectedContent.NacpTitleId = Hex(value);
        if (result.TitleId == null)
        {
            string text = (result.TitleId = result.NacpTitleId);
        }
    }

    private string GameDirectory(ulong title)
    {
        string text = System.IO.Path.Combine(AppDataManager.GamesDirPath, title.ToString("x16"));
        RejectLinks(text);
        return text;
    }

    private T ReadProfileJson<T>(string path, JsonSerializerOptions options = null)
    {
        RejectLinks(path);
        if (new FileInfo(path).Length > 4194304)
        {
            throw new NxError("invalid_argument", "Profile metadata exceeds the 4 MiB safety bound.");
        }
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), options ?? HostJson);
    }

    private object GetContentConfiguration(JsonElement p)
    {
        Require("initialized", "loaded", "running", "paused");
        return ContentConfiguration(ReadTitle(p));
    }

    private object ContentConfiguration(ulong title)
    {
        string path = GameDirectory(title);
        string path2 = System.IO.Path.Combine(path, "updates.json");
        string path3 = System.IO.Path.Combine(path, "dlc.json");
        string text = (File.Exists(path2) ? ReadProfileJson<TitleUpdateMetadata>(path2, NativeJson).Selected : null);
        string[] array = (from x in File.Exists(path3) ? ReadProfileJson<List<DownloadableContentContainer>>(path3, NativeJson) : new List<DownloadableContentContainer>()
                          where x.DownloadableContentNcaList?.Any((DownloadableContentNca n) => n.Enabled) ?? false
                          select x.ContainerPath).Distinct().ToArray();
        List<string> list = new List<string> { "Native games/<title>/updates.json and dlc.json were read from the initialized profile." };
        bool flag = _configurationObserved.TryGetValue(title, out var value);
        if (flag)
        {
            list.AddRange(value);
        }
        else
        {
            list.Add("Configuration is not yet observed by the native loader in this worker.");
        }
        return new
        {
            titleId = Hex(title),
            updatePath = (string.IsNullOrWhiteSpace(text) ? null : text),
            dlcPaths = array,
            configured = (!string.IsNullOrWhiteSpace(text) || array.Length != 0),
            consumed = flag,
            requiresRestart = (_state != "initialized"),
            evidence = list
        };
    }

    private object SetContentConfiguration(JsonElement p)
    {
        Require("initialized");
        ulong title = ReadTitle(p);
        if ((title & 0x1FFF) != 0L)
        {
            throw new NxError("invalid_argument", "Update/DLC configuration requires the base application title ID (low 13 bits zero).");
        }
        string text = ((p.TryGetProperty("updatePath", out var value) && value.ValueKind != JsonValueKind.Null) ? AbsolutePath(Fields.RequiredString(p, "updatePath")) : null);
        if (!p.TryGetProperty("dlcPaths", out var value2) || value2.ValueKind != JsonValueKind.Array || value2.GetArrayLength() > 512)
        {
            throw new NxError("invalid_argument", "dlcPaths must be an array of at most 512 distinct absolute paths.");
        }
        string[] array = value2.EnumerateArray().Select((JsonElement x) =>
        {
            if (x.ValueKind != JsonValueKind.String)
            {
                throw new NxError("invalid_argument", "Each DLC path must be a string.");
            }
            return AbsolutePath(x.GetString());
        }).ToArray();
        if (array.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != array.Length)
        {
            throw new NxError("invalid_argument", "Duplicate DLC source paths are not allowed.");
        }
        List<DownloadableContentContainer> list = new List<DownloadableContentContainer>();
        HashSet<ulong> hashSet = new HashSet<ulong>();
        Span<byte> span = stackalloc byte[16];
        try
        {
            if (text != null)
            {
                ValidateContainerPath(text);
                using IFileSystem pfs = OpenContainer(text);
                ContentMetaData[] array2 = (from x in ReadAllMetadata(pfs, ContentMetaType.Patch)
                                            where x.ProgramBaseId == title
                                            select x).ToArray();
                if (array2.Length != 1)
                {
                    goto IL_01a0;
                }
                Nca ncaByType = array2[0].GetNcaByType(_vfs.KeySet, LibHac.Ncm.ContentType.Program);
                if (ncaByType == null || !ncaByType.IsPatch())
                {
                    goto IL_01a0;
                }
                if (array2[0].GetNcaByType(_vfs.KeySet, LibHac.Ncm.ContentType.Control) == null)
                {
                    throw new NxError("invalid_argument", "Selected update is missing its native control metadata.");
                }
                goto end_IL_014f;
            IL_01a0:
                throw new NxError("invalid_argument", "Selected update has no unique native patch program for this exact base title.");
            end_IL_014f:;
            }
            string[] array3 = array;
            foreach (string text2 in array3)
            {
                ValidateContainerPath(text2);
                using IFileSystem fileSystem = OpenContainer(text2);
                ContentMetaData[] array4 = (from x in ReadAllMetadata(fileSystem, ContentMetaType.AddOnContent)
                                            where x.ProgramBaseId == title
                                            select x).ToArray();
                if (array4.Length == 0)
                {
                    throw new NxError("invalid_argument", "Selected DLC has no native add-on metadata for this exact base title.");
                }
                List<DownloadableContentNca> list2 = new List<DownloadableContentNca>();
                foreach (DirectoryEntryEx item in fileSystem.EnumerateEntries("/", "*.nca"))
                {
                    if (item.Name.EndsWith(".cnmt.nca", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    Nca nca = fileSystem.GetNca(_vfs.KeySet, item.FullPath);
                    if (nca.Header.ContentType != NcaContentType.PublicData || !array4.Any((ContentMetaData x) => x.Id == nca.Header.TitleId))
                    {
                        continue;
                    }
                    if (!hashSet.Add(nca.Header.TitleId))
                    {
                        throw new NxError("invalid_argument", "Duplicate DLC title IDs are selected across containers.");
                    }
                    using IStorage storage = nca.OpenStorage(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);
                    storage.GetSize(out var size).ThrowIfFailure();
                    if (size <= 0)
                    {
                        throw new NxError("invalid_argument", "Selected native DLC data section is empty.");
                    }
                    storage.Read(0L, span.Slice(0, (int)Math.Min(size, 16L))).ThrowIfFailure();
                    list2.Add(new DownloadableContentNca
                    {
                        TitleId = nca.Header.TitleId,
                        FullPath = item.FullPath,
                        Enabled = true
                    });
                }
                if (list2.Count == 0)
                {
                    throw new NxError("invalid_argument", "Selected DLC has no readable native PublicData NCA matching its metadata.");
                }
                list.Add(new DownloadableContentContainer
                {
                    ContainerPath = text2,
                    DownloadableContentNcaList = list2
                });
            }
        }
        catch (NxError)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NxError("invalid_argument", "The native parser could not validate the selected update/DLC (" + ex.GetType().Name + "). Configuration was unchanged.");
        }
        string text3 = GameDirectory(title);
        Directory.CreateDirectory(text3);
        string[] array5 = new string[3]
        {
            System.IO.Path.Combine(text3, "updates.json"),
            System.IO.Path.Combine(text3, "dlc.json"),
            System.IO.Path.Combine(text3, "nx-content.json")
        };
        byte[][] array6 = array5.Select((string x) => (!File.Exists(x)) ? null : File.ReadAllBytes(x)).ToArray();
        string path = System.IO.Path.Combine(_profileRoot, ".nx-content-transaction.json");
        WriteJsonAtomic(path, new
        {
            titleId = Hex(title),
            files = array5.Select((string x) => System.IO.Path.GetRelativePath(_profileRoot, x)).ToArray(),
            originalBase64 = array6.Select((byte[] x) => (x != null) ? Convert.ToBase64String(x) : null).ToArray()
        });
        try
        {
            WriteJsonAtomic(array5[0], new TitleUpdateMetadata
            {
                Selected = (text ?? ""),
                Paths = ((text == null) ? new List<string>() : new List<string> { text })
            }, NativeJson);
            WriteJsonAtomic(array5[1], list, NativeJson);
            WriteJsonAtomic(array5[2], new ContentConfigurationReceipt(Hex(title), (text == null) ? null : ContentProof(text), array.Select(ContentProof).ToArray()));
            File.Delete(path);
        }
        catch
        {
            for (int num2 = 0; num2 < array5.Length; num2++)
            {
                if (array6[num2] == null)
                {
                    File.Delete(array5[num2]);
                }
                else
                {
                    File.WriteAllBytes(array5[num2], array6[num2]);
                }
            }
            File.Delete(path);
            throw;
        }
        _configurationObserved.Remove(title);
        return ContentConfiguration(title);
    }

    private static void ValidateContainerPath(string path)
    {
        RejectLinks(path);
        bool flag = !File.Exists(path);
        if (!flag)
        {
            string text = System.IO.Path.GetExtension(path).ToLowerInvariant();
            bool flag2 = ((text == ".nsp" || text == ".xci") ? true : false);
            flag = !flag2;
        }
        if (flag)
        {
            throw new NxError("invalid_argument", "The native update/DLC loader accepts existing NSP or XCI containers.");
        }
    }

    private IFileSystem OpenContainer(string path)
    {
        return PartitionFileSystemUtils.OpenApplicationFileSystem(path, _vfs);
    }

    private static ContentConfigurationProof ContentProof(string path)
    {
        using FileStream fileStream = File.OpenRead(path);
        return new ContentConfigurationProof(path, Convert.ToHexString(SHA256.HashData(fileStream)), fileStream.Length);
    }

    private void VerifyContentFilesUnchanged(ulong title)
    {
        string path = System.IO.Path.Combine(GameDirectory(title), "nx-content.json");
        if (!File.Exists(path))
        {
            return;
        }
        ContentConfigurationReceipt contentConfigurationReceipt = ReadProfileJson<ContentConfigurationReceipt>(path);
        foreach (ContentConfigurationProof item in from x in contentConfigurationReceipt.Dlc.Prepend(contentConfigurationReceipt.Update)
                                                   where (object)x != null
                                                   select x)
        {
            if (!File.Exists(item.Path) || ContentProof(item.Path) != item)
            {
                throw new NxError("invalid_state", "Configured update/DLC bytes changed since native validation. Re-select and validate the content before loading.");
            }
        }
    }

    private void ObserveLoadedConfiguration(ulong title)
    {
        ulong num = title & 0xFFFFFFFFFFFFE000uL;
        string path = GameDirectory(num);
        string path2 = System.IO.Path.Combine(path, "updates.json");
        string path3 = System.IO.Path.Combine(path, "dlc.json");
        List<string> list = new List<string>();
        string text = (File.Exists(path2) ? ReadProfileJson<TitleUpdateMetadata>(path2, NativeJson).Selected : null);
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (_loadIdentity?.NativeSelectedUpdatePath == null || !string.Equals(_loadIdentity.NativeSelectedUpdatePath, text, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || _loadIdentity.BuildId == null || _loadIdentity.BuildId != _applicationProcess?.MainBuildId || _loadIdentity.DisplayVersion != _applicationProcess?.DisplayVersion)
            {
                return;
            }
            list.Add("The native load completed with its selected update configuration; exact loaded main build and display version are returned by load. Guest-visible update behavior remains unverified.");
        }
        List<DownloadableContentContainer> source = (File.Exists(path3) ? ReadProfileJson<List<DownloadableContentContainer>>(path3, NativeJson) : new List<DownloadableContentContainer>());
        Span<byte> span = stackalloc byte[16];
        foreach (DownloadableContentNca item in from n in source.SelectMany((DownloadableContentContainer c) => c.DownloadableContentNcaList ?? new List<DownloadableContentNca>())
                                                where n.Enabled
                                                select n)
        {
            if (!_contentManager.GetAocTitleIds().Contains(item.TitleId) || !_contentManager.GetAocDataStorage(item.TitleId, out var aocStorage, IntegrityCheckLevel.ErrorOnInvalid))
            {
                return;
            }
            using (aocStorage)
            {
                aocStorage.GetSize(out var size).ThrowIfFailure();
                aocStorage.Read(0L, span.Slice(0, (int)Math.Min(size, 16L))).ThrowIfFailure();
            }
            list.Add("Native ContentManager registered DLC " + Hex(item.TitleId) + " and reopened its actual data storage; guest use is not claimed.");
        }
        if (list.Count > 0)
        {
            _configurationObserved[num] = list;
        }
    }

    public CoreSession(Protocol protocol, WorkerDiagnostics diagnostics = null)
        : this(protocol, diagnostics, null)
    {
    }

    private CoreSession(Protocol protocol, WorkerDiagnostics diagnostics, UserChannelPersistence channel)
    {
        _protocol = protocol;
        _programChannel = channel ?? new UserChannelPersistence();
        _diagnostics = diagnostics ?? new WorkerDiagnostics(Console.Error, "nxi");
        Interactions = new Interactions(protocol, _shutdown.Token, (Action action) =>
        {
            _hostActions.Enqueue(action);
        }, QueueProgramTransition, RequestInteractionInputCapture);
        Logger.Updated += NativeLogObserved;
        for (int num = 0; num < _input.Length; num++)
        {
            _input[num].PlayerId = (PlayerIndex)num;
        }
    }

    private object DispatchCore(string method, JsonElement p)
    {
        return method switch
        {
            "hello" => Hello(),
            "network.configure" => ConfigureNetwork(p),
            "network.get" => NetworkState(),
            "initialize" => Initialize(p),
            "load" => Load(p),
            "start" => Start(),
            "pause" => Pause(value: true),
            "resume" => Pause(value: false),
            "stop" => Stop(),
            "dispose" => DisposeCommand(),
            "telemetry.get" => Telemetry(),
            "frame_rate.get" => FrameRateGet(),
            "frame_rate.set" => FrameRateSet(p),
            "game_patch.prepare" => GamePatchPrepare(p),
            "game_patch.apply" => GamePatchOperation("apply", p),
            "game_patch.rollback" => GamePatchOperation("rollback", p),
            "game_patch.get" => GamePatchOperation("get", p),
            "settings.list" => new
            {
                settings = SettingCatalog.All
            },
            "settings.get" => GetSetting(Fields.RequiredString(p, "id")),
            "settings.set" => SetSetting(p),
            "input.update" => Input(p),
            "input.devices" => InputDevices(),
            "input.assign" => AssignInput(p),
            "input.routing.set" => RouteInput(p),
            "window.get" => WindowState(),
            "window.set" => SetWindow(p),
            "content.inspect" => InspectContent(p),
            "prerequisites.get" => Prerequisites(p),
            "prerequisites.import" => ImportPrerequisite(p),
            "content.configuration.get" => GetContentConfiguration(p),
            "content.configuration.set" => SetContentConfiguration(p),
            "mods.get" => GetMods(p),
            "mods.set" => SetMods(p),
            "saves.list" => NativeSaves(p),
            "nxi.probe" => Probe(),
            _ => throw new NxError("unsupported", "Operation '" + method + "' is not integrated by this adapter."),
        };
    }

    private static object Hello()
    {
        return new
        {
            protocolVersion = 1,
            backendId = "nxi",
            backendName = "NX Infinity Core",
            backendRevision = "67591deee07ab2ce86fc9b72c42e8b64c95ba1b3",
            adapterVersion = "1.0.0-nxi",
            qualification = "unqualified",
            performancePolicies = CompiledPerformancePolicies,
            capabilities = new string[39]
            {
                "network.configure", "network.get", "network.ryuldn.relay", "frame_rate.get", "frame_rate.set", "game_patch.prepare", "game_patch.apply", "game_patch.rollback", "game_patch.get", "game_patch.module_bytes_v1",
                "hello", "initialize", "initialize.renderer_threading", "load", "start", "pause", "resume", "stop", "dispose", "telemetry.get",
                "settings.list", "settings.get", "settings.set", "input.update", "input.devices", "input.assign", "input.routing.set", "window.get", "window.set", "content.inspect",
                "prerequisites.get", "prerequisites.import", "content.configuration.get", "content.configuration.set", "mods.get", "mods.set", "saves.list", "interaction.respond", "nxi.probe"
            },
            limitations = new string[7] { "NX Infinity fork of the pinned Kenji-NX source; game and hardware qualification is reported separately.", "Vulkan worker-owned SDL3 surface and OpenAL output only; no null renderer or embedded HWND.", "No gameplay qualification has been performed; keys, firmware and games are user-supplied.", "Only catalogued settings and basic controller input are integrated; full upstream feature coverage is unfinished.", "One session per worker. Stop/dispose then create a new worker to initialize again.", "RyuLDN relay routing is available when explicitly configured; two-peer retail gameplay is unqualified. Nextendo and other network providers are not integrated.", "Applet prompts and dynamic text are protocol-wired but have not been validated with commercial games." }
        };
    }

    private unsafe object Initialize(JsonElement p)
    {
        Require("created");
        string text = AbsolutePath(Fields.RequiredString(p, "dataRoot"));
        if (Fields.String(p, "graphicsBackend", "vulkan") != "vulkan")
        {
            throw new NxError("unsupported", "This adapter implements the Vulkan renderer only.");
        }
        _rendererThreading = Fields.String(p, "rendererThreading", "threaded_renderer");
        string rendererThreading = _rendererThreading;
        if ((!(rendererThreading == "single_gpu_thread") && !(rendererThreading == "threaded_renderer")) || 1 == 0)
        {
            throw new NxError("invalid_argument", "rendererThreading must be single_gpu_thread or threaded_renderer.");
        }
        _requested["graphics.backend_threading"] = _rendererThreading;
        int width = Fields.Integer(p, "width", 1280, 64, 8192);
        int height = Fields.Integer(p, "height", 720, 64, 8192);
        string text2 = Fields.String(p, "title", "NX Infinity Core");
        if (text2.Length > 256)
        {
            throw new NxError("invalid_argument", "Window title is limited to 256 characters.");
        }
        bool flag = Fields.Boolean(p, "headless", fallback: false) || !Fields.Boolean(p, "visible", fallback: false);
        if (_runtimeLease == null)
        {
            _runtimeLease = AcquireProfileLease(text);
        }
        try
        {
            CheckProfileMarkers(text);
            EnsureFilesystem(text);
            SDL3.SDL_SetHint(SDL3.SDL_HINT_APP_NAME, "NX Infinity runtime");
            if (!SDL3.SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO | SDL_InitFlags.SDL_INIT_GAMEPAD | SDL_InitFlags.SDL_INIT_EVENTS))
            {
                throw new NxError("missing_dependency", "SDL3 could not initialize a video device.");
            }
            _sdlInitialized = true;
            RefreshGamepads();
            _window = SDL3.SDL_CreateWindow(text2, width, height, (SDL_WindowFlags)(0x10000020uL | (ulong)(flag ? 8 : 0)));
            if (_window == null)
            {
                throw new NxError("missing_dependency", "SDL3 could not create the Vulkan surface. A graphics device and desktop session are required.");
            }
            SynchronizeWindow();
            if (!OpenALHardwareDeviceDriver.IsSupported)
            {
                throw new NxError("missing_dependency", "No usable OpenAL audio output was found.");
            }
            LibHacHorizonManager libHacHorizonManager = (_horizon = new LibHacHorizonManager());
            libHacHorizonManager.InitializeFsServer(_vfs);
            libHacHorizonManager.InitializeArpServer();
            libHacHorizonManager.InitializeBcatServer();
            libHacHorizonManager.InitializeSystemClients();
            ContentManager contentManager = _contentManager;
            contentManager.LoadEntries();
            AccountManager accountManager = (_accounts = new AccountManager(libHacHorizonManager.RyujinxClient));
            RyuPipelineCacheSettings.Directory = System.IO.Path.Combine(text, "cache", "nx-vulkan-pipelines");
            RyuPipelineCacheSettings.FastGraphicsCompilation = true;
            _renderer = new VulkanRenderer(Vk.GetApi(), CreateSurface, RequiredExtensions, string.Empty);
            ConfigureRendererObservation();
            MeteredAudioDriver audioDeviceDriver = (_audio = new MeteredAudioDriver());
            HLEConfiguration configuration = new HLEConfiguration(_vfs, libHacHorizonManager, contentManager, accountManager, _programChannel, _coreRenderer, audioDeviceDriver, MemoryConfiguration.MemoryConfiguration4GiB, Interactions, SystemLanguage.AmericanEnglish, RegionCode.USA, Ryujinx.Common.Configuration.VSyncMode.Switch, enableDockedMode: true, enablePtc: true, enableInternetAccess: false, IntegrityCheckLevel.ErrorOnInvalid, 0, 0L, "UTC", MemoryManagerMode.HostMappedUnsafe, ignoreMissingServices: false, AspectRatio.Fixed16x9, 1f, useHypervisor: false, string.Empty, NativeMultiplayerMode, multiplayerDisableP2p: true, _networkPassphrase, _networkServer, 60);
            GraphicsConfig.ResScale = 1f;
            GraphicsConfig.MaxAnisotropy = -1f;
            GraphicsConfig.EnableShaderCache = true;
            GraphicsConfig.EnableTextureRecompression = false;
            _constructingCore = true;
            _device = new Ryujinx.HLE.Switch(configuration);
            _device.FramePolicy.ConfigureBackend("nxi", "67591deee07ab2ce86fc9b72c42e8b64c95ba1b3", () => _state == "loaded" && _device.System.IsPaused, NativePatchModsAbsent);
            _constructingCore = false;
            _device.Gpu.ShaderCacheStateChanged += NativeShaderCacheStateChanged;
            _device.System.TogglePauseEmulation(pause: true);
            ConfigurePlayer(0);
            _device.SetVolume(1f);
            _renderActive = true;
            _gpuThread = new Thread(() =>
            {
                RenderLoop(width, height);
            })
            {
                Name = "NX.Ryujinx.Gpu",
                IsBackground = true
            };
            _gpuThread.Start();
            WaitWhilePumping(_gpuReady, TimeSpan.FromSeconds(30L));
            if (_gpuError != null)
            {
                throw new NxError("core_error", "Vulkan initialization failed. See the backend error event.");
            }
            object result = ChangeState("initialized");
            ObserveWindowPresentationState();
            return result;
        }
        catch
        {
            try
            {
                DisposeResources();
            }
            finally
            {
                _state = "stopped";
            }
            throw;
        }
    }

    private object Load(JsonElement p)
    {
        Require("initialized");
        string text = Fields.RequiredString(p, "contentPath");
        if (!System.IO.Path.IsPathFullyQualified(text))
        {
            throw new NxError("invalid_argument", "contentPath must be an absolute path.");
        }
        if (!File.Exists(text) && !Directory.Exists(text))
        {
            throw new NxError("invalid_argument", "The content path does not exist.");
        }
        string text2 = System.IO.Path.GetExtension(text).ToLowerInvariant();
        if (!Directory.Exists(text) && !Enumerable.Contains(new string[6] { ".nro", ".nso", ".nsp", ".pfs0", ".nca", ".xci" }, text2))
        {
            throw new NxError("unsupported", "Supported content routes are NRO, NSO, NSP/PFS0, NCA, XCI and an unpacked ExeFS directory.");
        }
        bool flag;
        switch (text2)
        {
            case ".nsp":
            case ".xci":
            case ".nca":
                flag = true;
                break;
            default:
                flag = false;
                break;
        }
        if (flag && !File.Exists(System.IO.Path.Combine(AppDataManager.KeysDirPath, "prod.keys")))
        {
            throw new NxError("missing_dependency", "This content route requires user-supplied keys in this backend profile's system directory.");
        }
        _loadIdentity = InspectForLoad(text);
        if (_loadIdentity.TitleId != null)
        {
            VerifyContentFilesUnchanged(ParseTitle(_loadIdentity.TitleId) & 0xFFFFFFFFFFFFE000uL);
        }
        VerifyModIdentityBeforeLoad(_loadIdentity);
        lock (_nativeEvidenceLock)
        {
            _nativeLoadEvidence.Clear();
        }
        _observingNativeLoad = true;
        bool flag3;
        try
        {
            string text3 = ((p.TryGetProperty("romFsPath", out var value) && value.ValueKind != JsonValueKind.Null) ? AbsolutePath(Fields.RequiredString(p, "romFsPath")) : null);
            if (text3 != null && (!Directory.Exists(text) || !File.Exists(text3)))
            {
                throw new NxError("invalid_argument", "romFsPath requires an unpacked ExeFS directory and an existing local RomFS image.");
            }
            if (Directory.Exists(text) && text3 == null)
            {
                if (File.Exists(System.IO.Path.Combine(text, "romfs.bin")))
                {
                    text3 = System.IO.Path.Combine(text, "romfs.bin");
                }
                else
                {
                    string[] files = Directory.GetFiles(text, "*.romfs");
                    if (files.Length == 1)
                    {
                        text3 = files[0];
                    }
                }
            }
            if (Directory.Exists(text))
            {
                flag = _device.LoadCart(text, text3);
            }
            else
            {
                bool flag2;
                switch (text2)
                {
                    case ".xci":
                        flag2 = _device.LoadXci(text, 0uL);
                        break;
                    case ".nca":
                        flag2 = _device.LoadNca(text);
                        break;
                    case ".nsp":
                    case ".pfs0":
                        flag2 = _device.LoadNsp(text, 0uL);
                        break;
                    default:
                        flag2 = _device.LoadProgram(text);
                        break;
                }
                flag = flag2;
            }
            flag3 = flag;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            _protocol.Event("core.error", new
            {
                operation = "load",
                exception = ex.GetType().Name,
                message = "The core could not load the supplied content."
            });
            Stop();
            throw new NxError("core_error", "Content loading failed. Recreate the session after reviewing local logs and prerequisites.");
        }
        finally
        {
            _observingNativeLoad = false;
        }
        if (!flag3)
        {
            Stop();
            throw new NxError("core_error", "The core rejected the content; a fresh session is required.");
        }
        _device.System.TogglePauseEmulation(pause: true);
        _gpuStart.Set();
        ChangeState("loaded");
        ProcessResult processResult = (_applicationProcess = _device.Processes.ActiveApplication);
        _runtimeProgramId = ((processResult == null) ? ((ulong?)null) : _device.Processes.GetRuntimeProgramId(processResult.ProcessId));
        ulong? runtimeProgramId = _runtimeProgramId;
        if (runtimeProgramId.HasValue)
        {
            ulong valueOrDefault = runtimeProgramId.GetValueOrDefault();
            ObserveLoadedConfiguration(valueOrDefault);
        }
        string state = _state;
        runtimeProgramId = _runtimeProgramId;
        object titleId;
        if (runtimeProgramId.HasValue)
        {
            ulong valueOrDefault2 = runtimeProgramId.GetValueOrDefault();
            titleId = Hex(valueOrDefault2);
        }
        else
        {
            titleId = null;
        }
        runtimeProgramId = _runtimeProgramId;
        object runtimeProgramId2;
        if (runtimeProgramId.HasValue)
        {
            ulong valueOrDefault3 = runtimeProgramId.GetValueOrDefault();
            runtimeProgramId2 = Hex(valueOrDefault3);
        }
        else
        {
            runtimeProgramId2 = null;
        }
        return new
        {
            state = state,
            titleId = (string)titleId,
            runtimeProgramId = (string)runtimeProgramId2,
            nacpTitleId = _loadIdentity?.NacpTitleId,
            npdmProgramId = processResult?.ProgramIdText,
            name = processResult?.Name,
            displayVersion = processResult?.DisplayVersion,
            buildId = processResult?.MainBuildId,
            format = (Directory.Exists(text) ? "exefs" : text2.Substring(1))
        };
    }

    private object Start()
    {
        Require("loaded");
        if (_gpuError != null)
        {
            throw new NxError("core_error", "The GPU worker failed.");
        }
        NativeFrameOperation(() =>
        {
            _device.FramePolicy.MarkStarted();
            return true;
        });
        _guestStartedAt = Stopwatch.GetTimestamp();
        _device.System.TogglePauseEmulation(pause: false);
        object result = ChangeState("running");
        ObserveWindowPresentationState();
        return result;
    }

    private object Pause(bool value)
    {
        Require(value ? "running" : "paused");
        if (value)
        {
            _presentTiming.SetRunning(running: false);
            _device.FramePolicy.SetRunning(running: false);
        }
        try
        {
            _device.System.TogglePauseEmulation(value);
        }
        finally
        {
            _presentTiming.SetRunning(!_device.System.IsPaused && !_windowPresentationSuspended);
            _device.FramePolicy.SetRunning(!_device.System.IsPaused && !_windowPresentationSuspended);
        }
        if (_device.System.IsPaused != value)
        {
            throw new NxError("core_error", "The core did not acknowledge the pause state.");
        }
        return ChangeState(value ? "paused" : "running");
    }

    private object Stop()
    {
        if (_state == "stopped")
        {
            return new
            {
                state = _state
            };
        }
        Require("initialized", "loaded", "running", "paused");
        DisposeResources();
        return ChangeState("stopped");
    }

    private object DisposeCommand()
    {
        Dispose();
        return new
        {
            state = _state
        };
    }

    public void Dispose()
    {
        if (!Disposed)
        {
            DisposeResources();
            ChangeState("disposed");
        }
    }

    private void DisposeResources()
    {
        try
        {
            _teardown.Run(() =>
            {
                TeardownPhase("teardown.begin");
                TeardownPhase("teardown.frame_policy");
                _presentTiming.SetRunning(running: false);
                _device?.FramePolicy.SetRunning(running: false);
                DisposeResourcesCore();
                TeardownPhase("teardown.complete");
            });
        }
        catch (Exception error)
        {
            RequiresProcessExit = true;
            _diagnostics.Trace("session", "teardown.failed", _state, "failedPhase=" + _teardownPhase);
            _diagnostics.Exception("session", _teardownPhase, _state, error);
            throw;
        }
    }

    private void TeardownPhase(string phase)
    {
        _teardownPhase = phase;
        _diagnostics.Trace("session", phase, _state);
    }

    private unsafe void DisposeResourcesCore()
    {
        TeardownPhase("teardown.cancel");
        _shutdown.Cancel();
        Logger.Updated -= NativeLogObserved;
        Interactions.CancelAll();
        if (_constructingCore)
        {
            throw new NxError("core_error", "Core construction failed after starting native services. This worker must exit before the profile is reusable.");
        }
        _renderActive = false;
        _windowPixelSize.Close();
        PendingWindowEvents = false;
        _gpuStart.Set();
        if (_gpuDisposalError != null)
        {
            throw new NxError("core_error", "The renderer consumer failed. This worker must exit before the profile is reusable.");
        }
        TeardownPhase("teardown.guest_pause");
        if (_device != null && !_device.System.IsPaused)
        {
            _device.System.TogglePauseEmulation(pause: true);
        }
        TeardownPhase("teardown.gpu_loop_wait");
        if (_gpuThread != null)
        {
            WaitWhilePumping(_gpuLoopStopped, TimeSpan.FromSeconds(20L));
        }
        if (_gpuDisposalError != null)
        {
            throw new NxError("core_error", "The renderer consumer failed during shutdown. The profile lease remains held until worker exit.");
        }
        if (_device != null)
        {
            TeardownPhase("teardown.guest_dispose");
            _device.Gpu.SignalHostShutdown();
            _device.Dispose();
            _device.Gpu.ShaderCacheStateChanged -= NativeShaderCacheStateChanged;
            if (_threadedRunLoopEntered)
            {
                TeardownPhase("teardown.gpu_dispose");
                _device.DisposeGpu();
                _gpuResourcesDisposed = true;
            }
        }
        else
        {
            _vfs?.Dispose();
        }
        _contentManager?.ClearAocData();
        TeardownPhase("teardown.gpu_join");
        _gpuMayDispose.Set();
        if (_gpuThread != null)
        {
            WaitWhilePumping(_gpuDisposed, TimeSpan.FromSeconds(20L));
            if (!_gpuThread.Join(TimeSpan.FromSeconds(1L)))
            {
                throw new NxError("timeout", "GPU disposal did not finish. The data lock remains held until the worker exits; save completion is unverified.");
            }
        }
        if (_gpuDisposalError != null)
        {
            throw new NxError("core_error", "GPU/cache disposal failed. The profile lease remains held until this worker exits.");
        }
        _device = null;
        _vfs = null;
        _applicationProcess = null;
        _runtimeProgramId = null;
        TeardownPhase("teardown.window");
        CloseGamepads();
        if (_window != null)
        {
            SDL3.SDL_DestroyWindow(_window);
            _window = null;
        }
        if (_sdlInitialized)
        {
            SDL3.SDL_Quit();
            _sdlInitialized = false;
        }
        TeardownPhase("teardown.lease_release");
        if (!_preserveLeaseForProgramTransition)
        {
            _runtimeLease?.Dispose();
        }
        _runtimeLease = null;
    }

    private void RenderLoop(int width, int height)
    {
        try
        {
            _coreRenderer.Initialize(GraphicsDebugLevel.Error);
            _renderer.Window.SetSize(width, height);
            _observedRenderer.Window.ChangeVSyncMode((Ryujinx.Graphics.GAL.VSyncMode)_device.VSyncMode);
            _gpuReady.Set();
            _gpuStart.Wait(_shutdown.Token);
            if (_renderActive)
            {
                if (_coreRenderer is ThreadedRenderer threadedRenderer)
                {
                    _threadedRunLoopEntered = true;
                    threadedRenderer.RunLoop(GpuCommandLoop);
                }
                else
                {
                    GpuCommandLoop();
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex2)
        {
            _diagnostics.Exception("gpu", "render.failed", _state, ex2);
            RenderingFailed(ex2);
            if (_threadedRunLoopEntered)
            {
                _gpuDisposalError = ex2;
                RequiresProcessExit = true;
            }
        }
        finally
        {
            _gpuReady.Set();
            _gpuLoopStopped.Set();
            _gpuMayDispose.Wait();
            try
            {
                _diagnostics.Trace("gpu", "dispose.begin", _state);
                if (!_gpuResourcesDisposed)
                {
                    _device?.DisposeGpu();
                }
                _diagnostics.Trace("gpu", "dispose.complete", _state);
            }
            catch (Exception ex3)
            {
                if (_gpuError == null)
                {
                    _gpuError = ex3;
                }
                _gpuDisposalError = ex3;
                _diagnostics.Exception("gpu", "dispose.failed", _state, ex3);
            }
            finally
            {
                _gpuDisposed.Set();
            }
        }
    }

    public unsafe void Pump()
    {
        PendingWindowEvents = false;
        ApplyInteractionInputCapture();
        Action result;
        while (_hostActions.TryDequeue(out result))
        {
            result();
        }
        if (ProgramTransitionPending || !_sdlInitialized || _window == null)
        {
            return;
        }
        int num = 0;
        bool flag = false;
        bool flag2 = false;
        bool flag3 = false;
        SDL_Event sDL_Event = default;
        bool flag5;
        while (num < 128 && (bool)SDL3.SDL_PollEvent(&sDL_Event))
        {
            num++;
            _windowEventsDispatched++;
            bool flag4 = sDL_Event.window.windowID == SDL3.SDL_GetWindowID(_window);
            if (sDL_Event.Type == SDL_EventType.SDL_EVENT_QUIT || ((sDL_Event.Type == SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED) & flag4))
            {
                CloseGameWindow((sDL_Event.Type == SDL_EventType.SDL_EVENT_QUIT) ? "sdl_quit" : "window_close_requested");
                return;
            }
            if (flag4 && sDL_Event.Type == SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED)
            {
                flag2 = true;
                flag = true;
            }
            flag5 = flag4;
            if (flag5)
            {
                bool flag6;
                switch (sDL_Event.Type)
                {
                    case SDL_EventType.SDL_EVENT_WINDOW_SHOWN:
                    case SDL_EventType.SDL_EVENT_WINDOW_HIDDEN:
                    case SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED:
                    case SDL_EventType.SDL_EVENT_WINDOW_RESTORED:
                    case SDL_EventType.SDL_EVENT_WINDOW_ENTER_FULLSCREEN:
                    case SDL_EventType.SDL_EVENT_WINDOW_LEAVE_FULLSCREEN:
                        flag6 = true;
                        break;
                    default:
                        flag6 = false;
                        break;
                }
                flag5 = flag6;
            }
            if (flag5)
            {
                ObserveWindowPresentationState();
                flag3 = true;
                flag = true;
            }
            SDL_EventType type = sDL_Event.Type;
            if ((uint)(type - 1619) <= 2u)
            {
                RefreshGamepads();
            }
            if (sDL_Event.Type != SDL_EventType.SDL_EVENT_KEY_DOWN || (bool)sDL_Event.key.repeat || sDL_Event.key.windowID != SDL3.SDL_GetWindowID(_window))
            {
                continue;
            }
            if (sDL_Event.key.scancode == SDL_Scancode.SDL_SCANCODE_F11)
            {
                try
                {
                    SetFullscreen((SDL3.SDL_GetWindowFlags(_window) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) == 0, emit: false);
                    flag = true;
                }
                catch (NxError nxError)
                {
                    _protocol.Event("window.error", new
                    {
                        operation = "fullscreen",
                        code = nxError.Code,
                        message = nxError.Message
                    });
                }
            }
            if (sDL_Event.key.scancode == SDL_Scancode.SDL_SCANCODE_ESCAPE)
            {
                RequestMenu();
            }
        }
        PendingWindowEvents = num == 128;
        if (PendingWindowEvents)
        {
            _windowEventBudgetYields++;
        }
        if (flag2 | flag3)
        {
            QueueWindowPixelSize(flag3);
        }
        if (flag && _state != "created")
        {
            _protocol.Event("window.changed", WindowState());
        }
        flag5 = _gpuError != null;
        if (flag5)
        {
            bool flag6;
            switch (_state)
            {
                case "running":
                case "paused":
                case "loaded":
                    flag6 = true;
                    break;
                default:
                    flag6 = false;
                    break;
            }
            flag5 = flag6;
        }
        if (flag5)
        {
            Stop();
            return;
        }
        string state = _state;
        flag5 = ((state == "running" || state == "paused") ? true : false);
        if (flag5 && _applicationProcess?.GetKernelState(_device) == "Exited")
        {
            long value = ((_guestStartedAt == 0L) ? 0 : (Stopwatch.GetTimestamp() - _guestStartedAt));
            _diagnostics.Trace("session", "guest.exited", _state, $"processId={_applicationProcess.ProcessId} elapsedTicks={value} presentedFrames={Interlocked.Read(in _presentedFrames)} reason=unknown");
            _protocol.Event("content.exited", new
            {
                reason = "guest_process_exit",
                processId = _applicationProcess.ProcessId
            });
            Stop();
            return;
        }
        flag5 = _device == null;
        if (!flag5)
        {
            bool flag6;
            switch (_state)
            {
                case "initialized":
                case "running":
                case "paused":
                case "loaded":
                    flag6 = true;
                    break;
                default:
                    flag6 = false;
                    break;
            }
            flag5 = !flag6;
        }
        if (flag5)
        {
            return;
        }
        long timestamp = Stopwatch.GetTimestamp();
        if ((double)(timestamp - _lastInputAt) * 1000.0 / (double)Stopwatch.Frequency < 8.0)
        {
            return;
        }
        _lastInputAt = timestamp;
        PollGamepads();
        if (!(_inputTarget == "ui"))
        {
            if (!_remotePlayers[0] && !_physicalPlayers.ContainsKey(0))
            {
                PollKeyboard();
            }
            _device.Hid.Npads.Update(UpdateTouchPointer());
            _device.Hid.DebugPad.Update();
        }
    }

    private unsafe void PollKeyboard()
    {
        int num = default;
        SDLBool* keys = SDL3.SDL_GetKeyboardState(&num);
        int keyCount = num;
        ControllerKeys controllerKeys = (ControllerKeys)0L;
        if (Held(SDL_Scancode.SDL_SCANCODE_Z))
        {
            controllerKeys |= ControllerKeys.A;
        }
        if (Held(SDL_Scancode.SDL_SCANCODE_X))
        {
            controllerKeys |= ControllerKeys.B;
        }
        if (Held(SDL_Scancode.SDL_SCANCODE_C))
        {
            controllerKeys |= ControllerKeys.X;
        }
        if (Held(SDL_Scancode.SDL_SCANCODE_V))
        {
            controllerKeys |= ControllerKeys.Y;
        }
        if (Held(SDL_Scancode.SDL_SCANCODE_RETURN))
        {
            controllerKeys |= ControllerKeys.Plus;
        }
        if (Held(SDL_Scancode.SDL_SCANCODE_BACKSPACE))
        {
            controllerKeys |= ControllerKeys.Minus;
        }
        if (Held(SDL_Scancode.SDL_SCANCODE_Q))
        {
            controllerKeys |= ControllerKeys.Zl;
        }
        if (Held(SDL_Scancode.SDL_SCANCODE_E))
        {
            controllerKeys |= ControllerKeys.L;
        }
        if (Held(SDL_Scancode.SDL_SCANCODE_U))
        {
            controllerKeys |= ControllerKeys.R;
        }
        if (Held(SDL_Scancode.SDL_SCANCODE_O))
        {
            controllerKeys |= ControllerKeys.Zr;
        }
        if (Held(SDL_Scancode.SDL_SCANCODE_F))
        {
            controllerKeys |= ControllerKeys.LStick;
        }
        if (Held(SDL_Scancode.SDL_SCANCODE_H))
        {
            controllerKeys |= ControllerKeys.RStick;
        }
        if (Held(SDL_Scancode.SDL_SCANCODE_UP))
        {
            controllerKeys |= ControllerKeys.DpadUp;
        }
        if (Held(SDL_Scancode.SDL_SCANCODE_DOWN))
        {
            controllerKeys |= ControllerKeys.DpadDown;
        }
        if (Held(SDL_Scancode.SDL_SCANCODE_LEFT))
        {
            controllerKeys |= ControllerKeys.DpadLeft;
        }
        if (Held(SDL_Scancode.SDL_SCANCODE_RIGHT))
        {
            controllerKeys |= ControllerKeys.DpadRight;
        }
        _input[0].LStick = new JoystickPosition
        {
            Dx = Axis(SDL_Scancode.SDL_SCANCODE_D, SDL_Scancode.SDL_SCANCODE_A),
            Dy = Axis(SDL_Scancode.SDL_SCANCODE_W, SDL_Scancode.SDL_SCANCODE_S)
        };
        _input[0].RStick = new JoystickPosition
        {
            Dx = Axis(SDL_Scancode.SDL_SCANCODE_L, SDL_Scancode.SDL_SCANCODE_J),
            Dy = Axis(SDL_Scancode.SDL_SCANCODE_I, SDL_Scancode.SDL_SCANCODE_K)
        };
        _input[0].Buttons = controllerKeys | _device.Hid.UpdateStickButtons(_input[0].LStick, _input[0].RStick);
        int Axis(SDL_Scancode plus, SDL_Scancode minus)
        {
            return (Held(plus) ? 32767 : 0) - (Held(minus) ? 32767 : 0);
        }
        unsafe bool Held(SDL_Scancode key)
        {
            if ((int)key < keyCount)
            {
                return keys[(int)key];
            }
            return false;
        }
    }

    private object Input(JsonElement p)
    {
        Require("loaded", "running", "paused");
        if (_inputTarget != "guest" || Volatile.Read(in _interactionInputCaptureRequested) != 0 || Interactions.InputCaptureActive)
        {
            throw new NxError("invalid_state", "Guest input is disabled while the frontend or a native applet owns input. Close the dialog, release held controls and route input to guest first.");
        }
        int num = Fields.Integer(p, "player", 0, 0, 7);
        if (!p.TryGetProperty("buttons", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 32)
        {
            throw new NxError("invalid_argument", "buttons must be an array of at most 32 button names.");
        }
        ControllerKeys controllerKeys = (ControllerKeys)0L;
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !ButtonNames.TryGetValue(item.GetString(), out var value2))
            {
                throw new NxError("invalid_argument", "Unknown controller button. See the Ryujinx adapter README.");
            }
            controllerKeys |= value2;
        }
        JoystickPosition joystickPosition = new JoystickPosition
        {
            Dx = (int)(Fields.Number(p, "leftX", 0.0, -1.0, 1.0) * 32767.0),
            Dy = (int)(Fields.Number(p, "leftY", 0.0, -1.0, 1.0) * 32767.0)
        };
        JoystickPosition joystickPosition2 = new JoystickPosition
        {
            Dx = (int)(Fields.Number(p, "rightX", 0.0, -1.0, 1.0) * 32767.0),
            Dy = (int)(Fields.Number(p, "rightY", 0.0, -1.0, 1.0) * 32767.0)
        };
        ConfigurePlayer(num);
        _remotePlayers[num] = true;
        _input[num] = new GamepadInput
        {
            PlayerId = (PlayerIndex)num,
            Buttons = (controllerKeys | _device.Hid.UpdateStickButtons(joystickPosition, joystickPosition2)),
            LStick = joystickPosition,
            RStick = joystickPosition2
        };
        _device.Hid.Npads.Update(UpdateTouchPointer());
        return new
        {
            accepted = true,
            player = num,
            applied = true,
            scope = "guest_hid_shared_memory"
        };
    }

    private void ConfigurePlayer(int player)
    {
        if (!_configuredPlayers[player])
        {
            _configuredPlayers[player] = true;
            _device.Hid.Npads.Configure(_configuredPlayers.Select((bool configured, int i) => new ControllerConfig
            {
                Player = (PlayerIndex)i,
                Type = (configured ? ControllerType.ProController : ControllerType.None)
            }).ToArray());
        }
    }

    private object Telemetry()
    {
        Require("initialized", "loaded", "running", "paused");
        bool flag = Interlocked.Read(in _presentedFrames) > 0;
        double gameFrameRate = _device.Statistics.GetGameFrameRate();
        HardwareInfo hardwareInfo = _renderer.GetHardwareInfo();
        return new
        {
            state = _state,
            emulationFps = ((flag && double.IsFinite(gameFrameRate)) ? new double?(gameFrameRate) : ((double?)null)),
            frameTimeMs = ((flag && gameFrameRate > 0.0) ? new double?(1000.0 / gameFrameRate) : ((double?)null)),
            frameCount = (flag ? new long?(Interlocked.Read(in _presentedFrames)) : ((long?)null)),
            memoryBytes = (long?)null,
            backendMetrics = new
            {
                fifoActivePercent = _device.Statistics.GetFifoPercent(),
                hostWorkingSetBytes = Process.GetCurrentProcess().WorkingSet64,
                gpuVendor = hardwareInfo.GpuVendor,
                gpuModel = hardwareInfo.GpuModel,
                gpuDriver = hardwareInfo.GpuDriver,
                gpuVersion = _renderer.GpuVersion,
                guestProcessState = _applicationProcess?.GetKernelState(_device),
                audio = _audio?.Snapshot(),
                inputTarget = _inputTarget,
                performance = _presentTiming.Snapshot(GraphicsConfig.EnableShaderCache, _device.System.EnablePtc, EffectiveRendererThreading),
                driverPipelineCache = RyuPipelineCacheSettings.Snapshot(),
                stutter = RyuStutterTrace.Snapshot(),
                gpuLoop = GpuLoopSnapshot(),
                shaderCompilation = _shaderCompilation.Snapshot(),
                presentation = VsyncConsumerState(),
                frameRate = _device.FramePolicy.Snapshot(),
                graphicsDevice = _renderer.NxGraphicsDevice
            },
            sampleTimeUtc = DateTimeOffset.UtcNow,
            source = "core"
        };
    }

    private object GetSetting(string id)
    {
        SettingCatalog.Find(id);
        object obj = ReadEffective(id);
        object valueOrDefault = _requested.GetValueOrDefault(id, obj ?? SettingCatalog.Find(id).DefaultValue);
        bool flag = id == "graphics.backend_threading" && obj != null && !object.Equals(valueOrDefault, obj);
        return new
        {
            id = id,
            requested = valueOrDefault,
            persisted = (object)null,
            effective = obj,
            effectiveKnown = (obj != null),
            applied = (obj != null && !flag),
            requiresRestart = flag,
            effectiveSource = ((id == "graphics.vsync") ? "guest_vsync_interval_and_renderer_window_setter_return" : null),
            consumerState = ((id == "graphics.vsync") ? VsyncConsumerState() : null)
        };
    }

    private object SetSetting(JsonElement p)
    {
        Require("initialized", "loaded", "running", "paused");
        string text = Fields.RequiredString(p, "id");
        Setting setting = SettingCatalog.Find(text);
        if (!p.TryGetProperty("value", out var value))
        {
            throw new NxError("invalid_argument", "Setting value is required.");
        }
        object normalized = setting.Validate(value);
        if (setting.Apply == "before_initialize")
        {
            _requested[text] = normalized;
            return GetSetting(text);
        }
        if (setting.Apply == "before_load" && _state != "initialized")
        {
            throw new NxError("invalid_state", "This setting must be applied before content is loaded.");
        }
        switch (text)
        {
            case "audio.volume":
                _device.SetVolume(Convert.ToSingle(normalized));
                break;
            case "system.docked":
                _device.System.ChangeDockedModeState((bool)normalized);
                break;
            case "graphics.vsync":
                {
                    Ryujinx.Common.Configuration.VSyncMode vSyncMode = ((!((string)normalized == "switch")) ? Ryujinx.Common.Configuration.VSyncMode.Unbounded : Ryujinx.Common.Configuration.VSyncMode.Switch);
                    if (vSyncMode != Ryujinx.Common.Configuration.VSyncMode.Switch && _device.FramePolicy.Snapshot().RequestedTarget == 60)
                    {
                        throw new NxError("invalid_state", "The correct-speed frame policy requires the original 60 Hz guest synchronization.");
                    }
                    ApplyWindowVsync((Ryujinx.Graphics.GAL.VSyncMode)vSyncMode);
                    _device.VSyncMode = vSyncMode;
                    _device.UpdateVSyncInterval();
                    break;
                }
            case "graphics.resolution_scale":
                OnGpu(() =>
                {
                    GraphicsConfig.ResScale = Convert.ToSingle(normalized);
                });
                break;
            case "graphics.max_anisotropy":
                GraphicsConfig.MaxAnisotropy = Convert.ToSingle(normalized);
                break;
            case "graphics.shader_cache":
                GraphicsConfig.EnableShaderCache = (bool)normalized;
                break;
            case "graphics.fast_pipeline_compilation":
                RyuPipelineCacheSettings.FastGraphicsCompilation = (bool)normalized;
                break;
            case "graphics.texture_recompression":
                GraphicsConfig.EnableTextureRecompression = (bool)normalized;
                break;
            case "graphics.fast_gpu_time":
                GraphicsConfig.FastGpuTime = (bool)normalized;
                break;
            case "cpu.ptc":
                _device.System.EnablePtc = (bool)normalized;
                break;
            default:
                throw new NxError("unsupported", "Setting has no implementation.");
        }
        _requested[text] = normalized;
        return GetSetting(text);
    }

    private object ReadEffective(string id)
    {
        if (_device == null)
        {
            return null;
        }
        return id switch
        {
            "audio.volume" => _device.GetVolume(),
            "system.docked" => _device.System.State.DockedMode,
            "graphics.vsync" => ReadEffectiveVsync(),
            "graphics.resolution_scale" => GraphicsConfig.ResScale,
            "graphics.max_anisotropy" => GraphicsConfig.MaxAnisotropy,
            "graphics.backend_threading" => EffectiveRendererThreading,
            "graphics.fast_pipeline_compilation" => RyuPipelineCacheSettings.FastGraphicsCompilation,
            "graphics.shader_cache" => GraphicsConfig.EnableShaderCache,
            "graphics.texture_recompression" => GraphicsConfig.EnableTextureRecompression,
            "graphics.fast_gpu_time" => GraphicsConfig.FastGpuTime,
            "cpu.ptc" => _device.System.EnablePtc,
            _ => null,
        };
    }

    private void OnGpu(Action action)
    {
        if (_state == "initialized")
        {
            action();
            return;
        }
        ManualResetEventSlim done = new ManualResetEventSlim();
        Exception error = null;
        _gpuActions.Enqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                done.Set();
            }
        });
        WaitWhilePumping(done, TimeSpan.FromSeconds(10L));
        if (error == null)
        {
            return;
        }
        throw new NxError("core_error", "GPU setting application failed.");
    }

    private unsafe SurfaceKHR CreateSurface(Instance instance, Vk api)
    {
        ulong surface = 0uL;
        Exception error = null;
        ManualResetEventSlim done = new ManualResetEventSlim();
        _hostActions.Enqueue(() =>
        {
            try
            {
                VkSurfaceKHR_T* ptr = null;
                if (!SDL3.SDL_Vulkan_CreateSurface(_window, (VkInstance_T*)instance.Handle, null, &ptr))
                {
                    throw new NxError("missing_dependency", "SDL could not create a Vulkan surface.");
                }
                surface = (ulong)ptr;
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                done.Set();
            }
        });
        if (!done.Wait(TimeSpan.FromSeconds(15L)))
        {
            throw new NxError("timeout", "Timed out creating the Vulkan surface.");
        }
        if (error != null)
        {
            throw error;
        }
        return new SurfaceKHR(surface);
    }

    private unsafe static string[] RequiredExtensions()
    {
        uint num = 0u;
        byte** ptr = SDL3.SDL_Vulkan_GetInstanceExtensions(&num);
        if (ptr == null || num == 0)
        {
            throw new NxError("missing_dependency", "SDL did not report Vulkan instance extensions.");
        }
        string[] array = new string[num];
        for (int i = 0; i < num; i++)
        {
            array[i] = Marshal.PtrToStringUTF8((nint)ptr[i]);
        }
        return array;
    }

    private void WaitWhilePumping(ManualResetEventSlim signal, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!signal.IsSet)
        {
            Action result;
            while (_hostActions.TryDequeue(out result))
            {
                result();
            }
            if (_sdlInitialized)
            {
                SDL3.SDL_PumpEvents();
            }
            if (stopwatch.Elapsed >= timeout)
            {
                throw new NxError("timeout", "The core operation did not finish before its deadline.");
            }
            signal.Wait(2);
        }
    }

    private static object Probe()
    {
        using MemoryOwner<byte> memoryOwner = BCnDecoder.DecodeBC1(new byte[8] { 0, 248, 0, 0, 0, 0, 0, 0 }, 4, 4, 1, 1, 1);
        bool flag = memoryOwner.Span.Length == 64;
        for (int i = 0; i < 64; i += 4)
        {
            flag &= memoryOwner.Span[i] == byte.MaxValue && memoryOwner.Span[i + 1] == 0 && memoryOwner.Span[i + 2] == 0 && memoryOwner.Span[i + 3] == byte.MaxValue;
        }
        if (!flag)
        {
            throw new NxError("core_error", "Pinned core BC1 fixture did not match expected opaque-red pixels.");
        }
        return new
        {
            backendRevision = "67591deee07ab2ce86fc9b72c42e8b64c95ba1b3",
            coreAssembly = typeof(Ryujinx.HLE.Switch).Assembly.GetName().Name,
            decoderAssembly = typeof(BCnDecoder).Assembly.GetName().Name,
            fixture = "bc1-4x4-opaque-red",
            source = "pinned_core",
            passed = true,
            outputSha256 = Convert.ToHexString(SHA256.HashData(memoryOwner.Span)).ToLowerInvariant(),
            gameplayTested = false
        };
    }

    private object ChangeState(string next)
    {
        _state = next;
        _diagnostics.Trace("session", "state.changed", next);
        if (!_suppressTransitionStates)
        {
            _protocol.Event("state", new
            {
                state = next
            });
        }
        return new
        {
            state = next
        };
    }

    private void Require(params string[] states)
    {
        if (!Enumerable.Contains(states, _state))
        {
            throw new NxError("invalid_state", "Operation is invalid in state '" + _state + "'.");
        }
    }

    private object FrameRateGet()
    {
        Require("initialized", "loaded", "running", "paused");
        return _device.FramePolicy.Snapshot();
    }

    private object FrameRateSet(JsonElement parameters)
    {
        Require("initialized", "loaded", "running", "paused");
        RequirePatchShape(parameters, "target");
        int target = Fields.Integer(parameters, "target", -1, 0, 60);
        if (target != 0 && _networkProvider != "offline")
        {
            throw new NxError("network_mods_mismatch", "Local Wireless requires the original game timing policy.");
        }
        int num = target;
        if ((num != 0 && num != 60) || 1 == 0)
        {
            throw new NxError("invalid_argument", "Frame target must be 0 (original) or 60.");
        }
        if (target == 60 && (_device.VSyncMode != Ryujinx.Common.Configuration.VSyncMode.Switch || _device.TargetVSyncInterval != 60))
        {
            throw new NxError("invalid_state", "The correct-speed frame policy requires the original 60 Hz guest synchronization.");
        }
        return NativeFrameOperation(() => _device.FramePolicy.SetTarget(target));
    }

    private object GamePatchPrepare(JsonElement parameters)
    {
        if (_networkProvider != "offline")
        {
            throw new NxError("network_mods_mismatch", "Executable patches are unavailable during a Local Wireless session.");
        }
        Require("loaded");
        RequirePatchShape(parameters, "transactionId", "recipeSha256", "recipe");
        JsonElement property = parameters.GetProperty("recipe");
        RequirePatchShape(property, "schemaVersion", "recipeId", "adapterId", "backendId", "backendRevision", "titleId", "buildId", "displayVersion", "targetFrameRate", "frameGate", "writes");
        JsonElement property2 = property.GetProperty("writes");
        bool flag = property2.ValueKind != JsonValueKind.Array;
        if (!flag)
        {
            int arrayLength = property2.GetArrayLength();
            bool flag2 = ((arrayLength < 1 || arrayLength > 128) ? true : false);
            flag = flag2;
        }
        if (flag)
        {
            throw new NxError("invalid_argument", "A patch requires between 1 and 128 guarded writes.");
        }
        foreach (JsonElement item in property2.EnumerateArray())
        {
            RequirePatchShape(item, "module", "offset", "expectedHex", "replacementHex");
        }
        GamePatchRequest request;
        try
        {
            request = parameters.Deserialize<GamePatchRequest>(NativeGamePatchJson);
        }
        catch (JsonException)
        {
            throw new NxError("invalid_argument", "The native patch request does not match its typed schema.");
        }
        return NativeFrameOperation(() => _device.FramePolicy.Prepare(request));
    }

    private object GamePatchOperation(string operation, JsonElement parameters)
    {
        Require("loaded", "running", "paused");
        RequirePatchShape(parameters, "transactionId");
        string transactionId = Fields.RequiredString(parameters, "transactionId");
        return NativeFrameOperation(() => operation switch
        {
            "apply" => _device.FramePolicy.Apply(transactionId),
            "rollback" => _device.FramePolicy.Rollback(transactionId),
            "get" => _device.FramePolicy.GetPatch(transactionId),
            _ => throw new NxError("unsupported", "Unsupported native patch operation."),
        });
    }

    private bool NativePatchModsAbsent()
    {
        if (_applicationProcess != null)
        {
            ulong? runtimeProgramId = _runtimeProgramId;
            if (runtimeProgramId.HasValue && _loadIdentity != null && _loadIdentity.IdentityVerified && !(_loadIdentity.BuildIdSource != "main_nso") && string.Equals(_loadIdentity.BuildId, _applicationProcess.MainBuildId, StringComparison.OrdinalIgnoreCase))
            {
                string path = System.IO.Path.Combine(GameDirectory(_runtimeProgramId.Value), "nx-mods.json");
                if (!File.Exists(path))
                {
                    return true;
                }
                ModConfiguration modConfiguration = ReadProfileJson<ModConfiguration>(path);
                if (modConfiguration?.Mounts != null)
                {
                    return !modConfiguration.Mounts.Any((BoundMount m) => m.Mount.Enabled);
                }
                return false;
            }
        }
        return false;
    }

    private static T NativeFrameOperation<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (RyuFramePolicyException ex)
        {
            throw new NxError(ex.Code, ex.Message);
        }
    }

    private static void RequirePatchShape(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new NxError("invalid_argument", "Native patch parameters must be JSON objects.");
        }
        HashSet<string> hashSet = new HashSet<string>(fields, StringComparer.Ordinal);
        HashSet<string> hashSet2 = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty item in value.EnumerateObject())
        {
            if (!hashSet.Contains(item.Name) || !hashSet2.Add(item.Name))
            {
                throw new NxError("invalid_argument", "Native patch parameters contain unknown or duplicate fields.");
            }
        }
        if (!hashSet2.SetEquals(hashSet))
        {
            throw new NxError("invalid_argument", "Native patch parameters are missing required fields.");
        }
    }

    private void NativeLogObserved(object sender, LogEventArgs e)
    {
        ObserveNetworkLog(e);
        if (!_observingNativeLoad || e.Message == null || !e.Message.StartsWith("ModLoader ", StringComparison.Ordinal))
        {
            return;
        }
        lock (_nativeEvidenceLock)
        {
            if (_nativeLoadEvidence.Count < 256)
            {
                _nativeLoadEvidence.Add((e.Message.Length <= 2048) ? e.Message : e.Message.Substring(0, 2048));
            }
        }
    }

    private object GetMods(JsonElement p)
    {
        Require("initialized", "loaded", "running", "paused");
        return ModState(ReadTitle(p));
    }

    private object ModState(ulong title)
    {
        string path = System.IO.Path.Combine(GameDirectory(title), "nx-mods.json");
        ModConfiguration modConfiguration = (File.Exists(path) ? ReadProfileJson<ModConfiguration>(path) : null);
        string[] array;
        lock (_nativeEvidenceLock)
        {
            array = _nativeLoadEvidence.ToArray();
        }
        bool flag = _runtimeProgramId == title;
        bool flag2 = flag && (object)modConfiguration != null && modConfiguration.Mounts.Any((BoundMount x) => x.Mount.Enabled) && array.Any((string x) => x.Contains("Using modded RomFS", StringComparison.Ordinal) || (x.Contains("NSO '", StringComparison.Ordinal) && x.Contains("replaced", StringComparison.Ordinal)) || x.Contains("Applying patch", StringComparison.Ordinal) || x.Contains("Patching", StringComparison.Ordinal));
        List<string> list = new List<string>();
        if ((object)modConfiguration != null)
        {
            list.Add("NX-owned native mount configuration is bound to backend " + modConfiguration.BackendRevision + " and main NSO " + modConfiguration.ExpectedBuildId + ".");
            list.Add("Deployed files are verified against their copied SHA-256 inventory immediately before loading.");
        }
        if (flag)
        {
            list.AddRange(array);
        }
        string titleId = Hex(title);
        NativeMount[] mounts = modConfiguration?.Mounts.Select((BoundMount x) => x.Mount).ToArray() ?? Array.Empty<NativeMount>();
        bool loaderObserved = flag2;
        bool requiresRestart = _state != "initialized";
        string status;
        if (flag2)
        {
            status = "native_loader_observed";
        }
        else
        {
            status = (((object)modConfiguration == null) ? "unconfigured" : "configured_unobserved");
        }
        return new
        {
            titleId = titleId,
            mounts = mounts,
            loaderObserved = loaderObserved,
            requiresRestart = requiresRestart,
            status = status,
            reason = (flag2 ? "Native loader application was observed. Visible guest effects require an independent guest test." : "A native load must verify exact main identity and observe mod application before consumption is reported."),
            evidence = list
        };
    }

    private object SetMods(JsonElement p)
    {
        Require("initialized");
        ulong num = ReadTitle(p);
        if (!p.TryGetProperty("mounts", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 128)
        {
            throw new NxError("invalid_argument", "mounts must be an array of at most 128 native mount requests.");
        }
        NativeMount[] array = (from m in value.EnumerateArray()
                               select new NativeMount(Fields.RequiredString(m, "id"), AbsolutePath(Fields.RequiredString(m, "sourcePath")), Fields.Boolean(m, "enabled", fallback: true))).ToArray();
        if (array.Select((NativeMount x) => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != array.Length)
        {
            throw new NxError("invalid_argument", "Native mount IDs must be unique.");
        }
        string text = ((p.TryGetProperty("expectedBuildId", out var value2) && value2.ValueKind != JsonValueKind.Null) ? Fields.RequiredString(p, "expectedBuildId").ToUpperInvariant() : null);
        string text2 = ((p.TryGetProperty("backendRevision", out var value3) && value3.ValueKind != JsonValueKind.Null) ? Fields.RequiredString(p, "backendRevision").ToLowerInvariant() : null);
        if (array.Any((NativeMount x) => x.Enabled) && (text == null || !Regex.IsMatch(text, "^[0-9A-F]{64}$") || text.All((char x) => x == '0') || text2 != "67591deee07ab2ce86fc9b72c42e8b64c95ba1b3"))
        {
            throw new NxError("invalid_argument", "Enabled native mounts require the exact nonzero 64-hex main NSO expectedBuildId and this backendRevision. Unknown identities cannot mount.");
        }
        NativeMount[] array2 = array;
        foreach (NativeMount nativeMount in array2)
        {
            bool flag = !Regex.IsMatch(nativeMount.Id, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$");
            if (!flag)
            {
                string id = nativeMount.Id;
                bool flag2 = ((id == "." || id == "..") ? true : false);
                flag = flag2;
            }
            if (flag)
            {
                throw new NxError("invalid_argument", "Mount ID must be a safe 1..64 character identifier.");
            }
            RejectLinks(nativeMount.SourcePath);
            if (!Directory.Exists(nativeMount.SourcePath))
            {
                throw new NxError("invalid_argument", "Each native mount source must be an existing directory.");
            }
            if (IsInside(_profileRoot, nativeMount.SourcePath))
            {
                throw new NxError("invalid_argument", "Import mod payloads from outside the managed profile to avoid self-copy and ownership ambiguity.");
            }
            foreach (string item in Directory.EnumerateFileSystemEntries(nativeMount.SourcePath))
            {
                switch (System.IO.Path.GetFileName(item).ToLowerInvariant())
                {
                    case "romfs":
                    case "exefs":
                    case "romfs.bin":
                    case "exefs.nsp":
                        flag = true;
                        break;
                    default:
                        flag = false;
                        break;
                }
                if (!flag)
                {
                    throw new NxError("unsupported", "This native mount adapter accepts only RomFS/ExeFS directories or romfs.bin/exefs.nsp containers. Other payload types require a separate adapter.");
                }
            }
        }
        string path = System.IO.Path.Combine(GameDirectory(num), "nx-mods.json");
        ModConfiguration modConfiguration = (File.Exists(path) ? ReadProfileJson<ModConfiguration>(path) : null);
        byte[] array3 = (File.Exists(path) ? File.ReadAllBytes(path) : null);
        string applicationDir = ModLoader.GetApplicationDir(ModLoader.GetModsBasePath(), num.ToString("x16"));
        string text3 = Guid.NewGuid().ToString("N");
        string text4 = System.IO.Path.Combine(_profileRoot, "nx-mod-staging", text3);
        string text5 = System.IO.Path.Combine(_profileRoot, "nx-mod-backups", num.ToString("x16"), text3);
        Directory.CreateDirectory(text4);
        List<BoundMount> list = new List<BoundMount>();
        try
        {
            array2 = array;
            foreach (NativeMount nativeMount2 in array2)
            {
                string target = System.IO.Path.Combine(applicationDir, "nx-" + nativeMount2.Id);
                if (Directory.Exists(target) && ((object)modConfiguration == null || !modConfiguration.Mounts.Any((BoundMount x) => x.NativePath == target)))
                {
                    throw new NxError("invalid_state", "The selected native target already exists without an NX ownership receipt. No user mod was replaced.");
                }
                MountedFile[] files = (nativeMount2.Enabled ? CopyModPayload(nativeMount2.SourcePath, System.IO.Path.Combine(text4, "nx-" + nativeMount2.Id)) : Array.Empty<MountedFile>());
                list.Add(new BoundMount(nativeMount2, target, files));
            }
            ModConfiguration value4 = new ModConfiguration(1, Hex(num), text, text2 ?? "67591deee07ab2ce86fc9b72c42e8b64c95ba1b3", list.ToArray());
            if (JsonSerializer.SerializeToUtf8Bytes(value4, HostJson).Length > 4194304)
            {
                throw new NxError("invalid_argument", "The copied native mod inventory exceeds the 4 MiB profile metadata limit. No native mount was changed.");
            }
            string path2 = System.IO.Path.Combine(_profileRoot, ".nx-mod-transaction.json");
            WriteJsonAtomic(path2, new
            {
                version = 1,
                titleId = Hex(num),
                staging = text4,
                backup = text5,
                previous = modConfiguration,
                next = list
            });
            List<(string, string)> list2 = new List<(string, string)>();
            List<string> list3 = new List<string>();
            try
            {
                Directory.CreateDirectory(text5);
                BoundMount[] array4 = modConfiguration?.Mounts ?? Array.Empty<BoundMount>();
                foreach (BoundMount boundMount in array4)
                {
                    string text6 = System.IO.Path.Combine(applicationDir, "nx-" + boundMount.Mount.Id);
                    if (boundMount.NativePath != text6)
                    {
                        throw new NxError("invalid_state", "Native mod ownership metadata does not match its exact managed path.");
                    }
                    if (Directory.Exists(text6))
                    {
                        RejectLinks(text6);
                        string text7 = System.IO.Path.Combine(text5, System.IO.Path.GetFileName(text6));
                        Directory.Move(text6, text7);
                        list2.Add((text7, text6));
                    }
                }
                foreach (BoundMount item2 in list.Where((BoundMount x) => x.Mount.Enabled))
                {
                    Directory.Move(System.IO.Path.Combine(text4, "nx-" + item2.Mount.Id), item2.NativePath);
                    list3.Add(item2.NativePath);
                }
                WriteJsonAtomic(path, value4);
                File.Delete(path2);
            }
            catch
            {
                try
                {
                    foreach (string item3 in list3)
                    {
                        if (Directory.Exists(item3))
                        {
                            Directory.Move(item3, System.IO.Path.Combine(text4, System.IO.Path.GetFileName(item3)));
                        }
                    }
                    foreach (var item4 in list2)
                    {
                        Directory.Move(item4.Item1, item4.Item2);
                    }
                    if (array3 == null)
                    {
                        File.Delete(path);
                    }
                    else
                    {
                        File.WriteAllBytes(path, array3);
                    }
                    File.Delete(path2);
                }
                catch
                {
                    throw new NxError("core_error", "Native mod deployment failed and recovery is incomplete. The profile transaction marker and all prior payloads are retained.");
                }
                throw;
            }
        }
        finally
        {
            if (!File.Exists(System.IO.Path.Combine(_profileRoot, ".nx-mod-transaction.json")) && Directory.Exists(text4))
            {
                Directory.Delete(text4, recursive: true);
            }
        }
        return ModState(num);
    }

    private static bool IsInside(string root, string candidate)
    {
        string relativePath = System.IO.Path.GetRelativePath(root, candidate);
        if (!(relativePath == "."))
        {
            if (!System.IO.Path.IsPathRooted(relativePath) && relativePath != "..")
            {
                return !relativePath.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
            }
            return false;
        }
        return true;
    }

    private static MountedFile[] CopyModPayload(string source, string target)
    {
        List<MountedFile> list = new List<MountedFile>();
        long num = 0L;
        Directory.CreateDirectory(target);
        Stack<string> stack = new Stack<string>();
        stack.Push(source);
        while (stack.Count > 0)
        {
            string path = stack.Pop();
            RejectLinks(path);
            foreach (string item in Directory.EnumerateFileSystemEntries(path))
            {
                RejectLinks(item);
                string relativePath = System.IO.Path.GetRelativePath(source, item);
                string text = System.IO.Path.Combine(target, relativePath);
                if (Directory.Exists(item))
                {
                    Directory.CreateDirectory(text);
                    stack.Push(item);
                    continue;
                }
                long length = new FileInfo(item).Length;
                num = checked(num + length);
                if (list.Count >= 100000 || num > 34359738368L)
                {
                    throw new NxError("invalid_argument", "Native mod payload exceeds 100,000 files or 32 GiB.");
                }
                File.Copy(item, text, overwrite: false);
                using FileStream fileStream = File.OpenRead(text);
                list.Add(new MountedFile(relativePath.Replace('\\', '/'), fileStream.Length, Convert.ToHexString(SHA256.HashData(fileStream))));
            }
        }
        if (list.Count == 0)
        {
            throw new NxError("invalid_argument", "An enabled mod must contain a nonempty native payload.");
        }
        return list.OrderBy((MountedFile x) => x.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private void VerifyModIdentityBeforeLoad(InspectedContent content)
    {
        if (content.TitleId == null)
        {
            return;
        }
        ulong title = ParseTitle(content.TitleId);
        string path = System.IO.Path.Combine(GameDirectory(title), "nx-mods.json");
        if (!File.Exists(path))
        {
            return;
        }
        ModConfiguration modConfiguration = ReadProfileJson<ModConfiguration>(path);
        if (!modConfiguration.Mounts.Any((BoundMount x) => x.Mount.Enabled))
        {
            return;
        }
        if (modConfiguration.TitleId != content.TitleId || modConfiguration.BackendRevision != "67591deee07ab2ce86fc9b72c42e8b64c95ba1b3" || content.BuildIdSource != "main_nso" || !content.IdentityVerified || content.BuildId != modConfiguration.ExpectedBuildId)
        {
            throw new NxError("invalid_state", "Native mods are blocked: the selected main NSO, update or backend revision does not match the exact stored binding.");
        }
        string applicationDir = ModLoader.GetApplicationDir(ModLoader.GetModsBasePath(), title.ToString("x16"));
        HashSet<string> ownedRoots = (from x in modConfiguration.Mounts
                                      where x.Mount.Enabled
                                      select x.NativePath).ToHashSet(StringComparer.Ordinal);
        if (Directory.EnumerateFileSystemEntries(applicationDir).Any((string x) => !ownedRoots.Contains(x)))
        {
            throw new NxError("invalid_state", "Additional native mod files exist outside the exact bound NX mount set. Review and isolate that configuration before loading.");
        }
        foreach (BoundMount item in modConfiguration.Mounts.Where((BoundMount x) => x.Mount.Enabled))
        {
            string text = System.IO.Path.Combine(ModLoader.GetApplicationDir(ModLoader.GetModsBasePath(), title.ToString("x16")), "nx-" + item.Mount.Id);
            if (item.NativePath != text || !Directory.Exists(text))
            {
                throw new NxError("invalid_state", "The native mount no longer matches its recorded owned directory.");
            }
            RejectLinks(text);
            HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
            Stack<string> stack = new Stack<string>();
            stack.Push(text);
            while (stack.Count > 0)
            {
                string path2 = stack.Pop();
                RejectLinks(path2);
                foreach (string item2 in Directory.EnumerateFileSystemEntries(path2))
                {
                    RejectLinks(item2);
                    if (Directory.Exists(item2))
                    {
                        stack.Push(item2);
                    }
                    else
                    {
                        hashSet.Add(System.IO.Path.GetRelativePath(text, item2).Replace('\\', '/'));
                    }
                }
            }
            if (!hashSet.SetEquals(item.Files.Select((MountedFile x) => x.RelativePath)))
            {
                throw new NxError("invalid_state", "The native mod file set changed after deployment.");
            }
            MountedFile[] files = item.Files;
            foreach (MountedFile mountedFile in files)
            {
                string text2 = System.IO.Path.Combine(text, mountedFile.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!IsInside(text, text2))
                {
                    throw new NxError("invalid_state", "Native mod inventory contains an unsafe path.");
                }
                using FileStream fileStream = File.OpenRead(text2);
                if (fileStream.Length != mountedFile.Bytes || Convert.ToHexString(SHA256.HashData(fileStream)) != mountedFile.Sha256)
                {
                    throw new NxError("invalid_state", "Native mod bytes changed after deployment. Re-import through the SDK before loading.");
                }
            }
        }
    }

    private object NativeSaves(JsonElement p)
    {
        Require("initialized", "loaded", "running", "paused");
        ulong? num = ((p.TryGetProperty("titleId", out var value) && value.ValueKind != JsonValueKind.Null) ? new ulong?(ReadTitle(p)) : ((ulong?)null));
        List<object> list = new List<object>();
        List<string> limitations = new List<string> { "Mappings come from the native LibHac User save-data index. Their presence does not prove a guest wrote data.", "Save backup/import must wait for full worker disposal and acquire the runtime data lease. Runtime save discovery does not release that lease.", "The adapter format is a complete Ryujinx directory-save container, including committed/working data and ExtraData. It is not an interchange format for Eden." };
        using UniqueRef<SaveDataIterator> uniqueRef = default;
        _horizon.RyujinxClient.Fs.OpenSaveDataIterator(ref uniqueRef.Ref, SaveDataSpaceId.User).ThrowIfFailure();
        Span<SaveDataInfo> buffer = stackalloc SaveDataInfo[8];
        while (true)
        {
            uniqueRef.Get.ReadSaveDataInfo(out var readCount, buffer).ThrowIfFailure();
            if (readCount == 0L)
            {
                break;
            }
            for (int i = 0; i < readCount; i++)
            {
                SaveDataInfo saveDataInfo = buffer[i];
                if (num.HasValue)
                {
                    ulong valueOrDefault = num.GetValueOrDefault();
                    if (saveDataInfo.ProgramId.Value != valueOrDefault)
                    {
                        continue;
                    }
                }
                string text = System.IO.Path.Combine(VirtualFileSystem.GetNandPath(), "user", "save", saveDataInfo.SaveDataId.ToString("x16"));
                RejectLinks(text);
                if (!IsInside(_profileRoot, text))
                {
                    throw new NxError("core_error", "Native indexed save path escaped the initialized profile.");
                }
                bool flag = Directory.Exists(text);
                list.Add(new
                {
                    backendId = "nxi",
                    titleId = saveDataInfo.ProgramId.Value.ToString("X16"),
                    userId = saveDataInfo.UserId.ToString().ToUpperInvariant(),
                    saveDataId = saveDataInfo.SaveDataId.ToString("X16"),
                    type = saveDataInfo.Type.ToString().ToLowerInvariant(),
                    formatId = "ryujinx-directory-save",
                    formatVersion = "1",
                    relativePath = System.IO.Path.GetRelativePath(_profileRoot, text).Replace('\\', '/'),
                    existing = flag,
                    adapterVerified = flag,
                    provenance = "LibHac0.20.0 SaveDataIterator(User): ProgramId/UserId/SaveDataId index and pinned VirtualFileSystem.GetNandPath()/user/save/<indexed SaveDataId>; native directory verified."
                });
                if (list.Count > 4096)
                {
                    throw new NxError("core_error", "Native save index exceeded the protocol catalog bound.");
                }
            }
        }
        return new
        {
            entries = list,
            limitations = limitations
        };
    }

    private object ConfigureNetwork(JsonElement parameters)
    {
        Require("created");
        string text = Fields.String(parameters, "provider", "offline");
        string server = Fields.String(parameters, "server", "");
        string text2 = Fields.String(parameters, "passphrase", "");
        try
        {
            server = NetworkPolicy.Validate(text, server, text2);
        }
        catch (ArgumentException ex)
        {
            throw new NxError("invalid_argument", ex.Message);
        }
        lock (_networkGate)
        {
            _networkProvider = text;
            _networkServer = server;
            _networkPassphrase = text2;
            _networkStage = ((text == "offline") ? "disabled" : "configured");
            _networkError = null;
        }
        return NetworkState();
    }

    private object NetworkState()
    {
        lock (_networkGate)
        {
            return new
            {
                provider = _networkProvider,
                server = _networkServer,
                port = 30456,
                privateSession = (_networkPassphrase.Length > 0),
                relayOnly = true,
                guestInternetEnabled = false,
                nativeConfigurationApplied = (_device != null),
                stage = ((_networkStage == "configured" && _device != null) ? "awaiting_game" : _networkStage),
                errorCode = _networkError,
                gameplayVerified = false
            };
        }
    }

    private void ObserveNetworkLog(LogEventArgs entry)
    {
        string message = default;
        int num;
        if (!(_networkProvider != "ryuldn"))
        {
            message = entry.Message;
            num = ((message == null) ? 1 : 0);
        }
        else
        {
            num = 1;
        }
        bool flag = (byte)num != 0;
        if (!flag)
        {
            LogLevel level = entry.Level;
            bool flag2 = ((level == LogLevel.Info || level == LogLevel.Error) ? true : false);
            flag = !flag2;
        }
        if (flag || !message.StartsWith("ServiceLdn ", StringComparison.Ordinal))
        {
            return;
        }
        string text = null;
        string networkError = null;
        if (message.Contains("Initializing with multiplayer mode: LdnRyu", StringComparison.Ordinal))
        {
            text = "connecting";
        }
        else if (message.Contains("LDN TCP client connected a new session", StringComparison.Ordinal))
        {
            text = "relay_connected";
        }
        else if (message.Contains("LDN TCP client disconnected a session", StringComparison.Ordinal))
        {
            text = "disconnected";
        }
        else if (message.Contains("LDN TCP client caught an error", StringComparison.Ordinal))
        {
            text = "error";
            networkError = "connection_failed";
        }
        else if (message.Contains("Could not locate RyuLDN server. Defaulting to stubbed wireless.", StringComparison.Ordinal))
        {
            text = "error";
            networkError = "dns_failed";
        }
        if (text != null)
        {
            lock (_networkGate)
            {
                _networkStage = text;
                _networkError = networkError;
            }
            _protocol.Event("network.state", NetworkState());
        }
    }

    private void TraceGpuPhase(string phase)
    {
        if (RyuStutterTrace.Enabled)
        {
            Volatile.Write(ref _gpuLoopPhaseStart, Stopwatch.GetTimestamp());
            Volatile.Write(ref _gpuLoopPhase, phase);
        }
    }

    private object GpuLoopSnapshot()
    {
        return new
        {
            enabled = RyuStutterTrace.Enabled,
            phase = Volatile.Read(in _gpuLoopPhase),
            startTicks = Volatile.Read(in _gpuLoopPhaseStart),
            queue = (_coreRenderer as ThreadedRenderer)?.CommandQueueSnapshot()
        };
    }

    private void ConfigureRendererObservation()
    {
        _renderer.FramePresentationCompleted += (bool accepted) =>
        {
            _device?.FramePolicy.RecordPresentResult(accepted);
        };
        _observedRenderer = new ObservedRenderer(_renderer, () =>
        {
            RyuStutterTrace.Present();
            _presentTiming.RecordPresentReturn();
            Interlocked.Increment(ref _presentedFrames);
        }, NativeWindowVsyncChanged);
        IRenderer coreRenderer;
        if (!(_rendererThreading == "threaded_renderer"))
        {
            IRenderer observedRenderer = _observedRenderer;
            coreRenderer = observedRenderer;
        }
        else
        {
            IRenderer observedRenderer = new ThreadedRenderer(_observedRenderer);
            coreRenderer = observedRenderer;
        }
        _coreRenderer = coreRenderer;
    }

    private static string WindowVsyncName(Ryujinx.Graphics.GAL.VSyncMode mode)
    {
        return mode switch
        {
            Ryujinx.Graphics.GAL.VSyncMode.Switch => "switch",
            Ryujinx.Graphics.GAL.VSyncMode.Unbounded => "unbounded",
            Ryujinx.Graphics.GAL.VSyncMode.Custom => "custom",
            _ => null,
        };
    }

    private void NativeWindowVsyncChanged(Ryujinx.Graphics.GAL.VSyncMode mode)
    {
        lock (_presentationGate)
        {
            _windowVsyncMode = WindowVsyncName(mode);
            _windowVsyncSetterCalls++;
        }
        _windowVsyncChanged.Set();
    }

    private void ApplyWindowVsync(Ryujinx.Graphics.GAL.VSyncMode mode)
    {
        _windowVsyncChanged.Reset();
        try
        {
            OnGpu(() =>
            {
                _observedRenderer.Window.ChangeVSyncMode(mode);
            });
            WaitWhilePumping(_windowVsyncChanged, TimeSpan.FromSeconds(10L));
        }
        catch
        {
            lock (_presentationGate)
            {
                _windowVsyncMode = null;
            }
            throw;
        }
    }

    private object ReadEffectiveVsync()
    {
        if (_device == null)
        {
            return null;
        }
        string text = WindowVsyncName((Ryujinx.Graphics.GAL.VSyncMode)_device.VSyncMode);
        lock (_presentationGate)
        {
            return (_windowVsyncMode == text) ? text : null;
        }
    }

    private object VsyncConsumerState()
    {
        lock (_presentationGate)
        {
            return new
            {
                schemaVersion = 1,
                guestVsyncMode = ((_device == null) ? null : WindowVsyncName((Ryujinx.Graphics.GAL.VSyncMode)_device.VSyncMode)),
                guestTargetInterval = ((_device == null) ? ((double?)null) : new double?(_device.TargetVSyncInterval)),
                rendererVsyncMode = _windowVsyncMode,
                rendererSetterCalls = _windowVsyncSetterCalls,
                rendererEffectiveSource = "renderer_window_change_vsync_mode_return",
                windowVisible = _windowVisible,
                windowMinimized = _windowMinimized,
                timingSuspendedForWindow = _windowPresentationSuspended,
                actualSwapchainPresentMode = (string)null,
                physicalScanoutVerified = false
            };
        }
    }

    private void NativeShaderCacheStateChanged(ShaderCacheState state, int completed, int total)
    {
        if (_shaderCompilation.NativeStateChanged(state.ToString(), completed, total))
        {
            _protocol.Event("shader.progress", new
            {
                shaderCompilation = _shaderCompilation.Snapshot()
            });
        }
    }

    private void GpuCommandLoop()
    {
        try
        {
            _device.Gpu.SetGpuThread();
            _shaderCompilation.BeginCacheLoad(GraphicsConfig.EnableShaderCache);
            _device.Gpu.InitializeShaderCache(_shutdown.Token);
            _shaderCompilation.CacheLoadReturned();
            _protocol.Event("shader.progress", new
            {
                shaderCompilation = _shaderCompilation.Snapshot()
            });
            while (_renderActive)
            {
                TraceGpuPhase("host_actions");
                Action result;
                while (_gpuActions.TryDequeue(out result))
                {
                    result();
                }
                ApplyPendingWindowPixelSize();
                TraceGpuPhase("fifo_wait");
                if (_device.WaitFifo())
                {
                    _device.Statistics.RecordFifoStart();
                    TraceGpuPhase("fifo_process");
                    _device.ProcessFrame();
                    _device.Statistics.RecordFifoEnd();
                }
                TraceGpuPhase("frame_available");
                while (_renderActive && _device.ConsumeFrameAvailable())
                {
                    TraceGpuPhase("present");
                    _device.PresentFrame(() =>
                    {
                    });
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            RenderingFailed(error);
        }
        finally
        {
            try
            {
                if (_coreRenderer is ThreadedRenderer threadedRenderer)
                {
                    threadedRenderer.FlushThreadedCommands();
                }
            }
            catch (Exception error2)
            {
                RenderingFailed(error2);
            }
            _gpuLoopStopped.Set();
        }
    }

    private void RenderingFailed(Exception error)
    {
        Console.Error.WriteLine(error);
        _gpuError = error;
        _renderActive = false;
        _protocol.Event("core.error", new
        {
            operation = "render",
            exception = error.GetType().Name,
            message = "GPU worker failed; gameplay output is unavailable."
        });
    }

    private static IReadOnlyDictionary<string, string> ReadCompiledPerformancePolicies()
    {
        string value = ReadCompiledPolicy(typeof(GpuContext).Assembly, "NX.Kenji.CacheStallPolicy", "original-v1", "bounded-cache-v1");
        string value2 = ReadCompiledPolicy(typeof(VulkanRenderer).Assembly, "NX.Kenji.VulkanLinkStatusPolicy", "original-v1", "terminal-reread-v1");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["shaderCacheScheduler"] = value,
            ["cacheReadRecovery"] = value,
            ["shaderLinkStatus"] = value2,
            ["protocolInput"] = "buffered-v1",
            ["windowDispatch"] = "bounded-events-v1"
        };
    }

    private static string ReadCompiledPolicy(Assembly assembly, string key, params string[] known)
    {
        string[] array = (from attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                          where attribute.Key == key
                          select attribute.Value).ToArray();
        if (array.Length != 1 || !Enumerable.Contains(known, array[0], StringComparer.Ordinal))
        {
            return "unverified";
        }
        return array[0];
    }

    private object Prerequisites(JsonElement p)
    {
        return WithProfile(p, PrerequisiteReport);
    }

    private object PrerequisiteReport()
    {
        List<Issue> list = new List<Issue>();
        bool flag = new string[4] { "prod.keys", "dev.keys", "title.keys", "console.keys" }.Any((string name) => File.Exists(System.IO.Path.Combine(AppDataManager.KeysDirPath, name)));
        bool flag2 = !_vfs.KeySet.HeaderKey.IsZeros();
        string text = null;
        if (!flag)
        {
            list.Add(new Issue("keys_missing", "warning", "No user-supplied keys are installed in this isolated profile. Unencrypted authored homebrew may still load."));
        }
        if (!flag2)
        {
            list.Add(new Issue("header_key_missing", "warning", "The native NCA parser has no nonzero header key. Encrypted content inspection is unavailable."));
        }
        else
        {
            list.Add(new Issue("key_coverage_unverified", "info", "A nonzero header key is loaded; only opening each selected encrypted content verifies that its required keys work."));
        }
        try
        {
            text = _contentManager.GetCurrentFirmwareVersion()?.VersionString;
        }
        catch (Exception ex)
        {
            list.Add(new Issue("firmware_unreadable", "error", "Installed firmware could not be read by the pinned core (" + ex.GetType().Name + ")."));
        }
        if (text == null)
        {
            list.Add(new Issue("firmware_missing", "warning", "No readable installed firmware version was found. Firmware-dependent games and applets require a user-supplied verified package."));
        }
        return new
        {
            keysPresent = flag,
            headerKeyUsable = flag2,
            firmwareVersion = text,
            issues = list
        };
    }

    private object ImportPrerequisite(JsonElement p)
    {
        Require("created", "initialized");
        string kind = Fields.RequiredString(p, "kind");
        string text = kind;
        if ((!(text == "keys") && !(text == "firmware")) || 1 == 0)
        {
            throw new NxError("invalid_argument", "Prerequisite kind must be keys or firmware.");
        }
        string path = AbsolutePath(Fields.RequiredString(p, "sourcePath"));
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new NxError("invalid_argument", "The selected local prerequisite does not exist.");
        }
        RejectLinks(path);
        return WithProfile(p, () =>
        {
            if (kind == "keys")
            {
                ImportKeys(path);
            }
            else
            {
                ImportFirmware(path);
            }
            return PrerequisiteReport();
        });
    }

    private void ImportKeys(string path)
    {
        bool flag = !File.Exists(path);
        if (!flag)
        {
            long length = new FileInfo(path).Length;
            bool flag2 = ((length <= 0 || length > 1048576) ? true : false);
            flag = flag2;
        }
        if (flag)
        {
            throw new NxError("invalid_argument", "Select one nonempty prod.keys, title.keys, dev.keys or console.keys file of at most 1 MiB.");
        }
        try
        {
            _contentManager.VerifyKeysFile(path);
        }
        catch (Exception)
        {
            throw new NxError("invalid_argument", "The selected keys file failed the native filename/format check. Key bytes are never returned by the API.");
        }
        string text = System.IO.Path.Combine(AppDataManager.KeysDirPath, System.IO.Path.GetFileName(path));
        if (string.Equals(path, text, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            ReloadFreshProfileKeys();
            return;
        }
        string text2 = Guid.NewGuid().ToString("N");
        string text3 = System.IO.Path.Combine(AppDataManager.KeysDirPath, ".nx-key-stage-" + text2);
        string text4 = text + ".nx-backup-" + text2;
        Directory.CreateDirectory(text3);
        bool flag3 = false;
        try
        {
            _contentManager.InstallKeys(path, text3);
            string sourceFileName = System.IO.Path.Combine(text3, System.IO.Path.GetFileName(path));
            RejectLinks(text);
            if (File.Exists(text))
            {
                File.Copy(text, text4, overwrite: false);
            }
            File.Move(sourceFileName, text, overwrite: true);
            flag3 = true;
            ReloadFreshProfileKeys();
        }
        catch
        {
            if (flag3)
            {
                if (File.Exists(text4))
                {
                    File.Copy(text4, text, overwrite: true);
                }
                else
                {
                    File.Delete(text);
                }
                ReloadFreshProfileKeys();
            }
            throw new NxError("core_error", "The native keys import failed. The prior profile file was retained or restored.");
        }
        finally
        {
            if (Directory.Exists(text3))
            {
                Directory.Delete(text3, recursive: true);
            }
        }
    }

    private void ReloadFreshProfileKeys()
    {
        _vfs.KeySet.KeyStruct = KeySet.CreateDefaultKeySet().KeyStruct;
        _vfs.KeySet.ExternalKeySet.Clear();
        _vfs.ReloadKeySet();
    }

    private void ImportFirmware(string path)
    {
        if (_vfs.KeySet.HeaderKey.IsZeros())
        {
            throw new NxError("missing_dependency", "Install usable user-supplied keys before validating firmware.");
        }
        bool flag = !Directory.Exists(path);
        if (flag)
        {
            string extension = System.IO.Path.GetExtension(path);
            bool flag2 = ((extension == ".zip" || extension == ".xci") ? true : false);
            flag = !flag2;
        }
        if (flag)
        {
            throw new NxError("invalid_argument", "Native firmware import accepts a local directory, .zip package, or .xci containing an update partition.");
        }
        SystemVersion systemVersion;
        try
        {
            systemVersion = _contentManager.VerifyFirmwarePackage(path);
        }
        catch (Exception ex)
        {
            throw new NxError("invalid_argument", "Native firmware validation failed (" + ex.GetType().Name + "). No installed firmware was changed.");
        }
        if (systemVersion == null)
        {
            throw new NxError("invalid_argument", "The native parser did not find a complete firmware package.");
        }
        string text = System.IO.Path.Combine(VirtualFileSystem.GetNandPath(), "system", "Contents");
        RejectLinks(text);
        string text2 = System.IO.Path.Combine(text, "registered");
        string text3 = System.IO.Path.Combine(text, "temp");
        string text4 = Guid.NewGuid().ToString("N");
        string text5 = System.IO.Path.Combine(text, "nx-previous-firmware-" + text4);
        string destDirName = System.IO.Path.Combine(text, "nx-previous-stage-" + text4);
        string path2 = System.IO.Path.Combine(_profileRoot, ".nx-firmware-transaction.json");
        Directory.CreateDirectory(text);
        WriteJsonAtomic(path2, new
        {
            version = 1,
            operation = "native-firmware-install",
            expectedVersion = systemVersion.VersionString,
            registered = System.IO.Path.GetRelativePath(_profileRoot, text2),
            backup = System.IO.Path.GetRelativePath(_profileRoot, text5),
            phase = "prepared",
            priorFirmwarePresent = Directory.Exists(text2)
        });
        bool flag3 = false;
        try
        {
            if (Directory.Exists(text3))
            {
                Directory.Move(text3, destDirName);
            }
            if (Directory.Exists(text2))
            {
                Directory.Move(text2, text5);
                flag3 = true;
            }
            _contentManager.InstallFirmware(path);
            string text6 = _contentManager.GetCurrentFirmwareVersion()?.VersionString;
            if (text6 != systemVersion.VersionString)
            {
                throw new InvalidDataException("Installed firmware version did not match the verified source.");
            }
            File.Delete(path2);
            _protocol.Event("prerequisites.changed", new
            {
                kind = "firmware",
                firmwareVersion = text6,
                nativeValidated = true
            });
        }
        catch (Exception ex2)
        {
            try
            {
                if (Directory.Exists(text2))
                {
                    Directory.Move(text2, System.IO.Path.Combine(text, "nx-failed-firmware-" + text4));
                }
                if (flag3)
                {
                    Directory.Move(text5, text2);
                }
                _contentManager.LoadEntries();
                File.Delete(path2);
            }
            catch
            {
                throw new NxError("core_error", "Firmware import failed and automatic recovery could not finish. The profile transaction marker and prior files are retained for recovery.");
            }
            throw new NxError("core_error", "Native firmware installation failed (" + ex2.GetType().Name + "). The previous registered content was restored.");
        }
    }

    private static void WriteJsonAtomic(string path, object value, JsonSerializerOptions options = null)
    {
        RejectLinks(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
        string text = path + ".nx-temp-" + Guid.NewGuid().ToString("N");
        try
        {
            byte[] array = JsonSerializer.SerializeToUtf8Bytes(value, options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            });
            using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fileStream.Write(array);
                fileStream.Flush(flushToDisk: true);
            }
            File.Move(text, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(text))
            {
                File.Delete(text);
            }
        }
    }

    public object Dispatch(string method, JsonElement parameters)
    {
        object result = DispatchCore(method, parameters);
        bool flag;
        switch (method)
        {
            case "initialize":
                _programInitialize = parameters.Clone();
                break;
            case "load":
                _programLoad = parameters.Clone();
                break;
            case "network.configure":
            case "settings.set":
            case "input.routing.set":
            case "input.assign":
            case "window.set":
                flag = true;
                goto IL_008e;
            default:
                {
                    flag = false;
                    goto IL_008e;
                }
            IL_008e:
                if (flag)
                {
                    string text = method;
                    string? text2;
                    if (method == "settings.set")
                    {
                        text2 = ":" + Fields.RequiredString(parameters, "id");
                    }
                    else
                    {
                        text2 = ((method == "input.assign" && parameters.TryGetProperty("player", out var value)) ? (":" + value.GetRawText()) : "");
                    }
                    string key = text + text2;
                    _programReplay[key] = (method, parameters.Clone());
                }
                break;
        }
        return result;
    }

    private void QueueProgramTransition(ProgramSpecifyKind kind, ulong value)
    {
        bool flag = ProgramTransitionPending;
        if (!flag)
        {
            bool flag2;
            switch (_state)
            {
                case "running":
                case "paused":
                case "loaded":
                    flag2 = true;
                    break;
                default:
                    flag2 = false;
                    break;
            }
            flag = !flag2;
        }
        if (flag)
        {
            return;
        }
        string path = ((_programLoad.ValueKind == JsonValueKind.Object) ? Fields.RequiredString(_programLoad, "contentPath") : "");
        flag = kind switch
        {
            ProgramSpecifyKind.RestartProgram => (value == 0L || value == _runtimeProgramId) ? 1 : 0,
            ProgramSpecifyKind.ExecuteProgram => (value < 16) ? 1 : 0,
            _ => 0,
        } == 0;
        if (!flag)
        {
            bool flag2 = kind == ProgramSpecifyKind.ExecuteProgram;
            if (flag2)
            {
                bool flag3;
                switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
                {
                    case ".nsp":
                    case ".pfs0":
                    case ".xci":
                        flag3 = true;
                        break;
                    default:
                        flag3 = false;
                        break;
                }
                flag2 = !flag3;
            }
            flag = flag2;
        }
        if (flag)
        {
            _protocol.Event("interaction.unavailable", new
            {
                code = "unsupported",
                kind = "program_transition",
                message = "This transition requires another program in the same NSP/PFS0/XCI container."
            });
            Stop();
        }
        else
        {
            _programWasPaused = _state == "paused";
            _programChannel.ExecuteProgram(kind, value);
            ProgramTransitionPending = true;
        }
    }

    public CoreSession CreateProgramSuccessor()
    {
        if (!ProgramTransitionPending)
        {
            throw new InvalidOperationException("No pending program transition.");
        }
        VirtualFileSystem vfs = _vfs;
        ContentManager contentManager = _contentManager;
        string profileRoot = _profileRoot;
        FileStream runtimeLease = _runtimeLease;
        _suppressTransitionStates = true;
        _preserveLeaseForProgramTransition = true;
        Dispose();
        if (RequiresProcessExit)
        {
            throw new InvalidOperationException("Native teardown did not complete.");
        }
        CoreSession coreSession = new CoreSession(_protocol, _diagnostics, _programChannel)
        {
            _vfs = vfs,
            _contentManager = contentManager,
            _profileRoot = profileRoot,
            _runtimeLease = runtimeLease,
            _programInitialize = _programInitialize.Clone(),
            _programLoad = _programLoad.Clone(),
            _programWasPaused = _programWasPaused,
            _programBootstrap = true,
            _suppressTransitionStates = true
        };
        foreach (KeyValuePair<string, (string, JsonElement)> item in _programReplay)
        {
            coreSession._programReplay.Add(item.Key, item.Value);
        }
        return coreSession;
    }

    public void BootProgramSuccessor()
    {
        if (!_programBootstrap)
        {
            throw new InvalidOperationException("No program restart is prepared.");
        }
        (string, JsonElement)[] source = _programReplay.Values.ToArray();
        foreach (var item in source.Where(((string Method, JsonElement Parameters) item) => item.Method == "network.configure"))
        {
            DispatchCore(item.Item1, item.Item2);
        }
        Initialize(_programInitialize);
        foreach (var item2 in source.Where(((string Method, JsonElement Parameters) item) => item.Method != "network.configure"))
        {
            DispatchCore(item2.Item1, item2.Item2);
        }
        Load(_programLoad);
        Start();
        if (_programWasPaused)
        {
            Pause(value: true);
        }
        _programChannel.ShouldRestart = false;
        _programBootstrap = false;
        _suppressTransitionStates = false;
        ChangeState(_state);
        _protocol.Event("content.program_changed", new
        {
            programIndex = _programChannel.Index,
            previousProgramIndex = _programChannel.PreviousIndex,
            patchesReapplied = false
        });
    }

    private void ReleaseTouch()
    {
        _touchDown = false;
        _device?.Hid.Touchscreen.Update(default(ReadOnlySpan<TouchPoint>));
    }

    private unsafe GamepadInput[] UpdateTouchPointer()
    {
        GamepadInput[] array = _input.Where((GamepadInput _, int i) => _configuredPlayers[i]).ToArray();
        if ((SDL3.SDL_GetWindowFlags(_window) & SDL_WindowFlags.SDL_WINDOW_INPUT_FOCUS) == (SDL_WindowFlags)0uL)
        {
            ReleaseTouch();
            return array;
        }
        long timestamp = Stopwatch.GetTimestamp();
        double num = ((_touchAt == 0L) ? 0.0 : Math.Clamp((double)(timestamp - _touchAt) / (double)Stopwatch.Frequency, 0.0, 0.05));
        _touchAt = timestamp;
        GamepadInput gamepadInput = _input[0];
        bool flag = (gamepadInput.Buttons & (ControllerKeys.LStick | ControllerKeys.RStick)) == (ControllerKeys.LStick | ControllerKeys.RStick);
        if (flag && !_touchToggleHeld)
        {
            _touchPointer = !_touchPointer;
            SDL3.SDL_ShowCursor();
        }
        _touchToggleHeld = flag;
        float num2 = 0f;
        float num3 = 0f;
        uint num4 = (uint)SDL3.SDL_GetMouseState(&num2, &num3);
        int num5 = 0;
        int num6 = 0;
        SDL3.SDL_GetWindowSize(_window, &num5, &num6);
        if (num5 <= 0 || num6 <= 0)
        {
            ReleaseTouch();
            return array;
        }
        double num7 = Math.Min((double)num5 / 1280.0, (double)num6 / 720.0);
        double num8 = ((double)num5 - 1280.0 * num7) / 2.0;
        double num9 = ((double)num6 - 720.0 * num7) / 2.0;
        bool flag2;
        if (_touchPointer)
        {
            _touchX = Math.Clamp(_touchX + (double)gamepadInput.RStick.Dx / 32767.0 * 900.0 * num, 0.0, 1279.0);
            _touchY = Math.Clamp(_touchY - (double)gamepadInput.RStick.Dy / 32767.0 * 900.0 * num, 0.0, 719.0);
            SDL3.SDL_WarpMouseInWindow(_window, (float)(num8 + _touchX * num7), (float)(num9 + _touchY * num7));
            flag2 = (gamepadInput.Buttons & ControllerKeys.A) != 0;
            for (int num10 = 0; num10 < array.Length; num10++)
            {
                if (array[num10].PlayerId == PlayerIndex.Player1)
                {
                    array[num10].Buttons &= ~(ControllerKeys.A | ControllerKeys.LStick | ControllerKeys.RStick);
                    array[num10].RStick = default;
                }
            }
        }
        else
        {
            flag2 = (double)num2 >= num8 && (double)num3 >= num9 && (double)num2 < num8 + 1280.0 * num7 && (double)num3 < num9 + 720.0 * num7 && (num4 & 1) != 0 && (SDL3.SDL_GetWindowFlags(_window) & SDL_WindowFlags.SDL_WINDOW_INPUT_FOCUS) != 0;
            if (flag2)
            {
                _touchX = ((double)num2 - num8) / num7;
                _touchY = ((double)num3 - num9) / num7;
            }
        }
        if (flag2 || _touchDown)
        {
            _device.Hid.Touchscreen.Update(new TouchPoint
            {
                X = (uint)Math.Clamp(_touchX, 0.0, 1279.0),
                Y = (uint)Math.Clamp(_touchY, 0.0, 719.0),
                DiameterX = 10u,
                DiameterY = 10u,
                Attribute = ((!flag2) ? TouchAttribute.End : ((!_touchDown) ? TouchAttribute.Start : TouchAttribute.None))
            });
        }
        else
        {
            _device.Hid.Touchscreen.Update(default(ReadOnlySpan<TouchPoint>));
        }
        _touchDown = flag2;
        return array;
    }

    private unsafe void ObserveWindowPresentationState()
    {
        if (_window != null)
        {
            SDL_WindowFlags sDL_WindowFlags = SDL3.SDL_GetWindowFlags(_window);
            _windowVisible = (sDL_WindowFlags & SDL_WindowFlags.SDL_WINDOW_HIDDEN) == 0;
            _windowMinimized = (sDL_WindowFlags & SDL_WindowFlags.SDL_WINDOW_MINIMIZED) != 0;
            _windowPresentationSuspended = !_windowVisible || _windowMinimized;
            _presentTiming.SetRunning(_state == "running" && !_windowPresentationSuspended && !_shutdown.IsCancellationRequested);
            _device?.FramePolicy.SetRunning(_state == "running" && !_windowPresentationSuspended && !_shutdown.IsCancellationRequested);
        }
    }

    private void CloseGameWindow(string reason)
    {
        bool flag;
        switch (_state)
        {
            case "initialized":
            case "loaded":
            case "running":
            case "paused":
                flag = true;
                break;
            default:
                flag = false;
                break;
        }
        if (flag)
        {
            _inputTarget = "ui";
            NeutralizeGuestInput();
            _protocol.Event("window.close_requested", new
            {
                reason = reason,
                sessionOnly = true
            });
            Stop();
            _protocol.Event("window.closed", new
            {
                reason = reason,
                state = _state,
                sessionOnly = true,
                resourcesDisposed = true
            });
        }
    }

    private void RequestInteractionInputCapture()
    {
        Interlocked.Exchange(ref _interactionInputCaptureRequested, 1);
        if (Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            ApplyInteractionInputCapture();
        }
    }

    private void ApplyInteractionInputCapture()
    {
        if (Interlocked.Exchange(ref _interactionInputCaptureRequested, 0) == 0)
        {
            return;
        }
        _inputTarget = "ui";
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }
        NeutralizeGuestInput();
        foreach (PhysicalGamepad value in _gamepads.Values)
        {
            value.LastUiState = null;
        }
        _protocol.Event("input.routing_changed", new
        {
            inputTarget = "ui",
            requiresNeutral = false,
            reason = "native_applet"
        });
    }

    private unsafe object WindowState()
    {
        Require("initialized", "loaded", "running", "paused");
        int num = 0;
        int num2 = 0;
        if (!SDL3.SDL_GetWindowSize(_window, &num, &num2))
        {
            throw new NxError("core_error", "SDL could not read the current window size.");
        }
        SDL_WindowFlags sDL_WindowFlags = SDL3.SDL_GetWindowFlags(_window);
        bool flag = (sDL_WindowFlags & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0;
        int value = 0;
        int value2 = 0;
        bool flag2 = SDL3.SDL_GetWindowSizeInPixels(_window, &value, &value2);
        int width = num;
        int height = num2;
        bool fullscreen = flag;
        bool visible = (sDL_WindowFlags & SDL_WindowFlags.SDL_WINDOW_HIDDEN) == 0;
        bool minimized = (sDL_WindowFlags & SDL_WindowFlags.SDL_WINDOW_MINIMIZED) != 0;
        int? pixelWidth = (flag2 ? new int?(value) : ((int?)null));
        int? pixelHeight = (flag2 ? new int?(value2) : ((int?)null));
        bool? fullscreenRequested = _fullscreenRequested;
        bool? fullscreenRequestAccepted = _fullscreenRequestAccepted;
        bool? fullscreenSynchronizationCompleted = _fullscreenSynchronizationCompleted;
        int fullscreenConfirmed;
        if (_windowStateSynchronized)
        {
            bool? fullscreenRequested2 = _fullscreenRequested;
            fullscreenConfirmed = ((!fullscreenRequested2.HasValue || flag == _fullscreenRequested) ? 1 : 0);
        }
        else
        {
            fullscreenConfirmed = 0;
        }
        return new
        {
            width = width,
            height = height,
            fullscreen = fullscreen,
            visible = visible,
            minimized = minimized,
            pixelWidth = pixelWidth,
            pixelHeight = pixelHeight,
            fullscreenRequested = fullscreenRequested,
            fullscreenRequestAccepted = fullscreenRequestAccepted,
            fullscreenSynchronizationCompleted = fullscreenSynchronizationCompleted,
            fullscreenConfirmed = ((byte)fullscreenConfirmed != 0),
            fullscreenEffectiveSource = (_windowStateSynchronized ? "sdl_window_flags_after_sync" : "sdl_window_flags"),
            presentationTimingSuspended = _windowPresentationSuspended,
            frontendWork = new
            {
                pixelSize = _windowPixelSize.Snapshot(),
                pixelSizeSetterCalls = Interlocked.Read(in _windowSizeSetterCalls),
                eventsDispatched = _windowEventsDispatched,
                eventBudgetYields = _windowEventBudgetYields,
                eventsPending = PendingWindowEvents
            }
        };
    }

    private unsafe object SetWindow(JsonElement p)
    {
        Require("initialized", "loaded", "running", "paused");
        int num = 0;
        int num2 = 0;
        SDL3.SDL_GetWindowSize(_window, &num, &num2);
        bool flag = p.TryGetProperty("width", out var value) && value.ValueKind != JsonValueKind.Null;
        bool flag2 = p.TryGetProperty("height", out var value2) && value2.ValueKind != JsonValueKind.Null;
        if (flag | flag2)
        {
            if (flag)
            {
                num = Fields.Integer(p, "width", num, 64, 16384);
            }
            if (flag2)
            {
                num2 = Fields.Integer(p, "height", num2, 64, 16384);
            }
            if (!SDL3.SDL_SetWindowSize(_window, num, num2))
            {
                throw new NxError("core_error", "SDL rejected the requested window size.");
            }
        }
        bool num3 = p.TryGetProperty("fullscreen", out var value3) && value3.ValueKind != JsonValueKind.Null;
        bool flag3 = p.TryGetProperty("visible", out var value4) && value4.ValueKind != JsonValueKind.Null;
        bool force = false;
        if (num3)
        {
            SetFullscreen(Fields.Boolean(p, "fullscreen", fallback: false), emit: false);
        }
        if (flag3)
        {
            bool flag4 = Fields.Boolean(p, "visible", fallback: true);
            force = flag4 != ((SDL3.SDL_GetWindowFlags(_window) & SDL_WindowFlags.SDL_WINDOW_HIDDEN) == 0);
            if (!(flag4 ? SDL3.SDL_ShowWindow(_window) : SDL3.SDL_HideWindow(_window)))
            {
                throw new NxError("core_error", "SDL rejected the window visibility change.");
            }
            if (flag4)
            {
                SDL3.SDL_RaiseWindow(_window);
            }
        }
        if (!num3 | flag3)
        {
            SynchronizeWindow();
        }
        ObserveWindowPresentationState();
        QueueWindowPixelSize(force);
        object obj = WindowState();
        _protocol.Event("window.changed", obj);
        return obj;
    }

    private unsafe void QueueWindowPixelSize(bool force = false)
    {
        int num = 0;
        int num2 = 0;
        if ((bool)SDL3.SDL_GetWindowSizeInPixels(_window, &num, &num2) && num > 0 && num2 > 0)
        {
            _windowPixelSize.Request(num, num2, force);
        }
    }

    private void ApplyPendingWindowPixelSize()
    {
        if (_renderActive && _windowPixelSize.TryTake(out var size))
        {
            _coreRenderer.Window.SetSize(size.Width, size.Height);
            Interlocked.Increment(ref _windowSizeSetterCalls);
        }
    }

    private unsafe void SynchronizeWindow()
    {
        _windowStateSynchronized = false;
        if (!SDL3.SDL_SyncWindow(_window))
        {
            throw new NxError("timeout", "The window system did not complete the requested window transition.");
        }
        _windowStateSynchronized = true;
    }

    private unsafe void SetFullscreen(bool value, bool emit = true)
    {
        bool force = (SDL3.SDL_GetWindowFlags(_window) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0 != value;
        _fullscreenRequested = value;
        _fullscreenRequestAccepted = false;
        _fullscreenSynchronizationCompleted = null;
        _windowStateSynchronized = false;
        if (!SDL3.SDL_SetWindowFullscreen(_window, value))
        {
            _protocol.Event("window.changed", WindowState());
            throw new NxError("core_error", "SDL rejected fullscreen mode.");
        }
        _fullscreenRequestAccepted = true;
        try
        {
            SynchronizeWindow();
            _fullscreenSynchronizationCompleted = true;
        }
        catch
        {
            _fullscreenSynchronizationCompleted = false;
            _protocol.Event("window.changed", WindowState());
            throw;
        }
        ObserveWindowPresentationState();
        QueueWindowPixelSize(force);
        if ((SDL3.SDL_GetWindowFlags(_window) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0 != value)
        {
            _windowStateSynchronized = false;
            _protocol.Event("window.changed", WindowState());
            throw new NxError("core_error", "The window system did not enter the requested fullscreen state.");
        }
        if (emit)
        {
            _protocol.Event("window.changed", WindowState());
        }
    }

    private void RequestMenu()
    {
        bool flag;
        switch (_state)
        {
            case "loaded":
            case "running":
            case "paused":
                flag = true;
                break;
            default:
                flag = false;
                break;
        }
        if (flag)
        {
            _inputTarget = "ui";
            NeutralizeGuestInput();
            if (_state == "running")
            {
                Pause(value: true);
            }
            _protocol.Event("window.menu_requested", new
            {
                paused = (_state != "running"),
                inputTarget = _inputTarget
            });
        }
    }

    private unsafe void RefreshGamepads()
    {
        int num = 0;
        SDL_JoystickID* ptr = SDL3.SDL_GetGamepads(&num);
        try
        {
            HashSet<uint> present = new HashSet<uint>();
            for (int i = 0; i < num; i++)
            {
                uint num2 = (uint)ptr[i];
                present.Add(num2);
                if (!_gamepads.ContainsKey(num2))
                {
                    SDL_Gamepad* ptr2 = SDL3.SDL_OpenGamepad(ptr[i]);
                    if (ptr2 != null)
                    {
                        _gamepads.Add(num2, new PhysicalGamepad
                        {
                            Id = num2,
                            Handle = ptr2
                        });
                    }
                }
            }
            uint[] array = _gamepads.Keys.Where((uint item) => !present.Contains(item)).ToArray();
            foreach (uint id in array)
            {
                SDL3.SDL_CloseGamepad(_gamepads[id].Handle);
                _gamepads.Remove(id);
                int[] array2 = (from pair in _physicalPlayers
                                where pair.Value == id
                                select pair.Key).ToArray();
                foreach (int num5 in array2)
                {
                    _physicalPlayers.Remove(num5);
                    _input[num5] = new GamepadInput
                    {
                        PlayerId = (PlayerIndex)num5
                    };
                }
                NeutralizeGuestInput();
            }
        }
        finally
        {
            SDL3.SDL_free(ptr);
        }
        if (_autoConnectController && !_remotePlayers[0] && !_physicalPlayers.ContainsKey(0) && _gamepads.Count > 0)
        {
            uint num6 = _gamepads.Keys.OrderBy((uint result) => result).First();
            _physicalPlayers[0] = num6;
            SDL3.SDL_SetGamepadPlayerIndex(_gamepads[num6].Handle, 0);
            if (_device != null)
            {
                ConfigurePlayer(0);
            }
        }
        if (_state != "created")
        {
            _protocol.Event("input.devices_changed", InputDevices());
        }
    }

    private unsafe object InputDevices()
    {
        Require("initialized", "loaded", "running", "paused");
        return new
        {
            devices = (from pad in _gamepads.Values
                       orderby pad.Id
                       select new
                       {
                           id = "sdl:" + pad.Id.ToString(CultureInfo.InvariantCulture),
                           name = SDL3.SDL_GetGamepadName(pad.Handle),
                           type = SDL3.SDL_GetGamepadType(pad.Handle).ToString(),
                           connected = (bool)SDL3.SDL_GamepadConnected(pad.Handle),
                           player = _physicalPlayers.Where((KeyValuePair<int, uint> p) => p.Value == pad.Id).Select((Func<KeyValuePair<int, uint>, int?>)((KeyValuePair<int, uint> p) => p.Key)).FirstOrDefault()
                       }).ToArray(),
            inputTarget = _inputTarget,
            physicalObservation = "SDL3 enumeration; device presence does not prove gameplay response",
            keyboardAvailable = true,
            keyboardActive = (_inputTarget == "guest" && !_remotePlayers[0] && !_physicalPlayers.ContainsKey(0)),
            keyboardMapping = "Player 1, focused core window: Z/X/C/V=A/B/X/Y; arrows=D-pad; WASD=left stick; IJKL=right stick; Q/E=ZL/L; U/O=R/ZR; F/H=stick clicks; Enter/Backspace=Plus/Minus; Esc=pause/menu; F11=fullscreen. SDK input snapshots override keyboard for that player until input.assign(deviceId:null) relinquishes it."
        };
    }

    private unsafe object AssignInput(JsonElement p)
    {
        Require("initialized", "loaded", "running", "paused");
        int player = Fields.Integer(p, "player", 0, 0, 7);
        if (!p.TryGetProperty("deviceId", out var value))
        {
            throw new NxError("invalid_argument", "deviceId is required; null explicitly unassigns the player.");
        }
        uint? num = null;
        if (value.ValueKind != JsonValueKind.Null)
        {
            string text = Fields.RequiredString(p, "deviceId");
            if (!text.StartsWith("sdl:", StringComparison.Ordinal) || !uint.TryParse(text.AsSpan(4), out var nativeId) || !_gamepads.ContainsKey(nativeId))
            {
                throw new NxError("invalid_argument", "The selected gamepad is no longer connected. Refresh the device list.");
            }
            if (_physicalPlayers.Any((KeyValuePair<int, uint> x) => x.Key != player && x.Value == nativeId))
            {
                throw new NxError("invalid_argument", "This gamepad is already assigned to another player.");
            }
            num = nativeId;
        }
        if (player == 0)
        {
            _autoConnectController = num.HasValue;
        }
        _physicalPlayers.Remove(player);
        if (num.HasValue)
        {
            uint valueOrDefault = num.GetValueOrDefault();
            _physicalPlayers[player] = valueOrDefault;
            SDL3.SDL_SetGamepadPlayerIndex(_gamepads[valueOrDefault].Handle, player);
            ConfigurePlayer(player);
        }
        _remotePlayers[player] = false;
        _input[player] = new GamepadInput
        {
            PlayerId = (PlayerIndex)player
        };
        NeutralizeGuestInput();
        _protocol.Event("input.devices_changed", InputDevices());
        return InputDevices();
    }

    private unsafe object RouteInput(JsonElement p)
    {
        Require("initialized", "loaded", "running", "paused");
        string text = Fields.RequiredString(p, "target");
        if ((!(text == "ui") && !(text == "guest")) || 1 == 0)
        {
            throw new NxError("invalid_argument", "Input target must be ui or guest.");
        }
        ApplyInteractionInputCapture();
        if (text == "guest" && Interactions.InputCaptureActive)
        {
            throw new NxError("invalid_state", "A native applet still owns input. Complete or cancel its interaction before returning input to the guest.");
        }
        SDL3.SDL_PumpEvents();
        if (text == "guest" && _inputTarget != text)
        {
            foreach (uint value in _physicalPlayers.Values)
            {
                GamepadInput gamepadInput = ReadGamepad(_gamepads[value], 0);
                if (gamepadInput.Buttons != (ControllerKeys)0L || gamepadInput.LStick.Dx != 0 || gamepadInput.LStick.Dy != 0 || gamepadInput.RStick.Dx != 0 || gamepadInput.RStick.Dy != 0 || (bool)SDL3.SDL_GetGamepadButton(_gamepads[value].Handle, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE))
                {
                    throw new NxError("invalid_state", "Release gamepad controls before returning input to the guest.");
                }
            }
            int num = 0;
            SDLBool* ptr = SDL3.SDL_GetKeyboardState(&num);
            for (int i = 0; i < num; i++)
            {
                if ((bool)ptr[i])
                {
                    throw new NxError("invalid_state", "Release keyboard keys before returning input to the guest.");
                }
            }
        }
        _inputTarget = text;
        NeutralizeGuestInput();
        foreach (PhysicalGamepad value2 in _gamepads.Values)
        {
            value2.LastUiState = null;
        }
        _protocol.Event("input.routing_changed", new
        {
            inputTarget = text,
            requiresNeutral = false
        });
        return new
        {
            inputTarget = text,
            requiresNeutral = false
        };
    }

    private void NeutralizeGuestInput()
    {
        ReleaseTouch();
        for (int i = 0; i < _input.Length; i++)
        {
            _input[i] = new GamepadInput
            {
                PlayerId = (PlayerIndex)i
            };
        }
        if (_device != null)
        {
            _device.Hid.Npads.Update(_input.Where((GamepadInput _, int num) => _configuredPlayers[num]).ToArray());
        }
    }

    private unsafe void PollGamepads()
    {
        foreach (var (num3, key) in _physicalPlayers)
        {
            if (!_gamepads.TryGetValue(key, out var value) || !SDL3.SDL_GamepadConnected(value.Handle))
            {
                continue;
            }
            GamepadInput value2 = ReadGamepad(value, num3);
            bool flag = SDL3.SDL_GetGamepadButton(value.Handle, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE);
            if (flag && !value.GuideHeld)
            {
                RequestMenu();
            }
            value.GuideHeld = flag;
            if (_inputTarget == "guest")
            {
                if (!_remotePlayers[num3])
                {
                    _input[num3] = value2;
                }
                continue;
            }
            string text = $"{value2.Buttons}:{value2.LStick.Dx}:{value2.LStick.Dy}:{value2.RStick.Dx}:{value2.RStick.Dy}";
            if (!(text == value.LastUiState))
            {
                value.LastUiState = text;
                _protocol.Event("input.state", new
                {
                    player = num3,
                    deviceId = "sdl:" + key.ToString(CultureInfo.InvariantCulture),
                    target = "ui",
                    buttons = (from x in ButtonNames
                               where (value2.Buttons & x.Value) != 0
                               select x.Key).ToArray(),
                    leftX = (double)value2.LStick.Dx / 32767.0,
                    leftY = (double)value2.LStick.Dy / 32767.0,
                    rightX = (double)value2.RStick.Dx / 32767.0,
                    rightY = (double)value2.RStick.Dy / 32767.0,
                    uiConfirm = (bool)SDL3.SDL_GetGamepadButton(value.Handle, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH),
                    uiBack = (bool)SDL3.SDL_GetGamepadButton(value.Handle, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST)
                });
            }
        }
    }

    private unsafe GamepadInput ReadGamepad(PhysicalGamepad pad, int player)
    {
        ControllerKeys buttons = (ControllerKeys)0L;
        Button(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH, ControllerKeys.B);
        Button(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST, ControllerKeys.A);
        Button(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST, ControllerKeys.Y);
        Button(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH, ControllerKeys.X);
        Button(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START, ControllerKeys.Plus);
        Button(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK, ControllerKeys.Minus);
        Button(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK, ControllerKeys.LStick);
        Button(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK, ControllerKeys.RStick);
        Button(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER, ControllerKeys.L);
        Button(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER, ControllerKeys.R);
        Button(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP, ControllerKeys.DpadUp);
        Button(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN, ControllerKeys.DpadDown);
        Button(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT, ControllerKeys.DpadLeft);
        Button(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT, ControllerKeys.DpadRight);
        if (SDL3.SDL_GetGamepadAxis(pad.Handle, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER) > 16384)
        {
            buttons |= ControllerKeys.Zl;
        }
        if (SDL3.SDL_GetGamepadAxis(pad.Handle, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER) > 16384)
        {
            buttons |= ControllerKeys.Zr;
        }
        JoystickPosition joystickPosition = new JoystickPosition
        {
            Dx = Axis(SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX),
            Dy = Axis(SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY, invert: true)
        };
        JoystickPosition joystickPosition2 = new JoystickPosition
        {
            Dx = Axis(SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX),
            Dy = Axis(SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY, invert: true)
        };
        if (_device != null)
        {
            buttons |= _device.Hid.UpdateStickButtons(joystickPosition, joystickPosition2);
        }
        return new GamepadInput
        {
            PlayerId = (PlayerIndex)player,
            Buttons = buttons,
            LStick = joystickPosition,
            RStick = joystickPosition2
        };
        unsafe int Axis(SDL_GamepadAxis axis, bool invert = false)
        {
            int num = SDL3.SDL_GetGamepadAxis(pad.Handle, axis);
            if (Math.Abs(num) < 6000)
            {
                return 0;
            }
            return Math.Clamp(invert ? (-num) : num, -32767, 32767);
        }
        unsafe void Button(SDL_GamepadButton button, ControllerKeys key)
        {
            if ((bool)SDL3.SDL_GetGamepadButton(pad.Handle, button))
            {
                buttons |= key;
            }
        }
    }

    private unsafe void CloseGamepads()
    {
        foreach (PhysicalGamepad value in _gamepads.Values)
        {
            SDL3.SDL_CloseGamepad(value.Handle);
        }
        _gamepads.Clear();
        _physicalPlayers.Clear();
    }
}
