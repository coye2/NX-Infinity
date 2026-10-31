using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NxInfinity;

public sealed class AvatarCatalog : IDisposable
{
    public const string Repository = "henry-debruin/nso-icons";

    private readonly HttpClient _http;

    private readonly string _cache;

    private readonly SemaphoreSlim _downloads = new SemaphoreSlim(4);

    public const string IslandSource = "https://www.hellokittyislandadventure.com/";

    public const string IslandAttribution = "Hello Kitty Island Adventure · © 2026 SANRIO CO., LTD. · Sunblink";

    public static readonly AvatarCategory IslandCategory = new AvatarCategory("hello-kitty-island-adventure", "Hello Kitty Island Adventure");

    private static readonly IReadOnlyDictionary<string, (string Name, string Url)> IslandCharacters = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["hello-kitty-island-adventure/characters/kitty.png"] = ("Hello Kitty", "https://static.wixstatic.com/media/b0050c_e051d3610ded476eb57590df19945607~mv2.png"),
        ["hello-kitty-island-adventure/characters/cinna.png"] = ("Cinnamoroll", "https://static.wixstatic.com/media/b0050c_b5ea03d40d51439ba4361e75e18578ee~mv2.png"),
        ["hello-kitty-island-adventure/characters/kuromi.png"] = ("Kuromi", "https://static.wixstatic.com/media/b0050c_5343278aac174b7fa391acc0ff565187~mv2.png"),
        ["hello-kitty-island-adventure/characters/mel.png"] = ("My Melody", "https://static.wixstatic.com/media/b0050c_55b16aa2ed654ca287c2183b0ad3f1e3~mv2.png"),
        ["hello-kitty-island-adventure/characters/hangy.png"] = ("Hangyodon", "https://static.wixstatic.com/media/b0050c_358a7d6650b0405e9f16053a13205bbf~mv2.png"),
        ["hello-kitty-island-adventure/characters/retsuko.png"] = ("Retsuko", "https://static.wixstatic.com/media/b0050c_509239115cb34cc4ad9c2b749dace181~mv2.png"),
        ["hello-kitty-island-adventure/characters/badtz.png"] = ("Badtz-maru", "https://static.wixstatic.com/media/b0050c_9e241774f9cb467f8388009af2a3d1f5~mv2.png"),
        ["hello-kitty-island-adventure/characters/choco.png"] = ("Chococat", "https://static.wixstatic.com/media/b0050c_5b91fe3ea098476b888b596e8bec4462~mv2.png"),
        ["hello-kitty-island-adventure/characters/kero.png"] = ("Keroppi", "https://static.wixstatic.com/media/b0050c_401a0bf6b6ea46b29a408df890bd38cd~mv2.png"),
        ["hello-kitty-island-adventure/characters/pom.png"] = ("Pompompurin", "https://static.wixstatic.com/media/b0050c_c6c5abc969314d5e94f84916ba939590~mv2.png"),
        ["hello-kitty-island-adventure/characters/poch.png"] = ("Pochacco", "https://static.wixstatic.com/media/b0050c_ae5997cdc94146a0a3f27af43aa6bb4e~mv2.png"),
        ["hello-kitty-island-adventure/characters/peck.png"] = ("Pekkle", "https://static.wixstatic.com/media/b0050c_693784adf8ae44ec9a2c6ef545606701~mv2.png"),
        ["hello-kitty-island-adventure/characters/tux.png"] = ("Tuxedosam", "https://static.wixstatic.com/media/b0050c_8110fba71b1c4015b7fdaeace3b012b6~mv2.png")
    };

    public bool Offline { get; private set; }

    public AvatarCatalog(string root, HttpMessageHandler? handler = null)
    {
        _cache = Path.Combine(root, "catalog");
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromSeconds(15L)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NX-Infinity-Avatars/1.0");
    }

    public static bool ValidImagePath(string? path)
    {
        if (path != null && path.Length <= 220 && Regex.IsMatch(path, "\\A[a-z0-9-]+/(characters|backgrounds|frames|mii)/[A-Za-z0-9_. -]+\\.(png|jpg|jpeg)\\z", RegexOptions.IgnoreCase))
        {
            return !path.Contains("..");
        }
        return false;
    }

    private static string Hash(string text)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private async Task<byte[]> DownloadAsync(Uri uri, int maximum, CancellationToken ct)
    {
        await _downloads.WaitAsync(ct);
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximum)
            {
                throw new InvalidDataException("Avatar download exceeds the size limit.");
            }
            using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using MemoryStream output = new MemoryStream();
            byte[] buffer = new byte[16384];
            int num;
            while ((num = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (output.Length + num > maximum)
                {
                    throw new InvalidDataException("Avatar download exceeds the size limit.");
                }
                await output.WriteAsync(buffer.AsMemory(0, num), ct);
            }
            return output.ToArray();
        }
        finally
        {
            _downloads.Release();
        }
    }

    private async Task<JsonDocument> ListingAsync(string path, CancellationToken ct)
    {
        string file = Path.Combine(_cache, Hash(path) + ".json");
        Offline = false;
        if (File.Exists(file) && new FileInfo(file).Length > 2097152)
        {
            throw new InvalidDataException("Cached avatar listing exceeds the size limit.");
        }
        if (File.Exists(file) && File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddDays(-1.0))
        {
            return JsonDocument.Parse(await File.ReadAllBytesAsync(file, ct));
        }
        try
        {
            byte[] array = await DownloadAsync(new Uri("https://api.github.com/repos/henry-debruin/nso-icons/contents/" + path + "?ref=main"), 2097152, ct);
            using JsonDocument jsonDocument = JsonDocument.Parse(array);
            if (jsonDocument.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The avatar catalog returned an invalid listing.");
            }
            AvatarStore.WriteAtomic(file, array);
            return JsonDocument.Parse(array);
        }
        catch (Exception ex) when (((Func<bool>)delegate
        {
            // Could not convert BlockContainer to single expression
            bool flag = !ct.IsCancellationRequested;
            if (flag)
            {
                flag = ((ex is HttpRequestException || ex is TaskCanceledException) ? true : false);
            }
            return flag;
        }).Invoke())
        {
            if (!File.Exists(file))
            {
                throw new IOException("Avatar catalog unavailable. Check your connection or import a local image.", ex);
            }
            Offline = true;
            return JsonDocument.Parse(await File.ReadAllBytesAsync(file, ct));
        }
    }

    public async Task<AvatarCategory[]> CategoriesAsync(CancellationToken ct = default(CancellationToken))
    {
        JsonDocument jsonDocument;
        try
        {
            jsonDocument = await ListingAsync("", ct);
        }
        catch (IOException)
        {
            Offline = true;
            return new AvatarCategory[1] { IslandCategory };
        }
        using (jsonDocument)
        {
            return (from x in (from x in jsonDocument.RootElement.EnumerateArray()
                               where x.GetProperty("type").GetString() == "dir"
                               select x.GetProperty("name").GetString() into x
                               where Regex.IsMatch(x, "\\A[a-z0-9-]+\\z")
                               select new AvatarCategory(x, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(x.Replace('-', ' ')))).Append(IslandCategory)
                    orderby x.Label
                    select x).ToArray();
        }
    }

    public async Task<AvatarItem[]> ItemsAsync(string category, string part = "characters", CancellationToken ct = default(CancellationToken))
    {
        bool flag = !Regex.IsMatch(category, "\\A[a-z0-9-]+\\z");
        if (!flag)
        {
            bool flag2;
            switch (part)
            {
                case "characters":
                case "backgrounds":
                case "frames":
                case "mii":
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
            throw new ArgumentException("Invalid avatar category.");
        }
        if (category == IslandCategory.Id)
        {
            return (part == "characters") ? IslandCharacters.Select((KeyValuePair<string, (string Name, string Url)> x) => new AvatarItem(x.Key, x.Value.Name)).ToArray() : Array.Empty<AvatarItem>();
        }
        using JsonDocument jsonDocument = await ListingAsync(category + "/" + part, ct);
        return (from x in (from x in jsonDocument.RootElement.EnumerateArray()
                           where x.GetProperty("type").GetString() == "file"
                           select x.GetProperty("path").GetString()).Where(ValidImagePath)
                select new AvatarItem(x, Path.GetFileNameWithoutExtension(x))).ToArray();
    }

    public async Task<byte[]> ImageAsync(string path, CancellationToken ct = default(CancellationToken))
    {
        ct.ThrowIfCancellationRequested();
        if (!ValidImagePath(path))
        {
            throw new ArgumentException("Invalid avatar image path.");
        }
        string file = Path.Combine(_cache, Hash(path) + ".png");
        if (File.Exists(file) && new FileInfo(file).Length <= 2097152)
        {
            try
            {
                byte[] array = await File.ReadAllBytesAsync(file, ct);
                byte[] array2 = AvatarStore.ReadCachedImage(array);
                if (array2 != array)
                {
                    AvatarStore.WriteAtomic(file, array2);
                }
                return array2;
            }
            catch (InvalidDataException)
            {
                File.Delete(file);
            }
        }
        Uri uri2 = (IslandCharacters.TryGetValue(path, out (string, string) value) ? new Uri(value.Item2) : new Uri("https://raw.githubusercontent.com/henry-debruin/nso-icons/main/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString))));
        byte[] bytes = await DownloadAsync(uri2, 8388608, ct);
        byte[] array4 = await Task.Run(() => AvatarStore.Normalize(bytes), ct);
        AvatarStore.WriteAtomic(file, array4);
        Trim();
        return array4;
    }

    private void Trim()
    {
        long num = 0L;
        int num2 = 0;
        foreach (FileInfo item in from f in new DirectoryInfo(_cache).EnumerateFiles("*.png")
                                  orderby f.LastWriteTimeUtc descending
                                  select f)
        {
            num += item.Length;
            num2++;
            if (num2 > 256 || num > 67108864)
            {
                try
                {
                    item.Delete();
                }
                catch (IOException)
                {
                }
            }
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
