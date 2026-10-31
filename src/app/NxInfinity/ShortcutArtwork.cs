using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace NxInfinity;

public static class ShortcutArtwork
{
    public static string? Write(string dataRoot, byte[] bytes, bool windows)
    {
        if (bytes.Length > 8388608)
        {
            return null;
        }
        using SKData data = SKData.CreateCopy(bytes);
        using SKCodec sKCodec = SKCodec.Create(data);
        if (sKCodec == null || sKCodec.Info.Width < 1 || sKCodec.Info.Height < 1 || (long)sKCodec.Info.Width * (long)sKCodec.Info.Height > 16777216)
        {
            return null;
        }
        SKSizeI scaledDimensions = sKCodec.GetScaledDimensions(Math.Min(1f, 256f / (float)Math.Max(sKCodec.Info.Width, sKCodec.Info.Height)));
        using SKBitmap sKBitmap = SKBitmap.Decode(sKCodec, new SKImageInfo(scaledDimensions.Width, scaledDimensions.Height));
        if (sKBitmap == null)
        {
            return null;
        }
        using SKSurface sKSurface = SKSurface.Create(new SKImageInfo(256, 256));
        float num = Math.Min(sKBitmap.Width, sKBitmap.Height);
        float num2 = ((float)sKBitmap.Width - num) / 2f;
        float num3 = ((float)sKBitmap.Height - num) / 2f;
        sKSurface.Canvas.DrawBitmap(sKBitmap, new SKRect(num2, num3, num2 + num, num3 + num), new SKRect(0f, 0f, 256f, 256f));
        using SKImage sKImage = sKSurface.Snapshot();
        using SKData sKData = sKImage.Encode(SKEncodedImageFormat.Png, 100);
        using MemoryStream memoryStream = new MemoryStream();
        if (windows)
        {
            using BinaryWriter binaryWriter = new BinaryWriter(memoryStream, Encoding.UTF8, leaveOpen: true);
            binaryWriter.Write((ushort)0);
            binaryWriter.Write((ushort)1);
            binaryWriter.Write((ushort)1);
            binaryWriter.Write((byte)0);
            binaryWriter.Write((byte)0);
            binaryWriter.Write((byte)0);
            binaryWriter.Write((byte)0);
            binaryWriter.Write((ushort)1);
            binaryWriter.Write((ushort)32);
            binaryWriter.Write((uint)sKData.Size);
            binaryWriter.Write(22u);
        }
        sKData.SaveTo(memoryStream);
        string text = Path.Combine(dataRoot, "frontend-shortcut-art");
        Directory.CreateDirectory(text);
        if (new DirectoryInfo(text).LinkTarget != null)
        {
            throw new IOException("Shortcut artwork requires a regular directory.");
        }
        string text2 = Path.Combine(text, Convert.ToHexStringLower(SHA256.HashData(bytes)) + (windows ? ".ico" : ".png"));
        if (new FileInfo(text2).LinkTarget != null)
        {
            throw new IOException("Shortcut artwork requires a regular file.");
        }
        byte[] array = memoryStream.ToArray();
        if (File.Exists(text2) && File.ReadAllBytes(text2).SequenceEqual(array))
        {
            return text2;
        }
        string text3 = text2 + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(text3, array);
            File.Move(text3, text2, overwrite: true);
        }
        finally
        {
            if (File.Exists(text3))
            {
                File.Delete(text3);
            }
        }
        return text2;
    }
}
