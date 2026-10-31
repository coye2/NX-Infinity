using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NxInfinity.Runtime;
using NxInfinity.Runtime.Services;

namespace NxInfinity;

internal static class LocalArtwork
{
    internal sealed record Source(string EntryId, LibraryArtwork Artwork, bool Catalog = false);

    internal static string? ApplicationTitle(GameLibraryEntry entry)
    {
        if (entry.Kind == LibraryContentKind.DownloadableContent)
        {
            return null;
        }
        string[] array = (from o in entry.Observations
                          where o.ContentSha256 == entry.Content.Sha256
                          select InfinitySession.KnownHex(o.TitleId, 16) into t
                          where t != null
                          select t).Distinct().ToArray();
        if (array.Length != 1)
        {
            return null;
        }
        ulong num = ulong.Parse(array[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (entry.Kind == LibraryContentKind.Update)
        {
            if ((num & 0xFFF) != 2048)
            {
                return null;
            }
            num -= 2048;
        }
        return num.ToString("X16", CultureInfo.InvariantCulture);
    }

    internal static IReadOnlyList<Source> Sources(GameLibraryEntry entry, NintendoCatalogSelection? catalog, IReadOnlyList<GameLibraryEntry> library)
    {
        List<Source> list = new List<Source>();
        LibraryArtwork artwork = entry.Artwork;
        if ((object)artwork != null)
        {
            list.Add(new Source(entry.EntryId, artwork));
        }
        if ((object)catalog != null)
        {
            string artworkSha = catalog.ArtworkSha256;
            if (artworkSha != null)
            {
                string artworkMediaType = catalog.ArtworkMediaType;
                if (artworkMediaType != null)
                {
                    int? artworkWidth = catalog.ArtworkWidth;
                    if (artworkWidth.HasValue)
                    {
                        int valueOrDefault = artworkWidth.GetValueOrDefault();
                        int? artworkHeight = catalog.ArtworkHeight;
                        if (artworkHeight.HasValue)
                        {
                            int valueOrDefault2 = artworkHeight.GetValueOrDefault();
                            int? artworkBytes = catalog.ArtworkBytes;
                            if (artworkBytes.HasValue)
                            {
                                int valueOrDefault3 = artworkBytes.GetValueOrDefault();
                                list.Add(new Source(entry.EntryId, new LibraryArtwork(artworkSha, artworkMediaType, valueOrDefault, valueOrDefault2, valueOrDefault3), Catalog: true));
                            }
                        }
                    }
                }
            }
        }
        string title = ApplicationTitle(entry);
        if (title != null)
        {
            foreach (GameLibraryEntry item in from e in library
                                              where e.EntryId != entry.EntryId && (object)e.Artwork != null && ApplicationTitle(e) == title
                                              orderby (e.Kind != LibraryContentKind.Update) ? 1 : 0, e.UpdatedUtc descending
                                              select e)
            {
                list.Add(new Source(item.EntryId, item.Artwork));
            }
        }
        LibraryArtwork embedded = entry.Artwork;
        if ((object)embedded != null && embedded.Width <= 256 && embedded.Height <= 256)
        {
            Source source = list.FirstOrDefault((Source s) => s.Catalog && Math.Min(s.Artwork.Width, s.Artwork.Height) > Math.Min(embedded.Width, embedded.Height));
            if ((object)source != null)
            {
                list.Remove(source);
                list.Insert(0, source);
            }
        }
        return list.DistinctBy((Source s) => (EntryId: s.EntryId, Sha256: s.Artwork.Sha256, Catalog: s.Catalog)).ToArray();
    }

    internal static byte[]? Read(Source source, GameLibraryStore library, NintendoLibraryMetadata metadata)
    {
        if (source.Catalog)
        {
            if (metadata.GetSelection(source.EntryId)?.ArtworkSha256 != source.Artwork.Sha256)
            {
                return null;
            }
            return metadata.GetCachedArtwork(source.EntryId);
        }
        return library.ReadArtwork(source.Artwork).Bytes;
    }

    internal static bool TryStoreNative(GameLibraryStore library, GameLibraryEntry entry, ContentInfo content)
    {
        if ((object)content.Artwork == null || content.Issues.Any((ContentIssue i) => i.Severity == "error"))
        {
            return false;
        }
        GameLibraryEntry gameLibraryEntry = library.Get(entry.EntryId);
        if (gameLibraryEntry.Content.Sha256 != entry.Content.Sha256)
        {
            return false;
        }
        string text = InfinitySession.TitleOf(gameLibraryEntry);
        string text2 = InfinitySession.KnownHex(content.TitleId, 16);
        if (text != null && text2 != text)
        {
            return false;
        }
        try
        {
            byte[] array = content.Artwork.Decode();
            LibraryArtwork artwork = gameLibraryEntry.Artwork;
            if ((object)artwork != null)
            {
                if (Convert.ToHexStringLower(SHA256.HashData(array)) != artwork.Sha256)
                {
                    return false;
                }
                try
                {
                    library.ReadArtwork(artwork);
                    return false;
                }
                catch (ServiceException ex) when (ex.Code == ServiceError.CorruptData)
                {
                }
            }
            library.SetArtwork(entry.EntryId, array);
            return true;
        }
        catch (Exception ex2) when ((ex2 is NxException || ex2 is ServiceException || ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
        {
            return false;
        }
    }

    internal static bool NeedsNativeArtwork(GameLibraryStore library, GameLibraryEntry entry)
    {
        if ((object)entry.Artwork == null)
        {
            return true;
        }
        try
        {
            library.ReadArtwork(entry.Artwork);
            return false;
        }
        catch (ServiceException ex) when (ex.Code == ServiceError.CorruptData)
        {
            return true;
        }
        catch (Exception ex2) when ((ex2 is ServiceException || ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
        {
            return false;
        }
    }
}
