using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NxInfinity;

public sealed class DiscordPresence : IAsyncDisposable
{
    private readonly string _path;

    private readonly DiscordPreferences _publisher;

    private Stream? _stream;

    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1);

    private string? _application;

    public static readonly JsonSerializerOptions Json = new JsonSerializerOptions();

    public DiscordPreferences Preferences { get; private set; } = new DiscordPreferences();

    public string ApplicationId
    {
        get
        {
            if (_publisher.ApplicationId.Length <= 0)
            {
                return Preferences.ApplicationId;
            }
            return _publisher.ApplicationId;
        }
    }

    public string LogoAsset
    {
        get
        {
            if (_publisher.ApplicationId.Length <= 0)
            {
                return Preferences.LogoAsset;
            }
            return _publisher.LogoAsset;
        }
    }

    public bool IsConfigured => ApplicationId.Length > 0;

    public string Status { get; private set; } = "Discord presence is off.";

    public DiscordPresence(string dataRoot, string? installRoot = null)
    {
        _publisher = ReadPreferences(Path.Combine(installRoot ?? AppContext.BaseDirectory, "config", "discord-presence.json"));
        _path = Path.Combine(dataRoot, "discord-presence.json");
        Preferences = ReadPreferences(_path);
    }

    private static DiscordPreferences ReadPreferences(string path)
    {
        try
        {
            DiscordPreferences? obj = ((File.Exists(path) && new FileInfo(path).Length < 16384) ? (JsonSerializer.Deserialize<DiscordPreferences>(File.ReadAllText(path)) ?? new DiscordPreferences()) : new DiscordPreferences());
            Validate(obj);
            return obj;
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException) ? true : false)
        {
            return new DiscordPreferences();
        }
    }

    public static void Validate(DiscordPreferences value)
    {
        if (value.ApplicationId == null || value.LogoAsset == null)
        {
            throw new ArgumentException("Invalid Discord presence configuration.");
        }
        if (value.ApplicationId.Length != 0 && !Regex.IsMatch(value.ApplicationId, "\\A[0-9]{17,20}\\z"))
        {
            throw new ArgumentException("Invalid Discord application configuration.");
        }
        if (!Regex.IsMatch(value.LogoAsset, "\\A[a-zA-Z0-9_-]{1,64}\\z") && (!Uri.TryCreate(value.LogoAsset, UriKind.Absolute, out Uri result) || !(result.Scheme == "https") || value.LogoAsset.Length > 256))
        {
            throw new ArgumentException("Use a Discord image asset key or a public HTTPS image URL for the NXI logo.");
        }
    }

    public void Save(DiscordPreferences value)
    {
        Validate(value);
        AvatarStore.WriteAtomic(_path, JsonSerializer.SerializeToUtf8Bytes(value));
        Preferences = value;
    }

    public async Task PublishAsync(DiscordActivity? activity, CancellationToken ct = default(CancellationToken))
    {
        await _gate.WaitAsync(ct);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4L));
        try
        {
            if (!Preferences.Enabled)
            {
                activity = null;
            }
            if (!IsConfigured)
            {
                Status = (Preferences.Enabled ? "Discord presence is unavailable in this build." : "Discord presence is off.");
                return;
            }
            string environmentVariable = Environment.GetEnvironmentVariable("NX_DISCORD_BRIDGE_DIR");
            string response;
            if (!string.IsNullOrWhiteSpace(environmentVariable))
            {
                if (!Path.IsPathFullyQualified(environmentVariable) || !Directory.Exists(environmentVariable))
                {
                    throw new IOException("The Linux presence bridge is unavailable. Relaunch from the applications menu.");
                }
                string nonce = Guid.NewGuid().ToString("N");
                AvatarStore.WriteAtomic(Path.Combine(environmentVariable, "request.json"), JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = 1,
                    nonce = nonce,
                    applicationId = ApplicationId,
                    activity = activity
                }, Json));
                response = Path.Combine(environmentVariable, "response.json");
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (File.Exists(response) && new FileInfo(response).Length <= 8192)
                    {
                        try
                        {
                            using JsonDocument jsonDocument = JsonDocument.Parse(await File.ReadAllTextAsync(response, timeout.Token));
                            if (jsonDocument.RootElement.GetProperty("nonce").GetString() == nonce)
                            {
                                if (!jsonDocument.RootElement.GetProperty("ok").GetBoolean())
                                {
                                    throw new IOException(jsonDocument.RootElement.GetProperty("error").GetString());
                                }
                                break;
                            }
                        }
                        catch (JsonException)
                        {
                        }
                    }
                    await Task.Delay(100, timeout.Token);
                }
                Status = (((object)activity == null) ? "Discord presence cleared." : "Connected through the Linux Discord bridge.");
                return;
            }
            if (_stream == null || _application != ApplicationId)
            {
                Disconnect();
                _stream = await ConnectAsync(timeout.Token);
                _application = ApplicationId;
                await WriteFrameAsync(_stream, 0, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    v = 1,
                    client_id = _application
                }), timeout.Token);
                if (!(await ReadMessageAsync(_stream, timeout.Token)).TryGetProperty("evt", out var value) || value.GetString() != "READY")
                {
                    throw new IOException("Discord rejected the application handshake.");
                }
            }
            response = Guid.NewGuid().ToString("N");
            await WriteFrameAsync(_stream, 1, JsonSerializer.SerializeToUtf8Bytes(new
            {
                cmd = "SET_ACTIVITY",
                args = new
                {
                    pid = Environment.ProcessId,
                    activity = activity
                },
                nonce = response
            }, Json), timeout.Token);
            for (int i = 0; i < 20; i++)
            {
                JsonElement jsonElement = await ReadMessageAsync(_stream, timeout.Token);
                if (jsonElement.TryGetProperty("nonce", out var value2) && value2.GetString() == response)
                {
                    if (jsonElement.TryGetProperty("evt", out var value3) && value3.GetString() == "ERROR")
                    {
                        throw new IOException("Discord rejected the presence update. Check the application ID and image asset.");
                    }
                    Status = (((object)activity == null) ? "Discord presence cleared." : "Connected to Discord.");
                    return;
                }
            }
            throw new IOException("Discord did not acknowledge the presence update.");
        }
        catch (Exception ex2) when ((ex2 is IOException || ex2 is OperationCanceledException || ex2 is SocketException || ex2 is UnauthorizedAccessException || ex2 is JsonException) ? true : false)
        {
            Disconnect();
            Status = ((ex2 is OperationCanceledException) ? "Discord is unavailable. Start the desktop client; NX Infinity will reconnect." : ex2.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<Stream> ConnectAsync(CancellationToken ct)
    {
        for (int i = 0; i < 10; i++)
        {
            if (OperatingSystem.IsWindows())
            {
                NamedPipeClientStream pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(150, ct);
                    return pipe;
                }
                catch (TimeoutException)
                {
                    pipe.Dispose();
                }
                catch (IOException)
                {
                    pipe.Dispose();
                }
                continue;
            }
            foreach (string item in new string[3]
            {
                Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
                Environment.GetEnvironmentVariable("TMPDIR"),
                "/tmp"
            }.OfType<string>().Distinct())
            {
                Socket socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(item, "discord-ipc-" + i)), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException)
                {
                    socket.Dispose();
                }
            }
        }
        throw new IOException("Discord desktop is not running. NX Infinity will reconnect automatically.");
    }

    public static async Task WriteFrameAsync(Stream stream, int opcode, byte[] body, CancellationToken ct)
    {
        if (body.Length > 65536)
        {
            throw new IOException("Discord frame exceeds its size limit.");
        }
        byte[] array = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(array, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(array.AsSpan(4), body.Length);
        await stream.WriteAsync(array, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<JsonElement> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        while (true)
        {
            byte[] header = new byte[8];
            await stream.ReadExactlyAsync(header, ct);
            int opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
            int num = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if ((num < 0 || num > 65536) ? true : false)
            {
                break;
            }
            byte[] body = new byte[num];
            await stream.ReadExactlyAsync(body, ct);
            switch (opcode)
            {
                case 3:
                    await WriteFrameAsync(stream, 4, body, ct);
                    break;
                default:
                    throw new IOException("Discord disconnected.");
                case 1:
                    {
                        using JsonDocument jsonDocument = JsonDocument.Parse(body);
                        return jsonDocument.RootElement.Clone();
                    }
            }
        }
        throw new IOException("Discord returned an invalid frame size.");
    }

    private void Disconnect()
    {
        _stream?.Dispose();
        _stream = null;
        _application = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream != null || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NX_DISCORD_BRIDGE_DIR")))
        {
            await PublishAsync(null);
        }
        Disconnect();
    }
}
