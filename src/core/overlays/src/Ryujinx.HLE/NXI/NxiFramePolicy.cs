using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NxInfinity.Runtime;

namespace Ryujinx.HLE;

public sealed class RyuFramePolicy
{
	private sealed record Module(ulong ProcessId, string TitleId, string BuildId, string DisplayVersion, ulong TextOffset, ulong TextLength, Func<ulong, int, byte[]> Read, Action<ulong, byte[]> Write, Action<ulong, ulong> Invalidate, Func<bool> IsPaused, Func<bool> HasNativeMods, bool SupportedJit);

	private sealed record Write(ulong Offset, byte[] Original, byte[] Replacement);

	private sealed class Transaction(GamePatchRequest request, Write[] writes)
	{
		public readonly GamePatchRequest Request = request;

		public readonly Write[] Writes = writes;

		public string Status = "prepared";

		public bool ReadbackVerified;

		public bool InvalidationComplete;

		public long Generation;
	}

	private readonly record struct Frame(long LayerId, ulong Number, bool IsGame, long Segment);

	private readonly object _patchLock = new object();

	private readonly object _framesLock = new object();

	private readonly Queue<Frame> _pendingPresents = new Queue<Frame>();

	private readonly Dictionary<long, ulong> _presentedByLayer = new Dictionary<long, ulong>();

	private readonly Func<long> _timestamp;

	private readonly long _timestampFrequency;

	private Module? _module;

	private Transaction? _transaction;

	private string? _backendId;

	private string? _backendRevision;

	private Func<bool>? _hostBoundary;

	private Func<bool>? _hostModsAbsent;

	private long _generation;

	private bool _started;

	private bool _poisoned;

	private int _requestedTarget;

	private int _appliedTarget;

	private int _appliedSwapGate;

	private string? _appliedTransactionId;

	private ulong _gameProcessId;

	private string? _titleId;

	private string? _buildId;

	private bool _running;

	private bool _nativeVsync = true;

	private int? _lastGuestSwap;

	private int? _lastEffectiveSwap;

	private long _renderLayer;

	private long _segment;

	private ulong? _lastQueued;

	private ulong? _lastAcquired;

	private ulong? _lastComposited;

	private ulong? _lastPresented;

	private bool _correlationLost;

	private long _queued;

	private long _replaced;

	private long _acquired;

	private long _composited;

	private long _appletAcquired;

	private long _presentReturns;

	private long _hostPresents;

	private long _hostPresentFailures;

	private long _correlated;

	private long _uncorrelated;

	private long _overflows;

	private long _measurementStarted;

	private long _measurementLast;

	private long _measurementFrames;

	public RyuFramePolicy()
		: this(Stopwatch.GetTimestamp, Stopwatch.Frequency)
	{
	}

	internal RyuFramePolicy(Func<long> timestamp, long timestampFrequency)
	{
		if (timestampFrequency <= 0)
		{
			throw new ArgumentOutOfRangeException("timestampFrequency");
		}
		_timestamp = timestamp;
		_timestampFrequency = timestampFrequency;
	}

	public void ConfigureBackend(string backendId, string backendRevision, Func<bool> loadedBeforeStart, Func<bool> hostModsAbsent)
	{
		lock (_patchLock)
		{
			if (_backendId != null)
			{
				throw Fail("invalid_state", "The native patch backend is already bound.");
			}
			_backendId = backendId;
			_backendRevision = backendRevision;
			_hostBoundary = loadedBeforeStart;
			_hostModsAbsent = hostModsAbsent;
		}
	}

	internal void BindMainModule(ulong processId, string titleId, string buildId, string displayVersion, ulong textOffset, ulong textLength, Func<ulong, int, byte[]> read, Action<ulong, byte[]> write, Action<ulong, ulong> invalidate, Func<bool> isPaused, Func<bool> hasNativeMods, bool supportedJit)
	{
		lock (_patchLock)
		{
			if (_module != null || _started || _transaction != null)
			{
				return;
			}
			_module = new Module(processId, titleId, buildId, displayVersion, textOffset, textLength, read, write, invalidate, isPaused, hasNativeMods, supportedJit);
			lock (_framesLock)
			{
				_gameProcessId = processId;
				_titleId = titleId;
				_buildId = buildId;
			}
		}
	}

