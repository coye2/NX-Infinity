using System;
using System.Threading;
using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL;

namespace Nx.Backends.Performance;

internal sealed class ObservedRenderer : IRenderer, IDisposable
{
    private sealed class ObservedWindow(IRenderer renderer, Action presentReturned, Action<Ryujinx.Graphics.GAL.VSyncMode> vsyncChanged) : IWindow
    {
        public void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback)
        {
            renderer.Window.Present(texture, crop, swapBuffersCallback);
            presentReturned();
        }

        public void SetSize(int width, int height)
        {
            renderer.Window.SetSize(width, height);
        }

        public void ChangeVSyncMode(Ryujinx.Graphics.GAL.VSyncMode mode)
        {
            renderer.Window.ChangeVSyncMode(mode);
            vsyncChanged?.Invoke(mode);
        }

        public void SetAntiAliasing(Ryujinx.Graphics.GAL.AntiAliasing mode)
        {
            renderer.Window.SetAntiAliasing(mode);
        }

        public void SetScalingFilter(Ryujinx.Graphics.GAL.ScalingFilter type)
        {
            renderer.Window.SetScalingFilter(type);
        }

        public void SetScalingFilterLevel(float level)
        {
            renderer.Window.SetScalingFilterLevel(level);
        }

        public void SetColorSpacePassthrough(bool enabled)
        {
            renderer.Window.SetColorSpacePassthrough(enabled);
        }
    }

    private readonly IRenderer _inner;

    public IWindow Window { get; }

    public bool PreferThreading => _inner.PreferThreading;

    public IPipeline Pipeline => _inner.Pipeline;

    public uint ProgramCount => _inner.ProgramCount;

    public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured
    {
        add
        {
            _inner.ScreenCaptured += value;
        }
        remove
        {
            _inner.ScreenCaptured -= value;
        }
    }

    internal ObservedRenderer(IRenderer inner, Action presentReturned, Action<Ryujinx.Graphics.GAL.VSyncMode> vsyncChanged = null)
    {
        _inner = inner;
        Window = new ObservedWindow(inner, presentReturned, vsyncChanged);
    }

    public void BackgroundContextAction(Action action, bool alwaysBackground = false)
    {
        _inner.BackgroundContextAction(action, alwaysBackground);
    }

    public BufferHandle CreateBuffer(int size, BufferAccess access = BufferAccess.Default)
    {
        return _inner.CreateBuffer(size, access);
    }

    public BufferHandle CreateBuffer(nint pointer, int size)
    {
        return _inner.CreateBuffer(pointer, size);
    }

    public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
    {
        return _inner.CreateBufferSparse(storageBuffers);
    }

    public IImageArray CreateImageArray(int size, bool isBuffer)
    {
        return _inner.CreateImageArray(size, isBuffer);
    }

    public IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info)
    {
        return _inner.CreateProgram(shaders, info);
    }

    public ISampler CreateSampler(SamplerCreateInfo info)
    {
        return _inner.CreateSampler(info);
    }

    public ITexture CreateTexture(TextureCreateInfo info)
    {
        return _inner.CreateTexture(info);
    }

    public ITextureArray CreateTextureArray(int size, bool isBuffer)
    {
        return _inner.CreateTextureArray(size, isBuffer);
    }

    public bool PrepareHostMapping(nint address, ulong size)
    {
        return _inner.PrepareHostMapping(address, size);
    }

    public void CreateSync(ulong id, bool strict)
    {
        _inner.CreateSync(id, strict);
    }

    public void DeleteBuffer(BufferHandle buffer)
    {
        _inner.DeleteBuffer(buffer);
    }

    public PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
    {
        return _inner.GetBufferData(buffer, offset, size);
    }

    public Capabilities GetCapabilities()
    {
        return _inner.GetCapabilities();
    }

    public ulong GetCurrentSync()
    {
        return _inner.GetCurrentSync();
    }

    public HardwareInfo GetHardwareInfo()
    {
        return _inner.GetHardwareInfo();
    }

    public IProgram LoadProgramBinary(byte[] programBinary, bool hasFragmentShader, ShaderInfo info)
    {
        return _inner.LoadProgramBinary(programBinary, hasFragmentShader, info);
    }

    public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
    {
        _inner.SetBufferData(buffer, offset, data);
    }

    public void UpdateCounters()
    {
        _inner.UpdateCounters();
    }

    public void PreFrame()
    {
        _inner.PreFrame();
    }

    public ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
    {
        return _inner.ReportCounter(type, resultHandler, divisor, hostReserved);
    }

    public void ResetCounter(CounterType type)
    {
        _inner.ResetCounter(type);
    }

    public void RunLoop(ThreadStart gpuLoop)
    {
        _inner.RunLoop(gpuLoop);
    }

    public void WaitSync(ulong id)
    {
        _inner.WaitSync(id);
    }

    public void Initialize(GraphicsDebugLevel logLevel)
    {
        _inner.Initialize(logLevel);
    }

    public void SetInterruptAction(Action<Action> interruptAction)
    {
        _inner.SetInterruptAction(interruptAction);
    }

    public void Screenshot()
    {
        _inner.Screenshot();
    }

    public void Dispose()
    {
        _inner.Dispose();
    }
}
