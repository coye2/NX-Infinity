using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NxInfinity;

public sealed class LocalTranslator : IDisposable
{
    private readonly string _root;

    private readonly string _scripts;

    private readonly string _preferences;

    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1);

    private readonly ConcurrentDictionary<string, string> _cache = new ConcurrentDictionary<string, string>();

    private Process? _worker;

    private bool _disposed;

    public TranslationPreferences Preferences { get; private set; }

    public string? LastError { get; private set; }

    public bool Available => File.Exists(Path.Combine(_scripts, "worker.py"));

    public string ModelsRoot => Path.Combine(_root, "models");

    public LocalTranslator(InfinityPaths paths)
    {
        _root = Path.Combine(paths.DataRoot, "translation");
        _scripts = Path.Combine(paths.InstallRoot, "translation");
        if (!Directory.Exists(_scripts))
        {
            _scripts = Path.Combine(AppContext.BaseDirectory, "translation");
        }
        _preferences = Path.Combine(_root, "preferences.json");
        Preferences = new TranslationPreferences();
        try
        {
            if (File.Exists(_preferences) && new FileInfo(_preferences).Length < 16384)
            {
                Preferences = JsonSerializer.Deserialize<TranslationPreferences>(File.ReadAllText(_preferences)) ?? new TranslationPreferences();
            }
            Validate(Preferences);
        }
        catch (Exception ex) when ((ex is IOException || ex is JsonException || ex is ArgumentException) ? true : false)
        {
            Preferences = new TranslationPreferences();
            LastError = "Translation preferences could not be read.";
        }
    }

    public void Save(TranslationPreferences value)
    {
        Validate(value);
        Directory.CreateDirectory(_root);
        AvatarStore.WriteAtomic(_preferences, JsonSerializer.SerializeToUtf8Bytes(value));
        Preferences = value;
    }

    private static void Validate(TranslationPreferences value)
    {
        bool flag = !Regex.IsMatch(value.SourceLanguage, "^[a-z]{2,3}$") || !Regex.IsMatch(value.TargetLanguage, "^[a-z]{2,3}$");
        if (!flag)
        {
            int intervalSeconds = value.IntervalSeconds;
            bool flag2 = ((intervalSeconds < 2 || intervalSeconds > 30) ? true : false);
            flag = flag2;
        }
        if (flag)
        {
            throw new ArgumentException("Choose valid translation languages and a 2–30 second interval.");
        }
    }

    public TranslationLanguage[] Languages()
    {
        try
        {
            using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(_scripts, "catalog.json")));
            return (from l in jsonDocument.RootElement.EnumerateArray().SelectMany((JsonElement m) => new TranslationLanguage[2]
                {
                    new TranslationLanguage(m.GetProperty("from_code").GetString(), m.GetProperty("from_name").GetString()),
                    new TranslationLanguage(m.GetProperty("to_code").GetString(), m.GetProperty("to_name").GetString())
                }).DistinctBy((TranslationLanguage l) => l.Id)
                    orderby l.Name
                    select l).ToArray();
        }
        catch (Exception ex) when ((ex is IOException || ex is JsonException) ? true : false)
        {
            return new TranslationLanguage[2]
            {
                new TranslationLanguage("en", "English"),
                new TranslationLanguage("it", "Italian")
            };
        }
    }

    public bool HasPair(string source, string target)
    {
        if (!(source == target) && !File.Exists(Path.Combine(ModelsRoot, source + "_" + target, "model", "model.bin")))
        {
            if (File.Exists(Path.Combine(ModelsRoot, source + "_en", "model", "model.bin")))
            {
                return File.Exists(Path.Combine(ModelsRoot, "en_" + target, "model", "model.bin"));
            }
            return false;
        }
        return true;
    }

    public async Task InstallPairAsync(string source, string target, CancellationToken ct)
    {
        if (source == target || HasPair(source, target))
        {
            return;
        }
        JsonDocument catalog = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_scripts, "catalog.json"), ct));
        try
        {
            if (Direct(source, target))
            {
                await RequestAsync(new
                {
                    operation = "install",
                    source = source,
                    target = target
                }, TimeSpan.FromMinutes(15L), ct);
                return;
            }
            if (!(source != "en") || !(target != "en") || !Direct(source, "en") || !Direct("en", target))
            {
                throw new InvalidOperationException("This language pair is not available in the bundled model catalog.");
            }
            await InstallPairAsync(source, "en", ct);
            await InstallPairAsync("en", target, ct);
        }
        finally
        {
            if (catalog != null)
            {
                ((IDisposable)catalog).Dispose();
            }
        }
        bool Direct(string a, string b)
        {
            return catalog.RootElement.EnumerateArray().Any((JsonElement m) => m.GetProperty("from_code").GetString() == a && m.GetProperty("to_code").GetString() == b);
        }
    }

    public async Task<string> TranslateAsync(string text, string source, string target, CancellationToken ct = default(CancellationToken))
    {
        if (text.Length > 4000)
        {
            throw new ArgumentException("Translate a smaller text selection (up to 4,000 characters).");
        }
        if (source == target || string.IsNullOrWhiteSpace(text))
        {
            return text;
        }
        string key = source + ":" + target + ":" + text;
        if (_cache.TryGetValue(key, out string value))
        {
            return value;
        }
        string text2 = (await RequestAsync(new
        {
            operation = "translate",
            source = source,
            target = target,
            texts = new string[1] { text }
        }, TimeSpan.FromSeconds(45L), ct)).GetProperty("texts")[0].GetString() ?? text;
        if (!(from m in Regex.Matches(text, "\\{\\d+[^{}]*\\}")
              select m.Value).SequenceEqual(from m in Regex.Matches(text2, "\\{\\d+[^{}]*\\}")
                                            select m.Value))
        {
            throw new InvalidDataException("Translation changed a formatting placeholder.");
        }
        if (_cache.Count >= 3000)
        {
            _cache.Clear();
        }
        _cache[key] = text2;
        return text2;
    }

    public async Task<string[]> TranslateBatchAsync(string[] texts, string source, string target, CancellationToken ct = default(CancellationToken))
    {
        if (texts.Length == 0)
        {
            return Array.Empty<string>();
        }
        string?[] array = (from x in (await RequestAsync(new
        {
            operation = "translate",
            source = source,
            target = target,
            texts = texts
        }, TimeSpan.FromSeconds(90L), ct)).GetProperty("texts").EnumerateArray()
                           select x.GetString() ?? "").ToArray();
        if (array.Length != texts.Length)
        {
            throw new InvalidDataException("Translation batch length mismatch.");
        }
        return array;
    }

    private async Task<JsonElement> RequestAsync(object request, TimeSpan timeout, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker == null || _worker.HasExited)
            {
                string text = (OperatingSystem.IsWindows() ? Path.Combine(_scripts, "python", "python.exe") : "python3");
                if (!Available || (OperatingSystem.IsWindows() && !File.Exists(text)))
                {
                    throw new IOException("The local translation runtime is missing. Install the complete NX Infinity package.");
                }
                ProcessStartInfo processStartInfo = new ProcessStartInfo(text)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                processStartInfo.ArgumentList.Add("-u");
                processStartInfo.ArgumentList.Add(Path.Combine(_scripts, "worker.py"));
                processStartInfo.ArgumentList.Add("--models-root");
                processStartInfo.ArgumentList.Add(ModelsRoot);
                processStartInfo.Environment["PYTHONIOENCODING"] = "utf-8";
                _worker = Process.Start(processStartInfo) ?? throw new IOException("The translation runtime could not start.");
                _worker.ErrorDataReceived += (object _, DataReceivedEventArgs _) =>
                {
                };
                _worker.BeginErrorReadLine();
                try
                {
                    _worker.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch (Exception ex) when ((ex is Win32Exception || ex is InvalidOperationException) ? true : false)
                {
                }
            }
            await _worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token);
            await _worker.StandardInput.FlushAsync(deadline.Token);
            string text2 = await _worker.StandardOutput.ReadLineAsync(deadline.Token);
            if (text2 == null || text2.Length > 200000)
            {
                throw new IOException("The translation worker stopped or returned an invalid response.");
            }
            using JsonDocument jsonDocument = JsonDocument.Parse(text2);
            if (!jsonDocument.RootElement.GetProperty("ok").GetBoolean())
            {
                throw new InvalidOperationException(jsonDocument.RootElement.GetProperty("error").GetString());
            }
            LastError = null;
            return jsonDocument.RootElement.Clone();
        }
        catch (Exception ex2)
        {
            LastError = ex2.Message;
            StopWorker();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StopWorker()
    {
        if (_worker == null)
        {
            return;
        }
        try
        {
            if (!_worker.HasExited)
            {
                _worker.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when ((ex is InvalidOperationException || ex is Win32Exception) ? true : false)
        {
        }
        _worker.Dispose();
        _worker = null;
    }

    public void Dispose()
    {
        _disposed = true;
        StopWorker();
    }
}
