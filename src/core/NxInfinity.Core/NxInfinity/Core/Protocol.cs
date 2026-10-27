using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NxInfinity.Core;

internal sealed class Protocol : IDisposable
{
    public const int MaxMessageBytes = 1048576;

    private readonly StreamWriter _output;

    private readonly Stream _providedInput;

    private readonly object _outputLock = new object();

    private readonly object _readerLock = new object();

    private readonly object _callbackLock = new object();

    private readonly TaskCompletionSource<bool> _readerCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    private Thread _reader;

    private bool _readerFinished;

    private bool _queueDisposed;

    private int _disposed;

    private int _endOfInput;

    public BlockingCollection<Request> Requests { get; } = new BlockingCollection<Request>(64);

    public bool EndOfInput => Volatile.Read(in _endOfInput) != 0;

    internal Task ReaderCompletion => _readerCompleted.Task;

    public Action<Request> ImmediateRequest { get; set; }

    public Action InputClosed { get; set; }

    internal Protocol(Stream output = null, Stream input = null)
    {
        _output = new StreamWriter(output ?? Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), -1, output != null)
        {
            AutoFlush = true
        };
        _providedInput = input;
    }

    public void StartReader()
    {
        lock (_readerLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(in _disposed) != 0, this);
            if (_reader != null)
            {
                throw new InvalidOperationException("The protocol reader is already started.");
            }
            _reader = new Thread(ReadLoop)
            {
                Name = "NX.Protocol.Input",
                IsBackground = true
            };
            try
            {
                _reader.Start();
            }
            catch
            {
                _reader = null;
                throw;
            }
        }
    }

    private void ReadLoop()
    {
        try
        {
            using Stream stream = new BufferedStream(_providedInput ?? Console.OpenStandardInput(), 16384);
            byte[] array = new byte[1048576];
            int num = 0;
            long num2 = 0L;
            while (Volatile.Read(in _disposed) == 0)
            {
                int num3 = stream.ReadByte();
                if (num3 == -1 || Volatile.Read(in _disposed) != 0)
                {
                    break;
                }
                if (num3 != 10)
                {
                    if (num >= 1048575)
                    {
                        throw new NxError("invalid_argument", "Message exceeds the 1 MiB protocol limit.");
                    }
                    array[num++] = (byte)num3;
                    continue;
                }
                if (num > 0 && array[num - 1] == 13)
                {
                    num--;
                }
                if (num == 0)
                {
                    continue;
                }
                long value = 0L;
                try
                {
                    using JsonDocument jsonDocument = JsonDocument.Parse(array.AsMemory(0, num), new JsonDocumentOptions
                    {
                        MaxDepth = 32
                    });
                    JsonElement rootElement = jsonDocument.RootElement;
                    if (rootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new NxError("invalid_argument", "A protocol request must be a JSON object.");
                    }
                    if (!rootElement.TryGetProperty("id", out var value2) || value2.ValueKind != JsonValueKind.Number || !value2.TryGetInt64(out value) || value <= num2)
                    {
                        throw new NxError("invalid_argument", "Request IDs must be positive and strictly increasing.");
                    }
                    num2 = value;
                    if (!rootElement.TryGetProperty("v", out var value3) || value3.ValueKind != JsonValueKind.Number || !value3.TryGetInt32(out var value4) || value4 != 1)
                    {
                        throw new NxError("protocol_mismatch", "This host implements protocol 1.");
                    }
                    string text = Fields.RequiredString(rootElement, "method");
                    if (!rootElement.TryGetProperty("params", out var value5) || value5.ValueKind != JsonValueKind.Object)
                    {
                        throw new NxError("invalid_argument", "params must be a JSON object.");
                    }
                    Request request = new Request(value, text, value5.Clone());
                    if (text == "interaction.respond" && ImmediateRequest != null)
                    {
                        lock (_callbackLock)
                        {
                            if (Volatile.Read(in _disposed) == 0)
                            {
                                ImmediateRequest?.Invoke(request);
                            }
                        }
                    }
                    else if (!Requests.TryAdd(request))
                    {
                        Failure(value, "busy", "The request queue is full. Retry with a new ID after pending commands finish.");
                    }
                }
                catch (NxError nxError)
                {
                    Failure(value, nxError.Code, nxError.Message);
                }
                catch (JsonException)
                {
                    Failure(value, "invalid_argument", "Malformed JSON request.");
                }
                finally
                {
                    num = 0;
                }
            }
            if (num != 0 && Volatile.Read(in _disposed) == 0)
            {
                Failure(0L, "invalid_argument", "Truncated request: newline required.");
            }
        }
        catch (NxError nxError2)
        {
            ReportReaderError(nxError2.Code, nxError2.Message);
        }
        catch (IOException)
        {
        }
        catch (Exception ex3)
        {
            ReportReaderError("core_error", "Protocol reader failed (" + ex3.GetType().Name + ").");
        }
        finally
        {
            try
            {
                Requests.CompleteAdding();
                lock (_callbackLock)
                {
                    if (Volatile.Read(in _disposed) == 0)
                    {
                        InputClosed?.Invoke();
                    }
                }
            }
            catch (Exception ex4)
            {
                ReportReaderError("core_error", "Protocol input closure failed (" + ex4.GetType().Name + ").");
            }
            finally
            {
                Volatile.Write(ref _endOfInput, 1);
                lock (_readerLock)
                {
                    _readerFinished = true;
                    if (Volatile.Read(in _disposed) != 0)
                    {
                        ReleaseQueue();
                    }
                }
                _readerCompleted.TrySetResult(result: true);
            }
        }
    }

    private void ReportReaderError(string code, string message)
    {
        try
        {
            Event("protocol.error", new { code, message });
        }
        catch (Exception ex) when ((ex is IOException || ex is ObjectDisposedException) ? true : false)
        {
        }
    }

    public void Success(long id, object result)
    {
        Write(new
        {
            v = 1,
            id = id,
            ok = true,
            result = result
        });
    }

    public void Failure(long id, string code, string message)
    {
        Write(new
        {
            v = 1,
            id = id,
            ok = false,
            error = new { code, message }
        });
    }

    public void Event(string name, object data)
    {
        Write(new
        {
            v = 1,
            @event = name,
            data = data
        });
    }

    private void Write(object value)
    {
        if (Volatile.Read(in _disposed) != 0)
        {
            return;
        }
        string text = JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
        if (Encoding.UTF8.GetByteCount(text) >= 1048576)
        {
            throw new NxError("core_error", "Outgoing message exceeds protocol size limit.");
        }
        lock (_outputLock)
        {
            if (Volatile.Read(in _disposed) == 0)
            {
                _output.WriteLine(text);
            }
        }
    }

    private void ReleaseQueue()
    {
        if (!_queueDisposed)
        {
            _queueDisposed = true;
            Requests.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_readerLock)
        {
            if (Volatile.Read(in _disposed) != 0)
            {
                return;
            }
            Volatile.Write(ref _disposed, 1);
            if (_reader == null || _readerFinished)
            {
                ReleaseQueue();
            }
            if (_reader == null)
            {
                _readerCompleted.TrySetResult(result: true);
            }
        }
        lock (_callbackLock)
        {
            ImmediateRequest = null;
            InputClosed = null;
        }
        lock (_outputLock)
        {
            try
            {
                _output.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }
}