	public GamePatchAcknowledgement Prepare(GamePatchRequest request)
	{
		lock (_patchLock)
		{
			Module module = CheckBoundary();
			GamePatchRecipe gamePatchRecipe = ValidateRequest(request, module);
			request = request with
			{
				Recipe = gamePatchRecipe,
				RecipeSha256 = request.RecipeSha256.ToLowerInvariant()
			};
			if (_transaction != null)
			{
				if (!SameRequest(_transaction.Request, request))
				{
					throw Fail("invalid_state", "A different patch transaction already owns this native process.");
				}
				VerifyTransactionBytes(module, _transaction);
				return Acknowledge(_transaction);
			}
			Write[] writes = (from w in gamePatchRecipe.Writes
				select new Write(w.Offset, Convert.FromHexString(w.ExpectedHex), Convert.FromHexString(w.ReplacementHex)) into w
				orderby w.Offset
				select w).ToArray();
			VerifyBytes(module, writes, replacement: false);
			GamePatchRecipe recipe = gamePatchRecipe with
			{
				Writes = gamePatchRecipe.Writes.Select((GamePatchWrite w) => w with { }).ToArray()
			};
			_transaction = new Transaction(request with
			{
				Recipe = recipe
			}, writes)
			{
				Generation = ++_generation
			};
			return Acknowledge(_transaction);
		}
	}

	public GamePatchAcknowledgement Apply(string transactionId)
	{
		lock (_patchLock)
		{
			Module module = CheckBoundary();
			Transaction transaction = GetTransaction(transactionId);
			if (transaction.Status == "applied")
			{
				VerifyTransactionBytes(module, transaction);
				return Acknowledge(transaction);
			}
			if (transaction.Status != "prepared")
			{
				throw Fail("invalid_state", "This transaction cannot be applied.");
			}
			if (transaction.Request.Recipe.TargetFrameRate == 60 && Volatile.Read(in _requestedTarget) != 60)
			{
				throw Fail("invalid_state", "Request 60 FPS before applying its paired timing repair.");
			}
			VerifyBytes(module, transaction.Writes, replacement: false);
			try
			{
				Write[] writes = transaction.Writes;
				foreach (Write write in writes)
				{
					module.Write(write.Offset, write.Replacement);
				}
				VerifyBytes(module, transaction.Writes, replacement: true);
				writes = transaction.Writes;
				foreach (Write write2 in writes)
				{
					module.Invalidate(write2.Offset, (ulong)write2.Replacement.Length);
				}
				transaction.ReadbackVerified = true;
				transaction.InvalidationComplete = true;
				transaction.Status = "applied";
				transaction.Generation = ++_generation;
				Volatile.Write(ref _appliedTransactionId, transaction.Request.TransactionId);
				Volatile.Write(ref _appliedTarget, transaction.Request.Recipe.TargetFrameRate.GetValueOrDefault());
				Volatile.Write(ref _appliedSwapGate, (transaction.Request.Recipe.FrameGate == "swap_2_to_1") ? 1 : 0);
				ResetMeasurement();
				return Acknowledge(transaction);
			}
			catch
			{
				RestoreOrPoison(module, transaction);
				throw Fail("core_error", "The native patch failed and its original bytes were restored. Create a fresh transaction in a new worker.");
			}
		}
	}

	public GamePatchAcknowledgement Rollback(string transactionId)
	{
		lock (_patchLock)
		{
			Module module = CheckBoundary();
			Transaction transaction = GetTransaction(transactionId);
			if (transaction.Status == "rolled_back")
			{
				VerifyTransactionBytes(module, transaction);
				return Acknowledge(transaction);
			}
			if (transaction.Status == "prepared")
			{
				VerifyBytes(module, transaction.Writes, replacement: false);
				transaction.Status = "rolled_back";
				transaction.ReadbackVerified = true;
				transaction.InvalidationComplete = true;
				transaction.Generation = ++_generation;
				return Acknowledge(transaction);
			}
			if (transaction.Status != "applied")
			{
				throw Fail("invalid_state", "This transaction cannot be rolled back.");
			}
			VerifyBytes(module, transaction.Writes, replacement: true);
			RestoreOrPoison(module, transaction);
			return Acknowledge(transaction);
		}
	}

