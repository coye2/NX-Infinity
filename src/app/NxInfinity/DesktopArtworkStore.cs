using System;
using System.IO;
using SkiaSharp;

namespace NxInfinity;

public sealed class DesktopArtworkStore(string dataRoot)
{
    private readonly string _root = Path.Combine(dataRoot, "frontend-metadata", "backgrounds");

    private string FilePath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out var _))
        {
            throw new ArgumentException("Invalid library identity.");
        }
        return Path.Combine(_root, id + ".image");
    }

    public byte[]? Read(string id)
    {
        FileInfo fileInfo = new FileInfo(FilePath(id));
        if (!fileInfo.Exists)
        {
            return null;
        }
        if (fileInfo.LinkTarget != null || fileInfo.Length > 8388608)
        {
            throw new IOException("Background artwork is not a supported local image.");
        }
        byte[] array = File.ReadAllBytes(fileInfo.FullName);
        Validate(array);
        return array;
    }

    public void Write(string id, byte[] bytes)
    {
        Validate(bytes);
        Directory.CreateDirectory(_root);
        string text = FilePath(id);
        if (new DirectoryInfo(_root).LinkTarget != null || new FileInfo(text).LinkTarget != null)
        {
            throw new IOException("Background artwork must use regular local files.");
        }
        string text2 = text + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(text2, bytes);
            File.Move(text2, text, overwrite: true);
        }
        finally
        {
            if (File.Exists(text2))
            {
                File.Delete(text2);
            }
        }
    }

    private static void Validate(byte[] bytes)
    {
        int num = bytes.Length;
        if ((num < 32 || num > 8388608) ? true : false)
        {
            throw new InvalidDataException("Use an image smaller than 8 MB.");
        }
        using SKData data = SKData.CreateCopy(bytes);
        using SKCodec sKCodec = SKCodec.Create(data);
        bool flag = sKCodec == null;
        if (!flag)
        {
            num = sKCodec.Info.Width;
            bool flag2 = ((num < 1 || num > 8192) ? true : false);
            flag = flag2;
        }
        bool flag3 = flag;
        if (!flag3)
        {
            num = sKCodec.Info.Height;
            bool flag2 = ((num < 1 || num > 8192) ? true : false);
            flag3 = flag2;
        }
        if (flag3 || (long)sKCodec.Info.Width * (long)sKCodec.Info.Height > 33554432)
        {
            throw new InvalidDataException("Choose a PNG, JPEG or WebP image up to 8192 pixels and 32 megapixels.");
        }
        if (sKCodec.Info.Width < 1280 || sKCodec.Info.Height < 640 || (double)sKCodec.Info.Width < (double)sKCodec.Info.Height * 1.5 || (double)sKCodec.Info.Width > (double)sKCodec.Info.Height * 2.5)
        {
            throw new InvalidDataException("Choose HD landscape artwork of at least 1280 × 640. Covers belong on game tiles.");
        }
    }
}
