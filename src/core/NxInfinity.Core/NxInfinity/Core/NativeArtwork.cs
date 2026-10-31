using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace NxInfinity.Core;

internal static class NativeArtwork
{
    internal sealed record Image(string MimeType, string DataBase64);

    internal const int MaximumBytes = 262144;

    internal static Image Select(Func<IEnumerable<Func<byte[]>>> preferred, Func<IEnumerable<Func<byte[]>>> fallback = null)
    {
        return ReadCandidates(preferred) ?? ReadCandidates(fallback);
    }

    private static Image ReadCandidates(Func<IEnumerable<Func<byte[]>>> source)
    {
        if (source == null)
        {
            return null;
        }
        try
        {
            foreach (Func<byte[]> item in source().Take(64))
            {
                try
                {
                    Image image = Decode(item());
                    if ((object)image != null)
                    {
                        return image;
                    }
                }
                catch (Exception error) when (OptionalFailure(error))
                {
                }
            }
        }
        catch (Exception error2) when (OptionalFailure(error2))
        {
        }
        return null;
    }

    internal static bool OptionalFailure(Exception error)
    {
        if (!(error is OutOfMemoryException) && !(error is StackOverflowException))
        {
            return !(error is AccessViolationException);
        }
        return false;
    }

    internal static Image Decode(byte[] original)
    {
        bool flag = original == null;
        if (!flag)
        {
            int num = original.Length;
            bool flag2 = ((num < 20 || num > 262144) ? true : false);
            flag = flag2;
        }
        if (flag)
        {
            return null;
        }
        int num2 = original.Length;
        while (num2 > 0 && original[num2 - 1] == 0)
        {
            num2--;
        }
        Span<byte> span = original.AsSpan(0, num2);
        try
        {
            int item;
            int item2;
            string mimeType;
            if (span.StartsWith(new byte[8] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                (int Width, int Height) tuple = Png(span);
                item = tuple.Width;
                item2 = tuple.Height;
                mimeType = "image/png";
            }
            else
            {
                if (span.Length < 4 || span[0] != byte.MaxValue || span[1] != 216)
                {
                    return null;
                }
                (int Width, int Height) tuple2 = Jpeg(span);
                item = tuple2.Width;
                item2 = tuple2.Height;
                mimeType = "image/jpeg";
            }
            flag = ((item < 1 || item > 8192) ? true : false);
            bool flag2 = flag;
            if (!flag2)
            {
                bool flag3 = ((item2 < 1 || item2 > 8192) ? true : false);
                flag2 = flag3;
            }
            if (flag2 || (long)item * (long)item2 > 16777216)
            {
                return null;
            }
            byte[] array = span.ToArray();
            using MemoryStream stream = new MemoryStream(array, writable: false);
            using SKCodec sKCodec = SKCodec.Create(stream);
            if (sKCodec == null || sKCodec.FrameCount > 1 || sKCodec.Info.Width != item || sKCodec.Info.Height != item2)
            {
                return null;
            }
            using SKBitmap sKBitmap = new SKBitmap(item, item2, SKColorType.Rgba8888, SKAlphaType.Premul);
            if (sKBitmap.GetPixels() == IntPtr.Zero || sKCodec.GetPixels(sKBitmap.Info, sKBitmap.GetPixels()) != SKCodecResult.Success)
            {
                return null;
            }
            return new Image(mimeType, Convert.ToBase64String(array));
        }
        catch (Exception error) when (OptionalFailure(error))
        {
            return null;
        }
    }

    private static (int Width, int Height) Png(ReadOnlySpan<byte> bytes)
    {
        int i = 8;
        int item = 0;
        int item2 = 0;
        bool flag = false;
        bool flag2 = false;
        int num2;
        for (; i <= bytes.Length - 12; i += num2 + 12)
        {
            uint num = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i));
            if (num > int.MaxValue || num > bytes.Length - i - 12)
            {
                throw Invalid("A PNG chunk is truncated.");
            }
            num2 = (int)num;
            ReadOnlySpan<byte> span = bytes.Slice(i + 4, 4);
            ReadOnlySpan<byte> source = bytes.Slice(i + 8, num2);
            uint num3 = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i + 8 + num2, 4));
            if (Crc32(bytes.Slice(i + 4, num2 + 4)) != num3)
            {
                throw Invalid("A PNG chunk checksum is invalid.");
            }
            if (!flag && !span.SequenceEqual("IHDR"u8))
            {
                throw Invalid("A PNG must begin with its image header.");
            }
            if (span.SequenceEqual("IHDR"u8))
            {
                if (flag || num2 != 13)
                {
                    throw Invalid("The PNG image header is invalid.");
                }
                uint num4 = BinaryPrimitives.ReadUInt32BigEndian(source);
                uint num5 = BinaryPrimitives.ReadUInt32BigEndian(source.Slice(4));
                if (num4 > int.MaxValue || num5 > int.MaxValue)
                {
                    throw Invalid("PNG dimensions are invalid.");
                }
                item = (int)num4;
                item2 = (int)num5;
                flag = true;
                bool flag3;
                switch (source[9])
                {
                    case 0:
                        {
                            bool flag4;
                            switch (source[8])
                            {
                                case 1:
                                case 2:
                                case 4:
                                case 8:
                                case 16:
                                    flag4 = true;
                                    break;
                                default:
                                    flag4 = false;
                                    break;
                            }
                            flag3 = flag4;
                            break;
                        }
                    case 2:
                    case 4:
                    case 6:
                        {
                            byte b = source[8];
                            bool flag4 = ((b == 8 || b == 16) ? true : false);
                            flag3 = flag4;
                            break;
                        }
                    case 3:
                        {
                            byte b = source[8];
                            bool flag4 = (((uint)(b - 1) <= 1u || b == 4 || b == 8) ? true : false);
                            flag3 = flag4;
                            break;
                        }
                    default:
                        flag3 = false;
                        break;
                }
                if (!flag3 || source[10] != 0 || source[11] != 0 || source[12] > 1)
                {
                    throw Invalid("The PNG pixel format is invalid.");
                }
                continue;
            }
            if (span.SequenceEqual("IDAT"u8))
            {
                flag2 |= num2 > 0;
                continue;
            }
            if (span.SequenceEqual("IEND"u8))
            {
                if (num2 != 0 || !flag2 || i + 12 != bytes.Length)
                {
                    throw Invalid("The PNG end marker or pixel payload is invalid.");
                }
                return (Width: item, Height: item2);
            }
            if (span.SequenceEqual("acTL"u8) || span.SequenceEqual("fcTL"u8) || span.SequenceEqual("fdAT"u8))
            {
                throw Invalid("Animated artwork is not supported.");
            }
            if ((span[0] & 0x20) == 0 && !span.SequenceEqual("PLTE"u8))
            {
                throw Invalid("The PNG contains an unsupported critical chunk.");
            }
        }
        throw Invalid("The PNG is missing its end marker.");
    }

    private static (int Width, int Height) Jpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes[bytes.Length - 2] == byte.MaxValue)
        {
            if (bytes[bytes.Length - 1] == 217)
            {
                int i = 2;
                int num = 0;
                int num2 = 0;
                while (i < bytes.Length - 2)
                {
                    if (bytes[i++] != byte.MaxValue)
                    {
                        throw Invalid("The JPEG segment marker is invalid.");
                    }
                    for (; i < bytes.Length && bytes[i] == byte.MaxValue; i++)
                    {
                    }
                    if (i >= bytes.Length)
                    {
                        throw Invalid("The JPEG marker is truncated.");
                    }
                    byte b = bytes[i++];
                    bool flag = ((b == 0 || (uint)(b - 216) <= 1u) ? true : false);
                    if (flag || (b >= 208 && b <= 215))
                    {
                        throw Invalid("An unexpected JPEG segment marker was found.");
                    }
                    if (b == 1)
                    {
                        continue;
                    }
                    if (i > bytes.Length - 2)
                    {
                        throw Invalid("The JPEG segment is truncated.");
                    }
                    ushort num3 = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i));
                    if (num3 < 2 || num3 > bytes.Length - i)
                    {
                        throw Invalid("The JPEG segment length is invalid.");
                    }
                    if ((uint)(b - 192) <= 2u)
                    {
                        if (num != 0 || num3 < 8 || bytes[i + 2] != 8)
                        {
                            throw Invalid("The JPEG frame header is invalid or unsupported.");
                        }
                        num2 = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i + 3));
                        num = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i + 5));
                        byte b2 = bytes[i + 7];
                        flag = ((b2 == 1 || (uint)(b2 - 3) <= 1u) ? true : false);
                        if (!flag || num3 != 8 + 3 * b2)
                        {
                            throw Invalid("The JPEG component header is invalid.");
                        }
                    }
                    else
                    {
                        if (b == 218)
                        {
                            if (num == 0 || num2 == 0 || i + num3 >= bytes.Length - 2)
                            {
                                throw Invalid("The JPEG pixel scan is missing or empty.");
                            }
                            return (Width: num, Height: num2);
                        }
                        flag = b >= 192 && b <= 207;
                        if (flag)
                        {
                            bool flag2 = ((b == 196 || b == 200 || b == 204) ? true : false);
                            flag = !flag2;
                        }
                        if (flag)
                        {
                            throw Invalid("This JPEG coding mode is not supported.");
                        }
                    }
                    i += num3;
                }
                throw Invalid("The JPEG is missing its image frame or pixel scan.");
            }
        }
        throw Invalid("The JPEG is missing its end marker.");
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

    private static InvalidDataException Invalid(string text)
    {
        return new InvalidDataException(text);
    }
}