	public GamePatchAcknowledgement GetPatch(string transactionId)
	{
		lock (_patchLock)
		{
			Transaction transaction = GetTransaction(transactionId);
			if (_started || _poisoned)
			{
				return Acknowledge(transaction)with
				{
					Boundary = (_started ? "started_receipt" : "recovery_required"),
					ReadbackVerified = false,
					ActiveModsVerified = false,
					RollbackAvailable = false
				};
			}
			VerifyTransactionBytes(CheckBoundary(), transaction);
			return Acknowledge(transaction);
		}
	}

	public void MarkStarted()
	{
		lock (_patchLock)
		{
			if (_poisoned)
			{
				throw Fail("core_error", "Native patch restoration was not verified. Dispose this worker.");
			}
			if (_transaction?.Status == "prepared")
			{
				throw Fail("invalid_state", "Finish or cancel the prepared native patch transaction before Start.");
			}
			if (_transaction?.Status == "applied")
			{
				VerifyBytes(CheckBoundary(), _transaction.Writes, replacement: true);
				if (!_transaction.InvalidationComplete)
				{
					throw Fail("core_error", "Executable cache invalidation was not verified. Dispose this worker.");
				}
				if (_transaction.Request.Recipe.TargetFrameRate == 60 && Volatile.Read(in _requestedTarget) != 60)
				{
					throw Fail("invalid_state", "The applied timing repair requires the matching 60 FPS policy before Start.");
				}
			}
			_started = true;
		}
	}

	public FrameRatePolicyState SetTarget(int target)
	{
		if ((target != 0 && target != 60) || 1 == 0)
		{
			throw Fail("invalid_argument", "Frame target must be 0 (original) or 60.");
		}
		lock (_patchLock)
		{
			if (_poisoned)
			{
				throw Fail("core_error", "Native patch restoration was not verified. Dispose this worker.");
			}
			if (_transaction?.Status == "applied" && _transaction.Request.Recipe.TargetFrameRate == 60 && target == 0)
			{
				throw Fail(_started ? "restart_required" : "invalid_state", "Roll back the paired native timing repair before selecting the original policy; after Start, use a fresh original session.");
			}
			if (Interlocked.Exchange(ref _requestedTarget, target) != target)
			{
				ResetMeasurement();
			}
		}
		return Snapshot();
	}

	public void SetRunning(bool running)
	{
		lock (_framesLock)
		{
			if (_running != running)
			{
				_running = running;
				ResetMeasurementLocked();
			}
		}
	}

	internal void SetRenderLayer(long layerId)
	{
		lock (_framesLock)
		{
			if (_renderLayer != layerId)
			{
				_renderLayer = layerId;
				_lastQueued = (_lastAcquired = (_lastComposited = (_lastPresented = null)));
				_lastGuestSwap = (_lastEffectiveSwap = null);
				ResetMeasurementLocked();
			}
		}
	}

	internal int ResolveSwapInterval(long layerId, ulong owner, int guestSwapInterval, bool nativeSixtyVsync)
	{
		bool flag = Volatile.Read(in _appliedSwapGate) == 1;
		lock (_framesLock)
		{
			if (layerId != _renderLayer || owner != _gameProcessId || _gameProcessId == 0L)
			{
				return guestSwapInterval;
			}
			_nativeVsync = nativeSixtyVsync;
			_lastGuestSwap = guestSwapInterval;
			_lastEffectiveSwap = (((flag & nativeSixtyVsync) && guestSwapInterval == 2) ? 1 : guestSwapInterval);
			return _lastEffectiveSwap.Value;
		}
	}

	internal void RecordQueued(long layerId, ulong owner, ulong frameNumber, bool replaced)
	{
		lock (_framesLock)
		{
			if (layerId == _renderLayer && owner == _gameProcessId && _gameProcessId != 0L && (!_lastQueued.HasValue || frameNumber > _lastQueued.Value))
			{
				_lastQueued = frameNumber;
				_queued++;
				if (replaced)
				{
					_replaced++;
				}
			}
		}
	}

	internal void RecordAcquired(long layerId, ulong owner, ulong frameNumber)
	{
		lock (_framesLock)
		{
			if (owner != _gameProcessId || _gameProcessId == 0L)
			{
				_appletAcquired++;
			}
			else if (layerId == _renderLayer && (!_lastAcquired.HasValue || frameNumber > _lastAcquired.Value))
			{
				_lastAcquired = frameNumber;
				_acquired++;
			}
		}
	}

