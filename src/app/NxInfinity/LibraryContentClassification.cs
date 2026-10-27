using System;
using System.IO;
using System.Linq;
using NxInfinity.Runtime;
using NxInfinity.Runtime.Services;

namespace NxInfinity;

public static class LibraryContentClassification
{
    public static bool IsGame(GameLibraryEntry entry)
    {
        if (entry.Kind != LibraryContentKind.Game)
        {
            return false;
        }
        if (Path.GetExtension(entry.Content.AbsolutePath).Equals(".nca", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (BeforeInspection(entry.Content.AbsolutePath) == LibraryContentKind.Game)
        {
            return true;
        }
        return entry.Observations.Any((LibraryCoreObservation o) =>
        {
            bool flag = o.ContentSha256 == entry.Content.Sha256;
            if (flag)
            {
                bool flag2;
                switch (o.ContentKind)
                {
                    case "application":
                    case "game":
                    case "homebrew":
                        flag2 = true;
                        break;
                    default:
                        flag2 = false;
                        break;
                }
                flag = flag2 || o.LoaderAccepted;
            }
            return flag;
        });
    }

    public static bool IsAvailableGame(GameLibraryEntry entry)
    {
        if (IsGame(entry))
        {
            return File.Exists(entry.Content.AbsolutePath);
        }
        return false;
    }

    public static LibraryContentKind BeforeInspection(string path)
    {
        string text = Path.GetExtension(path).ToLowerInvariant();
        if ((!(text == ".nro") && !(text == ".nso")) || 1 == 0)
        {
            return LibraryContentKind.Unidentified;
        }
        return LibraryContentKind.Game;
    }

    public static LibraryContentKind FromNative(ContentInfo metadata)
    {
        if (metadata.Issues.Any((ContentIssue i) => i.Severity == "error"))
        {
            return LibraryContentKind.Unidentified;
        }
        LibraryContentKind result;
        switch (metadata.Kind?.ToLowerInvariant())
        {
            case "application":
            case "game":
            case "homebrew":
                result = LibraryContentKind.Game;
                break;
            case "update":
            case "patch":
                result = LibraryContentKind.Update;
                break;
            case "dlc":
            case "add_on_content":
                result = LibraryContentKind.DownloadableContent;
                break;
            default:
                result = LibraryContentKind.Unidentified;
                break;
        }
        return result;
    }
}
