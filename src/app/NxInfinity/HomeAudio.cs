using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NxInfinity;

internal sealed class HomeAudio : IDisposable
{
    private struct Spec
    {
        public int Format;

        public int Channels;

        public int Frequency;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte Init(uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Quit(uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint Open(uint device, ref Spec spec, nint callback, nint user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte StreamBool(nint stream);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StreamVoid(nint stream);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte Put(nint stream, nint data, int bytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte Gain(nint stream, float gain);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Queued(nint stream);

    private nint _library;

    private nint _stream;

    private readonly StreamBool _clear;

    private readonly StreamBool _flush;

    private readonly Put _put;

    private readonly Gain _gain;

    private readonly StreamVoid _destroy;

    private readonly Quit _quit;

    private long _lastNavigation;

    private static readonly Dictionary<HomeSound, byte[]> Samples = Enum.GetValues<HomeSound>().ToDictionary((HomeSound c) => c, Synthesize);

    public int PlayedCues { get; private set; }

    public int QueuedBytes
    {
        get
        {
            if (_stream != 0)
            {
                return Bind<Queued>("SDL_GetAudioStreamQueued")(_stream);
            }
            return 0;
        }
    }

    private T Bind<T>(string name) where T : Delegate
    {
        return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
    }

    public HomeAudio(string path, string expectedSha256)
    {
        using FileStream source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Convert.ToHexStringLower(SHA256.HashData(source)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Audio library integrity check failed.");
        }
        _library = NativeLibrary.Load(path);
        bool flag = false;
        try
        {
            _quit = Bind<Quit>("SDL_QuitSubSystem");
            _destroy = Bind<StreamVoid>("SDL_DestroyAudioStream");
            _clear = Bind<StreamBool>("SDL_ClearAudioStream");
            _flush = Bind<StreamBool>("SDL_FlushAudioStream");
            _put = Bind<Put>("SDL_PutAudioStreamData");
            _gain = Bind<Gain>("SDL_SetAudioStreamGain");
            if (Bind<Init>("SDL_InitSubSystem")(16u) == 0)
            {
                throw new InvalidOperationException("No audio output device is available.");
            }
            flag = true;
            Spec spec = new Spec
            {
                Format = 32784,
                Channels = 1,
                Frequency = 48000
            };
            _stream = Bind<Open>("SDL_OpenAudioDeviceStream")(uint.MaxValue, ref spec, 0, 0);
            if (_stream == 0 || Bind<StreamBool>("SDL_ResumeAudioStreamDevice")(_stream) == 0)
            {
                throw new InvalidOperationException("The audio output device could not start.");
            }
        }
        catch
        {
            if (_stream != 0)
            {
                _destroy?.Invoke(_stream);
            }
            if (flag)
            {
                _quit?.Invoke(16u);
            }
            NativeLibrary.Free(_library);
            _library = (_stream = 0);
            throw;
        }
    }

    public bool Play(HomeSound cue, double volume)
    {
        if (_stream == 0 || !double.IsFinite(volume) || volume <= 0.0)
        {
            return false;
        }
        long tickCount = Environment.TickCount64;
        if (cue == HomeSound.Navigate && tickCount - _lastNavigation < 85)
        {
            return false;
        }
        _lastNavigation = tickCount;
        _clear(_stream);
        if (_gain(_stream, (float)Math.Clamp(volume, 0.0, 1.0)) == 0)
        {
            return false;
        }
        byte[] array = Samples[cue];
        GCHandle gCHandle = GCHandle.Alloc(array, GCHandleType.Pinned);
        try
        {
            if (_put(_stream, gCHandle.AddrOfPinnedObject(), array.Length) == 0 || _flush(_stream) == 0)
            {
                return false;
            }
            PlayedCues++;
            return true;
        }
        finally
        {
            gCHandle.Free();
        }
    }

    public void Stop()
    {
        if (_stream != 0)
        {
            _clear(_stream);
        }
    }

    public void Dispose()
    {
        if (_library != 0)
        {
            if (_stream != 0)
            {
                _destroy(_stream);
                _stream = 0;
            }
            _quit(16u);
            NativeLibrary.Free(_library);
            _library = 0;
        }
    }

    internal static byte[] Synthesize(HomeSound cue)
    {
        double num = cue switch
        {
            HomeSound.Startup => 1.55,
            HomeSound.Confirm => 0.17,
            HomeSound.Back => 0.13,
            HomeSound.Notice => 0.28,
            _ => 0.055,
        };
        int num2 = (int)(48000.0 * num);
        byte[] array = new byte[num2 * 2];
        double[] array2 = new double[4] { 220.0, 330.0, 440.0, 659.255 };
        double num3;
        short num8;
        double num4;
        for (int i = 0; i < num2; num4 *= Math.Clamp((num - num3) / 0.035, 0.0, 1.0), num8 = (short)(Math.Clamp(num4, -0.8, 0.8) * 32767.0), array[i * 2] = (byte)num8, array[i * 2 + 1] = (byte)(num8 >> 8), i++)
        {
            num3 = (double)i / 48000.0;
            num4 = 0.0;
            double num5;
            switch (cue)
            {
                case HomeSound.Startup:
                    {
                        for (int j = 0; j < array2.Length; j++)
                        {
                            double num6 = num3 - (double)j * 0.13;
                            if (num6 >= 0.0)
                            {
                                num4 += Math.Sin(Math.PI * 2.0 * array2[j] * num6) * Math.Min(1.0, num6 / 0.035) * Math.Exp(-2.8 * num6) * 0.1;
                            }
                        }
                        num4 += Math.Sin(Math.PI * 110.0 * num3) * Math.Min(1.0, num3 / 0.045) * Math.Exp(-5.0 * num3) * 0.14;
                        continue;
                    }
                case HomeSound.Confirm:
                    num5 = 880.0;
                    break;
                case HomeSound.Back:
                    num5 = 440.0 - 100.0 * num3 / num;
                    break;
                case HomeSound.Notice:
                    num5 = 220.0;
                    break;
                default:
                    num5 = 720.0;
                    break;
            }
            double num7 = num5;
            num4 = (Math.Sin(Math.PI * 2.0 * num7 * num3) + 0.2 * Math.Sin(Math.PI * 2.0 * num7 * 1.5 * num3)) * 0.2;
            num4 *= Math.Min(1.0, num3 / 0.006) * Math.Pow(1.0 - num3 / num, 2.0);
        }
        return array;
    }
}