	internal void RecordComposited(long layerId, ulong owner, ulong frameNumber)
	{
		lock (_framesLock)
		{
			if (layerId == _renderLayer && owner == _gameProcessId && _gameProcessId != 0L && (!_lastComposited.HasValue || frameNumber > _lastComposited.Value))
			{
				_lastComposited = frameNumber;
				_composited++;
			}
		}
	}

	internal long CaptureMeasurementSegment()
	{
		lock (_framesLock)
		{
			return _segment;
		}
	}

	internal void RecordReadyToPresent(long layerId, ulong owner, ulong frameNumber, long measurementSegment)
	{
		lock (_framesLock)
		{
			if (!_correlationLost)
			{
				if (_pendingPresents.Count == 64)
				{
					_overflows++;
					_correlationLost = true;
					_pendingPresents.Clear();
					ResetMeasurementLocked();
				}
				else
				{
					_pendingPresents.Enqueue(new Frame(layerId, frameNumber, layerId == _renderLayer && owner == _gameProcessId && _gameProcessId != 0, measurementSegment));
				}
			}
		}
	}

	public void RecordPresentResult(bool accepted)
	{
		lock (_framesLock)
		{
			_presentReturns++;
			if (accepted)
			{
				_hostPresents++;
			}
			else
			{
				_hostPresentFailures++;
			}
			if (_correlationLost || !_pendingPresents.TryDequeue(out var result))
			{
				_uncorrelated++;
			}
			else
			{
				if (!accepted || !result.IsGame || result.LayerId != _renderLayer || (_presentedByLayer.TryGetValue(result.LayerId, out var value) && result.Number <= value))
				{
					return;
				}
				if (!_presentedByLayer.ContainsKey(result.LayerId) && _presentedByLayer.Count == 256)
				{
					_overflows++;
					_correlationLost = true;
					ResetMeasurementLocked();
					return;
				}
				_presentedByLayer[result.LayerId] = result.Number;
				_lastPresented = result.Number;
				_correlated++;
				if (_running && result.Segment == _segment)
				{
					long num = _timestamp();
					if (_measurementFrames == 0L)
					{
						_measurementStarted = num;
					}
					_measurementLast = num;
					_measurementFrames++;
				}
			}
		}
	}

	public FrameRatePolicyState Snapshot()
	{
		lock (_framesLock)
		{
			int num = Volatile.Read(in _requestedTarget);
			bool flag = Volatile.Read(in _appliedTarget) == 60;
			bool flag2 = (num == 60) & flag;
			bool flag3 = Volatile.Read(in _appliedSwapGate) == 1 && _nativeVsync && _lastGuestSwap == 2 && _lastEffectiveSwap == 1;
			string text;
			if (num != 0)
			{
				if (_nativeVsync)
				{
					if (flag2)
					{
						text = (flag3 ? "native_patch_gate_consumed" : "native_timing_repair_applied");
					}
					else if (_lastGuestSwap != 1)
					{
						int? lastGuestSwap = _lastGuestSwap;
						if (lastGuestSwap.HasValue)
						{
							text = (flag ? "guest_swap_interval_preserved" : "verified_timing_repair_required");
						}
						else
						{
							text = "awaiting_game_buffer";
						}
					}
					else
					{
						text = "native_swap_one_preserved";
					}
				}
				else
				{
					text = "native_vsync_required";
				}
			}
			else
			{
				text = "original_requested";
			}
			string reasonCode = text;
			double? num2 = null;
			long? num3 = null;
			double? measuredGameFps = null;
			if (_running && !_correlationLost && _measurementFrames >= 2 && _measurementLast > _measurementStarted)
			{
				num2 = 1000.0 * (double)(_measurementLast - _measurementStarted) / (double)_timestampFrequency;
				num3 = _measurementFrames - 1;
				if ((double)(_timestamp() - _measurementLast) / (double)_timestampFrequency <= 1.0)
				{
					measuredGameFps = (double?)num3 / (num2 / 1000.0);
				}
			}
			return new FrameRatePolicyState
			{
				RequestedTarget = num,
				EffectivePolicy = (flag2 ? "patched_60" : "original"),
				EffectiveTarget = (flag2 ? new int?(60) : ((int?)null)),
				ReasonCode = reasonCode,
				PatchTransactionId = Volatile.Read(in _appliedTransactionId),
				TitleId = _titleId,
				BuildId = _buildId,
				GuestClockUnchanged = true,
				GuestQueuedFrames = _queued,
				ReplacedBeforeAcquire = _replaced,
				AcquiredGameBuffers = _acquired,
				CompositedGameFrames = _composited,
				AppletAcquisitions = _appletAcquired,
				HostPresentReturns = _presentReturns,
				HostPresents = _hostPresents,
				HostPresentFailures = _hostPresentFailures,
				CorrelatedGamePresents = _correlated,
				UncorrelatedPresents = _uncorrelated,
				CorrelationOverflows = _overflows,
				MeasuredGameFps = measuredGameFps,
				MeasurementIntervalMs = num2,
				MeasuredGameFrames = num3,
				MeasurementScope = (_correlationLost ? "correlation_lost" : "selected_game_layer_vk_queue_present_success")
			};
		}
	}

