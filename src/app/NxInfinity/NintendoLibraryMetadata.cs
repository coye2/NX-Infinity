using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NxInfinity;

public sealed class NintendoLibraryMetadata : IDisposable
{
    private sealed record BackgroundReceipt(string Identity, NintendoCatalogGame Game, string Url, string Sha256, string MediaType);

    private sealed record ArtworkInfo(string Sha256, string MediaType, int Width, int Height, int Bytes);

    private readonly SemaphoreSlim _backgroundRequests = new SemaphoreSlim(1, 1);

    private readonly Dictionary<string, DateTimeOffset> _backgroundRetries = new Dictionary<string, DateTimeOffset>();

    public const int MaximumResponseBytes = 524288;

    public const int MaximumArtworkBytes = 4194304;

    public const int MaximumArtworkDimension = 2048;

    public const string CatalogSource = "Nintendo Europe public catalog";

    private const int MaximumResults = 24;

    private const string Endpoint = "https://search.nintendo-europe.com/en/select";

    private static readonly Uri ProductOrigin = new Uri("https://www.nintendo.com/");

    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        MaxDepth = 24
    };

    private readonly HttpClient _http;

    private readonly string _root;

    private readonly SemaphoreSlim _writes = new SemaphoreSlim(1, 1);

    private readonly ConcurrentDictionary<string, NintendoCatalogSearch> _searches = new ConcurrentDictionary<string, NintendoCatalogSearch>(StringComparer.Ordinal);

    private bool _disposed;

    public async Task<byte[]?> GetBackgroundAsync(string entryId, string displayName, CancellationToken cancellationToken = default(CancellationToken))
    {
        ValidateEntryId(entryId);
        await _backgroundRequests.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            NintendoCatalogGame selected = GetSelection(entryId)?.Game;
            string query = SuggestQuery(selected?.DisplayTitle ?? displayName);
            string identity = selected?.CatalogKey ?? ("title:" + TitleKey(query));
            string receiptPath = CachePath("backgrounds", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
            if (File.Exists(receiptPath))
            {
                try
                {
                    if (new FileInfo(receiptPath).Length > 32768)
                    {
                        throw new InvalidDataException();
                    }
                    BackgroundReceipt backgroundReceipt = JsonSerializer.Deserialize<BackgroundReceipt>(File.ReadAllBytes(receiptPath), Json);
                    bool flag = (object)backgroundReceipt != null && backgroundReceipt.Identity == identity && HexHash(backgroundReceipt.Sha256);
                    if (flag)
                    {
                        string mediaType = backgroundReceipt.MediaType;
                        bool flag2 = ((mediaType == "image/png" || mediaType == "image/jpeg") ? true : false);
                        flag = flag2;
                    }
                    if (flag)
                    {
                        ValidateGame(backgroundReceipt.Game);
                        OfficialUri(backgroundReceipt.Url, artwork: true);
                        string text = ArtworkPath(backgroundReceipt.Sha256, backgroundReceipt.MediaType);
                        if (new FileInfo(text).Length <= 8388608)
                        {
                            byte[] array = File.ReadAllBytes(text);
                            if (InspectArtwork(array, background: true).Sha256 == backgroundReceipt.Sha256)
                            {
                                return array;
                            }
                        }
                    }
                }
                catch (Exception ex) when ((ex is IOException || ex is JsonException || ex is LibraryMetadataException) ? true : false)
                {
                }
            }
            if (_backgroundRetries.TryGetValue(identity, out var value) && DateTimeOffset.UtcNow < value)
            {
                return null;
            }
            _backgroundRetries[identity] = DateTimeOffset.UtcNow.AddMinutes(10.0);
            NintendoCatalogGame candidate = selected;
            if (candidate?.LandscapeUrl == null)
            {
                NintendoCatalogGame[] array2 = (await SearchAsync(query, cancellationToken)).Results.Where((NintendoCatalogGame g) => ((object)selected == null) ? (TitleKey(g.DisplayTitle) == TitleKey(query)) : (g.CatalogKey == selected.CatalogKey)).ToArray();
                if (array2.Length != 1)
                {
                    return null;
                }
                candidate = array2[0];
            }
            string landscapeUrl = candidate.LandscapeUrl;
            if (landscapeUrl == null)
            {
                return null;
            }
            string text2 = Regex.Replace(landscapeUrl, "_image\\d+w(?=\\.(jpg|png)$)", "", RegexOptions.IgnoreCase);
            string text3 = Regex.Replace(landscapeUrl, "_image\\d+w(?=\\.(jpg|png)$)", "_image1600w", RegexOptions.IgnoreCase);
            foreach (string url in new string[3] { text2, text3, landscapeUrl }.Distinct())
            {
                try
                {
                    (byte[] Bytes, string MediaType) response = await DownloadAsync(OfficialUri(url, artwork: true), artwork: true, cancellationToken, background: true);
                    ArtworkInfo info = InspectArtwork(response.Bytes, background: true);
                    if (info.MediaType != response.MediaType)
                    {
                        continue;
                    }
                    await _writes.WaitAsync(cancellationToken);
                    try
                    {
                        WriteAtomic(ArtworkPath(info.Sha256, info.MediaType), response.Bytes);
                        WriteAtomic(receiptPath, JsonSerializer.SerializeToUtf8Bytes(new BackgroundReceipt(identity, candidate, url, info.Sha256, info.MediaType), Json));
                    }
                    finally
                    {
                        _writes.Release();
                    }
                    return response.Bytes;
                }
                catch (LibraryMetadataException)
                {
                }
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            _backgroundRetries.Clear();
            throw;
        }
        catch (LibraryMetadataException)
        {
            return null;
        }
        finally
        {
            _backgroundRequests.Release();
        }
    }

    private static string TitleKey(string title)
    {
        return string.Concat(title.Normalize(NormalizationForm.FormD).Where(char.IsLetterOrDigit)).ToUpperInvariant();
    }

    public NintendoLibraryMetadata(string cacheRoot, HttpMessageHandler? testHandler = null)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot) || !Path.IsPathFullyQualified(cacheRoot))
        {
            throw new ArgumentException("Choose an absolute application metadata cache directory.", "cacheRoot");
        }
        _root = Path.GetFullPath(cacheRoot);
        RejectLinks(_root);
        Directory.CreateDirectory(_root);
        string[] array = new string[3] { "selections", "artwork", "backgrounds" };
        foreach (string text in array)
        {
            Directory.CreateDirectory(CachePath(text));
        }
        HttpMessageHandler handler = testHandler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
            AutomaticDecompression = (DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli)
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NX-Infinity/0.2.1 (public-library-metadata)");
    }

    public static string SuggestQuery(string displayName)
    {
        string input = (displayName ?? "").Replace('\\', '/').Split('/').LastOrDefault() ?? "";
        input = Regex.Replace(input, "\\.(xci|nsp|nro|nso|xcz|nsz)$", "", RegexOptions.IgnoreCase);
        input = Regex.Replace(input, "\\[[^\\]]*\\]", " ");
        input = Regex.Replace(input, "\\([^)]*\\.(com|net|org)[^)]*\\)", " ", RegexOptions.IgnoreCase);
        input = Regex.Replace(input, "\\b[0-9a-fA-F]{16,}\\b", " ");
        input = Regex.Replace(input, "\\s+", " ").Trim();
        return input.Substring(0, Math.Min(160, input.Length));
    }

    public async Task<NintendoCatalogSearch> SearchAsync(string query, CancellationToken cancellationToken = default(CancellationToken))
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        string normalized = NormalizeQuery(query);
        if (_searches.TryGetValue(normalized, out NintendoCatalogSearch value))
        {
            TimeSpan timeSpan = DateTimeOffset.UtcNow - value.RetrievedUtc;
            if (timeSpan >= TimeSpan.Zero && timeSpan < TimeSpan.FromMinutes(10L))
            {
                return value with
                {
                    FromCache = true
                };
            }
        }
        string[] terms = (from match in Regex.Matches(normalized, "[\\p{L}\\p{N}]+")
                          select "\"" + match.Value + "\"").ToArray();
        string value2 = "title:(" + string.Join(" AND ", terms) + ")";
        Dictionary<string, string> fields = new Dictionary<string, string>
        {
            ["q"] = value2,
            ["fq"] = "type:GAME AND (system_type:nintendoswitch_gamecard OR system_type:nintendoswitch_downloadsoftware)",
            ["q.op"] = "AND",
            ["rows"] = 24.ToString(CultureInfo.InvariantCulture),
            ["wt"] = "json",
            ["fl"] = "title,url,image_url_sq_s,image_url_h2x1_s,publisher,pretty_date_s,system_names_txt,type"
        };
        Uri uri = new Uri("https://search.nintendo-europe.com/en/select?" + string.Join('&', fields.Select((KeyValuePair<string, string> pair) => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
        (byte[], string) tuple = await DownloadAsync(uri, artwork: false, cancellationToken);
        NintendoCatalogSearch nintendoCatalogSearch = ParseSearch(normalized, tuple.Item1, DateTimeOffset.UtcNow);
        if (nintendoCatalogSearch.Results.Count == 0 && terms.Length > 1)
        {
            fields["q"] = "title:(" + string.Join(" OR ", terms) + ")";
            fields["q.op"] = "OR";
            uri = new Uri("https://search.nintendo-europe.com/en/select?" + string.Join('&', fields.Select((KeyValuePair<string, string> pair) => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
            tuple = await DownloadAsync(uri, artwork: false, cancellationToken);
            nintendoCatalogSearch = ParseSearch(normalized, tuple.Item1, DateTimeOffset.UtcNow);
            nintendoCatalogSearch = nintendoCatalogSearch with
            {
                Results = (from game in nintendoCatalogSearch.Results
                           orderby game.DisplayTitle.Equals(normalized, StringComparison.OrdinalIgnoreCase) descending, Regex.Matches(normalized, "[\\p{L}\\p{N}]+").Count((Match term) => game.DisplayTitle.Contains(term.Value, StringComparison.OrdinalIgnoreCase)) descending
                           select game).ToArray()
            };
        }
        if (_searches.Count >= 64)
        {
            _searches.Clear();
        }
        _searches[normalized] = nintendoCatalogSearch;
        return nintendoCatalogSearch;
    }

    public async Task<NintendoCatalogSelection> SelectAsync(string entryId, NintendoCatalogGame candidate, CancellationToken cancellationToken = default(CancellationToken))
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateEntryId(entryId);
        ValidateGame(candidate);
        byte[] bytes = null;
        ArtworkInfo info = null;
        DateTimeOffset? artworkRetrievedUtc = null;
        string squareCoverUrl = candidate.SquareCoverUrl;
        if (squareCoverUrl != null)
        {
            (byte[], string) tuple = await DownloadAsync(OfficialUri(squareCoverUrl, artwork: true), artwork: true, cancellationToken);
            bytes = tuple.Item1;
            info = InspectArtwork(bytes);
            if (tuple.Item2 != info.MediaType)
            {
                throw Error("metadata_artwork_invalid", "The artwork's media type does not match its bytes.");
            }
            artworkRetrievedUtc = DateTimeOffset.UtcNow;
        }
        NintendoCatalogSelection selection = new NintendoCatalogSelection(1, entryId, candidate, info?.Sha256, info?.MediaType, info?.Width, info?.Height, info?.Bytes, artworkRetrievedUtc, DateTimeOffset.UtcNow);
        await _writes.WaitAsync(cancellationToken);
        try
        {
            if (bytes != null && (object)info != null)
            {
                WriteAtomic(ArtworkPath(info.Sha256, info.MediaType), bytes);
            }
            WriteAtomic(SelectionPath(entryId), JsonSerializer.SerializeToUtf8Bytes(selection, Json));
            return selection;
        }
        finally
        {
            _writes.Release();
        }
    }

    public NintendoCatalogSelection? GetSelection(string entryId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string text = SelectionPath(entryId);
        if (!File.Exists(text))
        {
            return null;
        }
        try
        {
            long length = new FileInfo(text).Length;
            if ((length <= 0 || length > 32768) ? true : false)
            {
                throw Error("metadata_cache_invalid", "The cached selection exceeds its size limit.");
            }
            NintendoCatalogSelection nintendoCatalogSelection = JsonSerializer.Deserialize<NintendoCatalogSelection>(File.ReadAllBytes(text), Json) ?? throw Error("metadata_cache_invalid", "The cached selection is empty.");
            if (nintendoCatalogSelection.SchemaVersion != 1 || nintendoCatalogSelection.EntryId != entryId)
            {
                throw Error("metadata_cache_invalid", "The cached selection belongs to a different entry or schema.");
            }
            ValidateGame(nintendoCatalogSelection.Game);
            if (nintendoCatalogSelection.ArtworkSha256 == null)
            {
                if (nintendoCatalogSelection.ArtworkMediaType != null || nintendoCatalogSelection.ArtworkBytes.HasValue || nintendoCatalogSelection.ArtworkWidth.HasValue || nintendoCatalogSelection.ArtworkHeight.HasValue)
                {
                    throw Error("metadata_cache_invalid", "The cached artwork metadata is incomplete.");
                }
                goto IL_01f3;
            }
            bool flag = !HexHash(nintendoCatalogSelection.ArtworkSha256);
            if (!flag)
            {
                string artworkMediaType = nintendoCatalogSelection.ArtworkMediaType;
                bool flag2 = ((artworkMediaType == "image/png" || artworkMediaType == "image/jpeg") ? true : false);
                flag = !flag2;
            }
            if (!flag)
            {
                int? artworkBytes = nintendoCatalogSelection.ArtworkBytes;
                if (artworkBytes.HasValue)
                {
                    int valueOrDefault = artworkBytes.GetValueOrDefault();
                    if (valueOrDefault > 0 && valueOrDefault <= 4194304)
                    {
                        artworkBytes = nintendoCatalogSelection.ArtworkWidth;
                        if (artworkBytes.HasValue)
                        {
                            valueOrDefault = artworkBytes.GetValueOrDefault();
                            if (valueOrDefault > 0 && valueOrDefault <= 2048 && nintendoCatalogSelection.ArtworkHeight == nintendoCatalogSelection.ArtworkWidth && nintendoCatalogSelection.Game.SquareCoverUrl != null)
                            {
                                goto IL_01f3;
                            }
                        }
                    }
                }
            }
            throw Error("metadata_cache_invalid", "The cached artwork metadata is invalid.");
        IL_01f3:
            return nintendoCatalogSelection;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (JsonException inner)
        {
            throw Error("metadata_cache_invalid", "The cached selection is unreadable. Clear its catalog selection to retry.", inner);
        }
    }

    public byte[]? GetCachedArtwork(string entryId)
    {
        NintendoCatalogSelection selection = GetSelection(entryId);
        string text = selection?.ArtworkSha256;
        if (text != null)
        {
            string artworkMediaType = selection.ArtworkMediaType;
            if (artworkMediaType != null)
            {
                string text2 = ArtworkPath(text, artworkMediaType);
                if (!File.Exists(text2))
                {
                    return null;
                }
                if (new FileInfo(text2).Length != selection.ArtworkBytes)
                {
                    throw Error("metadata_cache_invalid", "The cached artwork size changed. Select its catalog cover again.");
                }
                byte[] array = File.ReadAllBytes(text2);
                ArtworkInfo artworkInfo = InspectArtwork(array);
                if (artworkInfo.Sha256 != text || artworkInfo.MediaType != artworkMediaType || artworkInfo.Width != selection.ArtworkWidth || artworkInfo.Height != selection.ArtworkHeight)
                {
                    throw Error("metadata_cache_invalid", "The cached artwork failed its integrity check. Select its catalog cover again.");
                }
                return array;
            }
        }
        return null;
    }

    public void ClearSelection(string entryId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writes.Wait();
        try
        {
            File.Delete(SelectionPath(entryId));
        }
        finally
        {
            _writes.Release();
        }
    }

    private static NintendoCatalogSearch ParseSearch(string query, byte[] bytes, DateTimeOffset retrievedUtc)
    {
        try
        {
            using JsonDocument jsonDocument = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 24
            });
            JsonElement rootElement = jsonDocument.RootElement;
            if (rootElement.ValueKind != JsonValueKind.Object || !rootElement.TryGetProperty("response", out var value) || value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("docs", out var value2) || value2.ValueKind != JsonValueKind.Array || value2.GetArrayLength() > 24)
            {
                throw Error("metadata_response_invalid", "Nintendo's catalog returned an unexpected response format.");
            }
            string catalogResponseSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            List<NintendoCatalogGame> list = new List<NintendoCatalogGame>();
            HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in value2.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || Text(item, "type", 16) != "GAME" || !item.TryGetProperty("system_names_txt", out var value3) || value3.ValueKind != JsonValueKind.Array || !value3.EnumerateArray().Any((JsonElement system) => system.ValueKind == JsonValueKind.String && system.GetString() == "Nintendo Switch"))
                {
                    continue;
                }
                string text = Text(item, "title", 200);
                string text2 = Text(item, "url", 2048);
                if (text == null || text2 == null)
                {
                    continue;
                }
                Uri uri;
                try
                {
                    uri = OfficialUri(text2, artwork: false);
                }
                catch (LibraryMetadataException)
                {
                    continue;
                }
                string squareCoverUrl = null;
                string text3 = Text(item, "image_url_sq_s", 2048);
                if (text3 != null)
                {
                    try
                    {
                        squareCoverUrl = OfficialUri(text3, artwork: true).AbsoluteUri;
                    }
                    catch (LibraryMetadataException)
                    {
                    }
                }
                string landscapeUrl = null;
                string text4 = Text(item, "image_url_h2x1_s", 2048);
                if (text4 != null)
                {
                    try
                    {
                        landscapeUrl = OfficialUri(text4, artwork: true).AbsoluteUri;
                    }
                    catch (LibraryMetadataException)
                    {
                    }
                }
                string text5 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
                if (hashSet.Add(text5))
                {
                    list.Add(new NintendoCatalogGame(text5, text, Text(item, "publisher", 160), Text(item, "pretty_date_s", 64), uri.AbsoluteUri, squareCoverUrl, "Nintendo Europe public catalog", retrievedUtc, catalogResponseSha, landscapeUrl));
                }
            }
            int totalResults = ((value.TryGetProperty("numFound", out var value4) && value4.ValueKind == JsonValueKind.Number && value4.TryGetInt32(out var value5)) ? Math.Clamp(value5, list.Count, 1000000) : list.Count);
            return new NintendoCatalogSearch(query, list.ToArray(), totalResults, FromCache: false, retrievedUtc);
        }
        catch (JsonException inner)
        {
            throw Error("metadata_response_invalid", "Nintendo's catalog did not return readable JSON.", inner);
        }
    }

    private async Task<(byte[] Bytes, string MediaType)> DownloadAsync(Uri uri, bool artwork, CancellationToken cancellationToken, bool background = false)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20L));
        try
        {
            for (int redirects = 0; redirects <= 3; redirects++)
            {
                ValidateRequestUri(uri, artwork);
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.ParseAdd(artwork ? "image/jpeg, image/png" : "application/json");
                using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                Uri uri2 = response.RequestMessage?.RequestUri;
                if ((object)uri2 != null)
                {
                    ValidateRequestUri(uri2, artwork);
                }
                HttpStatusCode statusCode = response.StatusCode;
                if (((uint)(statusCode - 301) <= 2u || (uint)(statusCode - 307) <= 1u) ? true : false)
                {
                    if (redirects == 3 || (object)response.Headers.Location == null)
                    {
                        throw Error("metadata_redirect_refused", "Nintendo's metadata request redirected too many times.");
                    }
                    uri = new Uri(uri, response.Headers.Location);
                    ValidateRequestUri(uri, artwork);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw Error("metadata_unavailable", "Nintendo's public catalog or artwork is temporarily unavailable (HTTP " + (int)response.StatusCode + "). Cached selections remain available.");
                }
                string mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                bool flag2;
                if (artwork)
                {
                    string text = mediaType;
                    bool flag = ((text == "image/png" || text == "image/jpeg") ? true : false);
                    flag2 = !flag;
                }
                else
                {
                    string text = mediaType;
                    bool flag = ((text == "application/json" || text == "text/json") ? true : false);
                    flag2 = !flag;
                }
                if (flag2)
                {
                    throw Error("metadata_response_invalid", "Nintendo's response has an unexpected content type.");
                }
                int maximum;
                if (background)
                {
                    maximum = 8388608;
                }
                else
                {
                    maximum = (artwork ? 4194304 : 524288);
                }
                long? contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue)
                {
                    long valueOrDefault = contentLength.GetValueOrDefault();
                    if (valueOrDefault > maximum)
                    {
                        throw Error("metadata_response_too_large", "Nintendo's response exceeds the permitted size.");
                    }
                }
                (byte[] Bytes, string MediaType) result;
                await using (Stream input = await response.Content.ReadAsStreamAsync(timeout.Token))
                {
                    using MemoryStream output = new MemoryStream();
                    byte[] buffer = new byte[16384];
                    while (true)
                    {
                        int num = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum + 1 - checked((int)output.Length))), timeout.Token);
                        if (num == 0)
                        {
                            break;
                        }
                        output.Write(buffer, 0, num);
                        if (output.Length > maximum)
                        {
                            throw Error("metadata_response_too_large", "Nintendo's response exceeds the permitted size.");
                        }
                    }
                    result = (Bytes: output.ToArray(), MediaType: mediaType);
                }
                return result;
            }
            throw Error("metadata_redirect_refused", "Nintendo's metadata request could not reach an allowed endpoint.");
        }
        catch (OperationCanceledException inner) when (!cancellationToken.IsCancellationRequested)
        {
            throw Error("metadata_timeout", "Nintendo's metadata request timed out. Cached selections remain available.", inner);
        }
        catch (HttpRequestException inner2)
        {
            throw Error("metadata_unavailable", "Nintendo's public catalog could not be reached. Cached selections remain available.", inner2);
        }
    }

    private static string NormalizeQuery(string? query)
    {
        query = Regex.Replace(query?.Trim() ?? "", "\\s+", " ");
        int length = query.Length;
        bool flag = ((length < 2 || length > 160) ? true : false);
        if (flag || query.Contains('\\') || query.Contains("://", StringComparison.Ordinal) || query.StartsWith('/') || Regex.IsMatch(query, "\\b[A-Za-z]:/") || query.Any(char.IsControl))
        {
            throw Error("metadata_query_invalid", "Search by a game name, using 2 to 160 characters; do not submit a local file path or URL.");
        }
        MatchCollection matchCollection = Regex.Matches(query, "[\\p{L}\\p{N}]+");
        length = matchCollection.Count;
        flag = ((length < 1 || length > 24) ? true : false);
        if (flag || matchCollection.Any((Match word) => word.Length > 64))
        {
            throw Error("metadata_query_invalid", "Use a shorter game name for the catalog search.");
        }
        return query;
    }

    private static string? Text(JsonElement item, string property, int maximum)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        string text = value.GetString().Trim();
        if (text.Length <= 0 || text.Length > maximum || text.Any(char.IsControl) || text.Contains('<') || text.Contains('>'))
        {
            return null;
        }
        return text;
    }

    private static Uri OfficialUri(string value, bool artwork)
    {
        if (!Uri.TryCreate(ProductOrigin, value, out Uri result) || !SafeHttps(result) || result.Host != "www.nintendo.com" || !(artwork ? result.AbsolutePath.StartsWith("/eu/media/images/", StringComparison.Ordinal) : result.AbsolutePath.StartsWith("/en-gb/Games/", StringComparison.Ordinal)))
        {
            throw Error("metadata_url_refused", "The catalog URL is outside Nintendo's approved public product or artwork locations.");
        }
        return result;
    }

    private static bool SafeHttps(Uri uri)
    {
        if (uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) && uri.AbsoluteUri.Length <= 4096)
        {
            return !Uri.UnescapeDataString(uri.AbsolutePath).Split('/').Any((string part) => ((part == "." || part == "..") ? true : false) || part.Any(char.IsControl));
        }
        return false;
    }

    private static void ValidateRequestUri(Uri uri, bool artwork)
    {
        bool num;
        if (SafeHttps(uri))
        {
            if (!artwork)
            {
                if (uri.Host == "search.nintendo-europe.com")
                {
                    num = uri.AbsolutePath == "/en/select";
                    goto IL_005b;
                }
            }
            else if (uri.Host == "www.nintendo.com")
            {
                num = uri.AbsolutePath.StartsWith("/eu/media/images/", StringComparison.Ordinal);
                goto IL_005b;
            }
        }
        goto IL_005d;
    IL_005d:
        throw Error("metadata_url_refused", "The metadata request or redirect is outside Nintendo's approved HTTPS endpoints.");
    IL_005b:
        if (num)
        {
            return;
        }
        goto IL_005d;
    }

    private static void ValidateGame(NintendoCatalogGame game)
    {
        if ((object)game == null || game.Source != "Nintendo Europe public catalog" || string.IsNullOrWhiteSpace(game.DisplayTitle) || !Bounded(game.DisplayTitle, 200) || !Bounded(game.Publisher, 160) || !Bounded(game.ReleaseDateText, 64) || !HexHash(game.CatalogResponseSha256))
        {
            throw Error("metadata_selection_invalid", "The catalog selection is incomplete or invalid.");
        }
        Uri uri = OfficialUri(game.ProductUrl, artwork: false);
        if (game.CatalogKey != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))))
        {
            throw Error("metadata_selection_invalid", "The catalog selection does not match its product URL.");
        }
        string squareCoverUrl = game.SquareCoverUrl;
        if (squareCoverUrl != null)
        {
            OfficialUri(squareCoverUrl, artwork: true);
        }
        string landscapeUrl = game.LandscapeUrl;
        if (landscapeUrl != null)
        {
            OfficialUri(landscapeUrl, artwork: true);
        }
        static bool Bounded(string? value, int maximum)
        {
            if (value != null)
            {
                if (value.Length <= maximum && !value.Any(char.IsControl) && !value.Contains('<'))
                {
                    return !value.Contains('>');
                }
                return false;
            }
            return true;
        }
    }

    private static ArtworkInfo InspectArtwork(ReadOnlySpan<byte> bytes, bool background = false)
    {
        int num = (background ? 4096 : 2048);
        if (bytes.Length < 24 || bytes.Length > (background ? 8388608 : 4194304))
        {
            throw Error("metadata_artwork_invalid", "The artwork is empty, truncated, or oversized.");
        }
        int num2 = 0;
        int num3 = 0;
        string mediaType;
        if (bytes.StartsWith(new byte[8] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            mediaType = "image/png";
            int i = 8;
            bool flag = false;
            bool flag2 = false;
            bool flag3 = false;
            int num5;
            for (; i <= bytes.Length - 12; i += num5 + 12)
            {
                uint num4 = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i));
                if (num4 > bytes.Length - i - 12)
                {
                    throw Error("metadata_artwork_invalid", "The PNG artwork contains a truncated chunk.");
                }
                num5 = (int)num4;
                ReadOnlySpan<byte> span = bytes.Slice(i + 4, 4);
                if (Crc32(bytes.Slice(i + 4, num5 + 4)) != BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i + num5 + 8)))
                {
                    throw Error("metadata_artwork_invalid", "The PNG artwork failed its chunk integrity check.");
                }
                if (span.SequenceEqual("IHDR"u8))
                {
                    if (flag || i != 8 || num5 != 13)
                    {
                        throw Error("metadata_artwork_invalid", "The PNG artwork header is invalid.");
                    }
                    uint num6 = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i + 8));
                    uint num7 = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i + 12));
                    if (num6 > num || num7 > num)
                    {
                        throw Error("metadata_artwork_invalid", "The PNG artwork dimensions exceed the supported bounds.");
                    }
                    num2 = (int)num6;
                    num3 = (int)num7;
                    flag = true;
                }
                else if (span.SequenceEqual("IDAT"u8))
                {
                    if (!flag)
                    {
                        throw Error("metadata_artwork_invalid", "The PNG artwork has no header.");
                    }
                    flag2 = true;
                }
                else
                {
                    if (span.SequenceEqual("IEND"u8))
                    {
                        flag3 = ((num5 == 0) & flag & flag2) && i + 12 == bytes.Length;
                        break;
                    }
                    if (span.SequenceEqual("acTL"u8))
                    {
                        throw Error("metadata_artwork_invalid", "Animated images are not supported for cached catalog covers.");
                    }
                }
            }
            if (!flag3)
            {
                throw Error("metadata_artwork_invalid", "The PNG artwork is incomplete.");
            }
            goto IL_0402;
        }
        if (bytes[0] == byte.MaxValue && bytes[1] == 216)
        {
            if (bytes[bytes.Length - 2] == byte.MaxValue)
            {
                if (bytes[bytes.Length - 1] == 217)
                {
                    mediaType = "image/jpeg";
                    int j = 2;
                    bool flag4 = false;
                    int num8;
                    for (; j < bytes.Length - 2; j += num8)
                    {
                        if (bytes[j++] != byte.MaxValue)
                        {
                            throw Error("metadata_artwork_invalid", "The JPEG artwork has an invalid marker.");
                        }
                        for (; j < bytes.Length && bytes[j] == byte.MaxValue; j++)
                        {
                        }
                        if (j >= bytes.Length)
                        {
                            break;
                        }
                        byte b = bytes[j++];
                        bool flag5 = ((b == 0 || (uint)(b - 216) <= 1u) ? true : false);
                        if (flag5 || j > bytes.Length - 2)
                        {
                            break;
                        }
                        num8 = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(j));
                        if (num8 < 2 || num8 > bytes.Length - j)
                        {
                            throw Error("metadata_artwork_invalid", "The JPEG artwork contains a truncated segment.");
                        }
                        if ((uint)(b - 192) <= 2u)
                        {
                            if (num2 != 0 || num8 < 8)
                            {
                                throw Error("metadata_artwork_invalid", "The JPEG artwork frame is invalid.");
                            }
                            num3 = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(j + 3));
                            num2 = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(j + 5));
                        }
                        if (b == 218)
                        {
                            flag4 = num2 != 0;
                            break;
                        }
                    }
                    if (!flag4)
                    {
                        throw Error("metadata_artwork_invalid", "The JPEG artwork has no supported image frame.");
                    }
                    goto IL_0402;
                }
            }
        }
        throw Error("metadata_artwork_invalid", "Only bounded PNG and JPEG square covers are supported.");
    IL_0453:
        throw Error("metadata_artwork_invalid", background ? "Use HD landscape artwork of at least 1280 × 640." : "Nintendo's selected artwork is not a supported square cover.");
    IL_0402:
        if (num2 >= 1 && num2 <= num && num3 >= 1 && num3 <= num)
        {
            bool num9;
            if (!background)
            {
                num9 = num3 != num2;
            }
            else
            {
                if (num2 < 1280 || num3 < 640 || (double)num2 < (double)num3 * 1.5)
                {
                    goto IL_0453;
                }
                num9 = (double)num2 > (double)num3 * 2.5;
            }
            if (!num9)
            {
                return new ArtworkInfo(Convert.ToHexStringLower(SHA256.HashData(bytes)), mediaType, num2, num3, bytes.Length);
            }
        }
        goto IL_0453;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint num = uint.MaxValue;
        ReadOnlySpan<byte> readOnlySpan = bytes;
        for (int i = 0; i < readOnlySpan.Length; i++)
        {
            byte b = readOnlySpan[i];
            num ^= b;
            for (int j = 0; j < 8; j++)
            {
                num = (num >> 1) ^ (uint)(((num & 1) != 0) ? (-306674912) : 0);
            }
        }
        return ~num;
    }

    private static bool HexHash(string? value)
    {
        if (value != null && value.Length == 64)
        {
            return value.All(Uri.IsHexDigit);
        }
        return false;
    }

    private static void ValidateEntryId(string id)
    {
        if (id == null || id.Length != 32 || !id.All(Uri.IsHexDigit))
        {
            throw Error("metadata_entry_invalid", "Select an existing library entry before assigning catalog metadata.");
        }
    }

    private string SelectionPath(string id)
    {
        ValidateEntryId(id);
        return CachePath("selections", id.ToLowerInvariant() + ".json");
    }

    private string ArtworkPath(string hash, string media)
    {
        return CachePath("artwork", hash + ((media == "image/png") ? ".png" : ".jpg"));
    }

    private string CachePath(params string[] parts)
    {
        string root = _root;
        int num = 0;
        string[] array = new string[1 + parts.Length];
        array[num] = root;
        num++;
        ReadOnlySpan<string> readOnlySpan = new ReadOnlySpan<string>(parts);
        readOnlySpan.CopyTo(new Span<string>(array).Slice(num, readOnlySpan.Length));
        num += readOnlySpan.Length;
        string text = Path.Combine(new ReadOnlySpan<string>(array));
        RejectLinks(text);
        return text;
    }

    private static void RejectLinks(string path)
    {
        for (FileSystemInfo fileSystemInfo = (Directory.Exists(path) ? ((FileSystemInfo)new DirectoryInfo(path)) : ((FileSystemInfo)new FileInfo(path))); fileSystemInfo != null; fileSystemInfo = ((fileSystemInfo is DirectoryInfo directoryInfo) ? directoryInfo.Parent : ((FileInfo)fileSystemInfo).Directory))
        {
            if (fileSystemInfo.LinkTarget != null || (fileSystemInfo.Exists && (fileSystemInfo.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw Error("metadata_cache_invalid", "The metadata cache must not traverse filesystem links.");
            }
        }
    }

    private static void WriteAtomic(string destination, byte[] bytes)
    {
        RejectLinks(destination);
        string text = Path.Combine(Path.GetDirectoryName(destination), ".metadata-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fileStream.Write(bytes);
                fileStream.Flush(flushToDisk: true);
            }
            File.Move(text, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(text))
            {
                File.Delete(text);
            }
        }
    }

    private static LibraryMetadataException Error(string code, string message, Exception? inner = null)
    {
        return new LibraryMetadataException(code, message, inner);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _http.Dispose();
            _writes.Dispose();
        }
    }
}
