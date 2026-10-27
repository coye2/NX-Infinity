using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Ryujinx.Audio.Backends.OpenAL;
using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Memory;

namespace NxInfinity.Core;

internal sealed class MeteredAudioDriver : IHardwareDeviceDriver, IDisposable
{
    private sealed class Session(MeteredAudioDriver owner, IHardwareDeviceSession inner) : IHardwareDeviceSession, IDisposable
    {
        public bool RegisterBuffer(AudioBuffer buffer)
        {
            return inner.RegisterBuffer(buffer);
        }

        public void UnregisterBuffer(AudioBuffer buffer)
        {
            inner.UnregisterBuffer(buffer);
        }

        public void QueueBuffer(AudioBuffer buffer)
        {
            inner.QueueBuffer(buffer);
            Interlocked.Increment(ref owner._queuedBuffers);
            int num = buffer.Data?.Length ?? 0;
            Interlocked.Add(ref owner._queuedBytes, num);
            byte[] data = buffer.Data;
            if (data != null && data.Length > 0)
            {
                if (data.AsSpan().IndexOfAnyExcept<byte>(0) >= 0)
                {
                    Interlocked.Increment(ref owner._nonzeroBuffers);
                }
                Volatile.Write(ref owner._lastPcmSha256, Convert.ToHexString(SHA256.HashData(data)));
            }
        }

        public bool WasBufferFullyConsumed(AudioBuffer buffer)
        {
            return inner.WasBufferFullyConsumed(buffer);
        }

        public void SetVolume(float volume)
        {
            inner.SetVolume(volume);
        }

        public float GetVolume()
        {
            return inner.GetVolume();
        }

        public ulong GetPlayedSampleCount()
        {
            return inner.GetPlayedSampleCount();
        }

        public void Start()
        {
            inner.Start();
        }

        public void Stop()
        {
            inner.Stop();
        }

        public void PrepareToClose()
        {
            inner.PrepareToClose();
        }

        public void Dispose()
        {
            inner.Dispose();
        }
    }

    private readonly OpenALHardwareDeviceDriver _inner = new OpenALHardwareDeviceDriver();

    private readonly ConcurrentBag<Session> _sessions = new ConcurrentBag<Session>();

    private long _queuedBuffers;

    private long _queuedBytes;

    private long _nonzeroBuffers;

    private string _lastPcmSha256;

    public static bool IsSupported => OpenALHardwareDeviceDriver.IsSupported;

    public float Volume
    {
        get
        {
            return _inner.Volume;
        }
        set
        {
            _inner.Volume = value;
        }
    }

    public IHardwareDeviceSession OpenDeviceSession(IHardwareDeviceDriver.Direction direction, IVirtualMemoryManager memoryManager, SampleFormat format, uint rate, uint channels)
    {
        Session session = new Session(this, _inner.OpenDeviceSession(direction, memoryManager, format, rate, channels));
        _sessions.Add(session);
        return session;
    }

    public object Snapshot()
    {
        return new
        {
            source = "native_OpenAL_buffer_queue",
            sessionsOpened = _sessions.Count,
            queuedBuffers = Interlocked.Read(in _queuedBuffers),
            queuedBytes = Interlocked.Read(in _queuedBytes),
            nonzeroPcmBuffers = Interlocked.Read(in _nonzeroBuffers),
            playedSamples = _sessions.Sum((Session s) => checked((long)s.GetPlayedSampleCount())),
            lastPcmSha256 = Volatile.Read(in _lastPcmSha256),
            outputQualification = ((Environment.GetEnvironmentVariable("ALSOFT_DRIVERS") == "null") ? "null_sink_no_physical_audio" : "native_output_selected_physical_audibility_unverified")
        };
    }

    public ManualResetEvent GetUpdateRequiredEvent()
    {
        return _inner.GetUpdateRequiredEvent();
    }

    public ManualResetEvent GetPauseEvent()
    {
        return _inner.GetPauseEvent();
    }

    public bool SupportsDirection(IHardwareDeviceDriver.Direction direction)
    {
        return _inner.SupportsDirection(direction);
    }

    public bool SupportsSampleRate(uint rate)
    {
        return _inner.SupportsSampleRate(rate);
    }

    public bool SupportsSampleFormat(SampleFormat format)
    {
        return _inner.SupportsSampleFormat(format);
    }

    public bool SupportsChannelCount(uint channels)
    {
        return _inner.SupportsChannelCount(channels);
    }

    public IHardwareDeviceDriver GetRealDeviceDriver()
    {
        return this;
    }

    public void Dispose()
    {
        _inner.Dispose();
    }
}