	private Module CheckBoundary()
	{
		if (_poisoned)
		{
			throw Fail("core_error", "Native patch restoration was not verified. Dispose this worker.");
		}
		if (_started)
		{
			throw Fail("restart_required", "Static module patches require a fresh loaded worker before its first Start.");
		}
		Module module = _module ?? throw Fail("unsupported", "The native loader did not expose an exact main NSO text range.");
		if (!module.SupportedJit)
		{
			throw Fail("unsupported", "This patch adapter supports the verified x64 JIT path only.");
		}
		Func<bool>? hostBoundary = _hostBoundary;
		if (hostBoundary == null || !hostBoundary() || !module.IsPaused())
		{
			throw Fail("invalid_state", "The native process is not at the loaded-before-start boundary.");
		}
		if (!module.HasNativeMods())
		{
			Func<bool>? hostModsAbsent = _hostModsAbsent;
			if (hostModsAbsent != null && hostModsAbsent())
			{
				return module;
			}
		}
		throw Fail("invalid_state", "Native mods, cheats or patches conflict with this transaction.");
	}

	private GamePatchRecipe ValidateRequest(GamePatchRequest request, Module module)
	{
		if ((object)request == null || !Guid.TryParseExact(request.TransactionId, "N", out var _) || request.TransactionId != request.TransactionId.ToLowerInvariant() || !IsHex(request.RecipeSha256, 64) || (object)request.Recipe == null)
		{
			throw Fail("invalid_argument", "A canonical transaction identity, digest and recipe are required.");
		}
		GamePatchRecipe gamePatchRecipe;
		try
		{
			gamePatchRecipe = GamePatchValidation.Canonicalize(request.Recipe);
			if (!string.Equals(GamePatchValidation.Fingerprint(gamePatchRecipe), request.RecipeSha256, StringComparison.OrdinalIgnoreCase))
			{
				throw Fail("invalid_argument", "The recipe SHA256 does not describe the supplied canonical writes and identity.");
			}
		}
		catch (NxException ex)
		{
			throw Fail("invalid_argument", ex.Message);
		}
		if (gamePatchRecipe.BackendId != _backendId || !string.Equals(gamePatchRecipe.BackendRevision, _backendRevision, StringComparison.OrdinalIgnoreCase) || !string.Equals(gamePatchRecipe.TitleId, module.TitleId, StringComparison.OrdinalIgnoreCase) || !string.Equals(gamePatchRecipe.BuildId, module.BuildId, StringComparison.OrdinalIgnoreCase) || gamePatchRecipe.DisplayVersion != module.DisplayVersion)
		{
			throw Fail("invalid_argument", "Recipe title, update, main executable or core revision differs from the actual native loader.");
		}
		foreach (GamePatchWrite write in gamePatchRecipe.Writes)
		{
			ulong num = (ulong)write.ExpectedHex.Length / 2uL;
			if (write.Offset < module.TextOffset || write.Offset - module.TextOffset > module.TextLength || num > module.TextLength - (write.Offset - module.TextOffset))
			{
				throw Fail("invalid_argument", "Every patch instruction must remain inside the actual loaded main executable text.");
			}
		}
		return gamePatchRecipe;
	}

	private static bool IsHex(string? value, int size)
	{
		if (value != null && value.Length == size)
		{
			return value.All(Uri.IsHexDigit);
		}
		return false;
	}

	private static bool SameRequest(GamePatchRequest a, GamePatchRequest b)
	{
		if (a.TransactionId == b.TransactionId && a.RecipeSha256 == b.RecipeSha256 && a.Recipe with
		{
			Writes = Array.Empty<GamePatchWrite>()
		} == b.Recipe with
		{
			Writes = Array.Empty<GamePatchWrite>()
		})
		{
			return a.Recipe.Writes.SequenceEqual(b.Recipe.Writes);
		}
		return false;
	}

