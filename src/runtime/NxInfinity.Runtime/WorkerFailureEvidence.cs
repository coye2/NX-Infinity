namespace NxInfinity.Runtime;

/// <summary>
/// Fixed classifications extracted from a private stderr tail. No raw message,
/// arbitrary exception name, method, path or player text is returned. These
/// observations narrow an investigation; they do not establish a crash cause.
/// </summary>
public sealed record WorkerFailureEvidence(bool UnhandledExceptionObserved,
    IReadOnlyList<string> ExceptionTypes, IReadOnlyList<string> StackSubsystems,
    IReadOnlyList<string> Signals)
{
    public int SchemaVersion => 2;
    /// <summary>Unqualified names from exact host error prefixes; these do not identify a namespace.</summary>
    public IReadOnlyList<string> HostExceptionNames { get; init; } = Array.Empty<string>();
    public const int MaximumTailCharacters = 65536;
    /// <summary>Only exact, authored framework/core method names; no arguments, paths or arbitrary text.</summary>
    public IReadOnlyList<string> StackMethods { get; init; } = Array.Empty<string>();
    private static readonly string[] KnownStackMethods =
    [
        "Ryujinx.HLE.HOS.Kernel.SupervisorCall.Syscall.Break",
        "Ryujinx.Audio.Renderer.Server.CommandGenerator.GenerateBiquadFilterEffect",
        "Ryujinx.Audio.Renderer.Server.Effect.BiquadFilterEffect.Update",
        "Ryujinx.Audio.Renderer.Server.AudioRenderSystem.Initialize",
        "Ryujinx.Horizon.Sdk.Audio.Detail.AudioRendererManager.GetWorkBufferSize",
        "Ryujinx.HLE.HOS.Kernel.Threading.KThread.ThreadStart",
        "Ryujinx.HLE.HOS.Kernel.KernelContext.Dispose",
        "Ryujinx.HLE.HOS.Kernel.KernelContext.DisposeResources",
        "Ryujinx.Memory.MemoryBlock.Dispose",
        "Ryujinx.Cpu.Jit.JitCpuContext.Execute",
        "Ryujinx.Cpu.Jit.JitExecutionContext.Dispose",
        "ARMeilleure.Translation.Translator.Execute",
        "ARMeilleure.State.ExecutionContext.Dispose",
        "System.Threading.WaitHandle.WaitOneNoCheck",
        "System.Threading.WaitHandle.WaitOneCore",
        "System.Threading.SemaphoreSlim.Wait",
        "System.Threading.SemaphoreSlim.Release",
        "Ryujinx.Graphics.Vulkan.ShaderCollection.CheckProgramLink",
        "Ryujinx.Graphics.Vulkan.ShaderCollection.BackgroundCompilation",
        "Ryujinx.Graphics.Vulkan.ShaderCollection.Dispose",
        "Ryujinx.Graphics.Vulkan.PipelineState.CreateGraphicsPipeline",
        "Ryujinx.Graphics.Vulkan.PipelineState.CreateComputePipeline",
        "Ryujinx.Graphics.Gpu.Shader.DiskCache.DiskCacheHostStorage.AddShader",
        "NxInfinity.Core.CoreSession.DisposeResources",
        "Nx.Ryujinx.Canary.Host.CoreSession.DisposeResources"
    ];
    private static readonly string[] KnownExceptionTypes =
    [
        "System.AccessViolationException", "System.AggregateException",
        "System.ArgumentException", "System.ArgumentNullException", "System.ArgumentOutOfRangeException",
        "System.BadImageFormatException", "System.DllNotFoundException", "System.EntryPointNotFoundException",
        "System.Exception", "System.IndexOutOfRangeException", "System.InvalidOperationException", "System.NotImplementedException",
        "System.NotSupportedException", "System.NullReferenceException", "System.ObjectDisposedException",
        "System.OperationCanceledException", "System.OutOfMemoryException", "System.OverflowException",
        "System.PlatformNotSupportedException", "System.StackOverflowException", "System.TimeoutException",
        "System.TypeInitializationException", "System.TypeLoadException", "System.UnauthorizedAccessException",
        "System.IO.DirectoryNotFoundException", "System.IO.EndOfStreamException", "System.IO.FileNotFoundException",
        "System.IO.IOException", "System.Threading.Tasks.TaskCanceledException",
        "System.Runtime.InteropServices.ExternalException", "System.Runtime.InteropServices.SEHException",
        "Ryujinx.Graphics.Vulkan.VulkanException", "Ryujinx.Memory.InvalidMemoryRegionException",
        "Ryujinx.HLE.Exceptions.GuestBrokeExecutionException",
        "LibHac.Common.HorizonResultException"
    ];
    private static readonly string[] KnownHostExceptionNames = KnownExceptionTypes
        .Select(type => type[(type.LastIndexOf('.') + 1)..]).Distinct(StringComparer.Ordinal).ToArray();
    private static readonly (string Prefix, string Label)[] KnownStackSubsystems =
    [
        ("Ryujinx.Graphics.Vulkan.", "vulkan"), ("Silk.NET.Vulkan.", "vulkan"),
        ("Ryujinx.Graphics.OpenGL.", "opengl"), ("Ryujinx.Graphics.Gpu.", "gpu"),
        ("Ryujinx.Graphics.GAL.", "graphics_abstraction"),
        ("Ryujinx.Graphics.Shader.", "shader"), ("ARMeilleure.", "cpu_jit"),
        ("Ryujinx.Cpu.", "cpu"), ("Ryujinx.Memory.", "memory"),
        ("Ryujinx.Audio.", "audio"), ("Ryujinx.Input.", "input"),
        ("Ryujinx.HLE.", "hle"), ("LibHac.", "filesystem"),
        ("NxInfinity.Core.CoreSession.", "core_session"),
        ("Nx.Ryujinx.Canary.Host.CoreSession.", "core_session")
    ];

    public static WorkerFailureEvidence Parse(string? privateStderr, bool firstLineMayBePartial = false)
    {
        var exceptions = new List<string>(); var hostExceptions = new List<string>();
        var subsystems = new List<string>(); var signals = new List<string>();
        var methods = new List<string>();
        bool unhandled = false;
        if (!string.IsNullOrEmpty(privateStderr))
        {
            // Match only complete retained lines when the input exceeds the
            // bound; a cut fragment must not masquerade as an exception header.
            var tail = privateStderr;
            if (tail.Length > MaximumTailCharacters)
            {
                tail = tail[^MaximumTailCharacters..];
                firstLineMayBePartial = true;
            }
            if (firstLineMayBePartial)
            {
                int newline = tail.IndexOf('\n');
                tail = newline < 0 ? "" : tail[(newline + 1)..];
            }
            foreach (string raw in tail.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("Unhandled exception.", StringComparison.Ordinal))
                {
                    unhandled = true;
                    line = line["Unhandled exception.".Length..].TrimStart();
                }
                if (line.StartsWith("--->", StringComparison.Ordinal)) line = line[4..].TrimStart();
                string? hostHeader = line.StartsWith("NX core error: ", StringComparison.Ordinal) ? line["NX core error: ".Length..] :
                    line.StartsWith("NX host stopped after ", StringComparison.Ordinal) ? line["NX host stopped after ".Length..] : null;
                string? hostException = hostHeader is null ? null : KnownHostExceptionNames.FirstOrDefault(name =>
                    hostHeader.StartsWith(name + ":", StringComparison.Ordinal));
                if (hostException is not null) Add(hostExceptions, hostException);
                string? exception = KnownExceptionTypes.FirstOrDefault(type =>
                    line.Equals(type, StringComparison.Ordinal) || line.StartsWith(type + ":", StringComparison.Ordinal));
                if (exception is not null)
                    Add(exceptions, exception);
                if (exception == "Ryujinx.Graphics.Vulkan.VulkanException" || hostException == "VulkanException")
                {
                    if (HasToken(line, "ErrorDeviceLost") || HasToken(line, "VK_ERROR_DEVICE_LOST")) Add(signals, "vulkan_device_lost");
                    if (HasToken(line, "ErrorOutOfDeviceMemory") || HasToken(line, "VK_ERROR_OUT_OF_DEVICE_MEMORY")) Add(signals, "vulkan_device_memory_exhausted");
                    if (HasToken(line, "ErrorOutOfHostMemory") || HasToken(line, "VK_ERROR_OUT_OF_HOST_MEMORY")) Add(signals, "vulkan_host_memory_exhausted");
                }
                if (!line.StartsWith("at ", StringComparison.Ordinal)) continue;
                var frame = line[3..].TrimStart();
                foreach (string method in KnownStackMethods)
                    if (frame.StartsWith(method + "(", StringComparison.Ordinal)) Add(methods, method);
                foreach (var (prefix, label) in KnownStackSubsystems)
                    if (frame.StartsWith(prefix, StringComparison.Ordinal)) Add(subsystems, label);
            }
        }
        return new(unhandled, exceptions.AsReadOnly(), subsystems.AsReadOnly(), signals.AsReadOnly())
        { HostExceptionNames = hostExceptions.AsReadOnly(), StackMethods = methods.AsReadOnly() };
    }

    private static void Add(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.Ordinal)) values.Add(value);
    }

    private static bool HasToken(string value, string token)
    {
        int start = 0;
        while ((start = value.IndexOf(token, start, StringComparison.Ordinal)) >= 0)
        {
            int end = start + token.Length;
            if ((start == 0 || !IsTokenCharacter(value[start - 1])) &&
                (end == value.Length || !IsTokenCharacter(value[end]))) return true;
            start = end;
        }
        return false;
    }
    private static bool IsTokenCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
}