	private Transaction GetTransaction(string id)
	{
		if (!IsHex(id, 32))
		{
			throw Fail("invalid_argument", "A 32-hex transaction ID is required.");
		}
		if (_transaction == null || _transaction.Request.TransactionId != id)
		{
			throw Fail("invalid_state", "The transaction does not belong to this native process.");
		}
		return _transaction;
	}

	private static void VerifyBytes(Module module, IEnumerable<Write> writes, bool replacement)
	{
		foreach (Write write in writes)
		{
			byte[] array = (replacement ? write.Replacement : write.Original);
			if (!module.Read(write.Offset, array.Length).AsSpan().SequenceEqual(array))
			{
				throw Fail("invalid_state", "Native main-module bytes do not match every guarded range.");
			}
		}
	}

	private static void VerifyTransactionBytes(Module module, Transaction transaction)
	{
		bool flag;
		switch (transaction.Status)
		{
		case "prepared":
		case "applied":
		case "rolled_back":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (!flag)
		{
			throw Fail("core_error", "This transaction requires worker disposal and cannot acknowledge a fresh byte readback.");
		}
		VerifyBytes(module, transaction.Writes, transaction.Status == "applied");
		transaction.ReadbackVerified = true;
	}

	private void RestoreOrPoison(Module module, Transaction transaction)
	{
		Volatile.Write(ref _appliedSwapGate, 0);
		Volatile.Write(ref _appliedTarget, 0);
		Volatile.Write(ref _appliedTransactionId, null);
		try
		{
			Write[] writes = transaction.Writes;
			foreach (Write write in writes)
			{
				module.Write(write.Offset, write.Original);
			}
			VerifyBytes(module, transaction.Writes, replacement: false);
			writes = transaction.Writes;
			foreach (Write write2 in writes)
			{
				module.Invalidate(write2.Offset, (ulong)write2.Original.Length);
			}
			transaction.Status = "rolled_back";
			transaction.ReadbackVerified = true;
			transaction.InvalidationComplete = true;
			transaction.Generation = ++_generation;
			ResetMeasurement();
		}
		catch
		{
			_poisoned = true;
			transaction.Status = "failed";
			transaction.ReadbackVerified = false;
			transaction.InvalidationComplete = false;
			transaction.Generation = ++_generation;
			throw Fail("core_error", "Native patch restoration failed verification. This worker must be disposed without Start.");
		}
	}

	private GamePatchAcknowledgement Acknowledge(Transaction transaction)
	{
		GamePatchRequest request = transaction.Request;
		GamePatchRecipe recipe = request.Recipe;
		bool rollbackAvailable = !_started && !_poisoned && transaction.Status is "prepared" or "applied";

		return new GamePatchAcknowledgement
		{
			TransactionId = request.TransactionId,
			RecipeSha256 = request.RecipeSha256,
			RecipeId = recipe.RecipeId,
			AdapterId = recipe.AdapterId,
			BackendId = recipe.BackendId,
			BackendRevision = recipe.BackendRevision,
			TitleId = recipe.TitleId,
			BuildId = recipe.BuildId,
			DisplayVersion = recipe.DisplayVersion,
			Status = transaction.Status,
			Boundary = "loaded_before_start",
			TargetFrameRate = recipe.TargetFrameRate,
			FrameGate = recipe.FrameGate,
			WriteCount = transaction.Writes.Length,
			ByteCount = transaction.Writes.Sum(w => w.Original.Length),
			OriginalBytesVerified = true,
			ReadbackVerified = transaction.ReadbackVerified,
			ExecutableInvalidationComplete = transaction.InvalidationComplete,
			ActiveModsVerified = true,
			RollbackAvailable = rollbackAvailable,
			Generation = transaction.Generation,
		};
	}

	private void ResetMeasurement()
	{
		lock (_framesLock)
		{
			ResetMeasurementLocked();
		}
	}

	private void ResetMeasurementLocked()
	{
		_segment++;
		_measurementStarted = (_measurementLast = (_measurementFrames = 0L));
	}

	private static RyuFramePolicyException Fail(string code, string message)
	{
		return new RyuFramePolicyException(code, message);
	}
}
