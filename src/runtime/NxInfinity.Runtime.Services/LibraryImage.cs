using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NxInfinity.Runtime.Services;

// Container validation and bounds checks only; the frontend still owns image decoding.
internal static class LibraryImage
{
    internal static LibraryArtwork Validate(ReadOnlySpan<byte> bytes, GameLibraryLimits limits)
    {
        if (bytes.Length is < 20 || bytes.Length > limits.MaximumArtworkBytes) throw Invalid("Artwork is empty, truncated, or exceeds its byte limit.");
        int width, height;
        string mediaType;
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            (width, height) = Png(bytes);
            mediaType = "image/png";
        }
        else if (bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            (width, height) = Jpeg(bytes);
            mediaType = "image/jpeg";
        }
        else throw Invalid("Only local PNG and JPEG artwork is supported.");
        if (width is < 1 || height is < 1 || width > limits.MaximumArtworkDimension || height > limits.MaximumArtworkDimension || (long)width * height > 16777216)
            throw Invalid("Artwork dimensions exceed the supported image bounds.");
        return new(Convert.ToHexStringLower(SHA256.HashData(bytes)), mediaType, width, height, bytes.Length);
    }

    private static (int Width, int Height) Png(ReadOnlySpan<byte> bytes)
    {
        var offset = 8;
        var width = 0;
        var height = 0;
        var header = false;
        var data = false;
        while (offset <= bytes.Length - 12)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (size > int.MaxValue || size > bytes.Length - offset - 12) throw Invalid("A PNG chunk is truncated.");
            var length = (int)size;
            var type = bytes.Slice(offset + 4, 4);
            var chunk = bytes.Slice(offset + 8, length);
            var checksum = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + length, 4));
            if (Crc32(bytes.Slice(offset + 4, length + 4)) != checksum) throw Invalid("A PNG chunk checksum is invalid.");
            if (!header && !type.SequenceEqual("IHDR"u8)) throw Invalid("A PNG must begin with its image header.");
            if (type.SequenceEqual("IHDR"u8))
            {
                if (header || length != 13) throw Invalid("The PNG image header is invalid.");
                var w = BinaryPrimitives.ReadUInt32BigEndian(chunk);
                var h = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
                if (w > int.MaxValue || h > int.MaxValue) throw Invalid("PNG dimensions are invalid.");
                width = (int)w; height = (int)h; header = true;
                var validDepth = chunk[9] switch
                {
                    0 => chunk[8] is 1 or 2 or 4 or 8 or 16,
                    2 or 4 or 6 => chunk[8] is 8 or 16,
                    3 => chunk[8] is 1 or 2 or 4 or 8,
                    _ => false
                };
                if (!validDepth || chunk[10] != 0 || chunk[11] != 0 || chunk[12] > 1) throw Invalid("The PNG pixel format is invalid.");
            }
            else if (type.SequenceEqual("IDAT"u8)) data |= length > 0;
            else if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0 || !data || offset + 12 != bytes.Length) throw Invalid("The PNG end marker or pixel payload is invalid.");
                return (width, height);
            }
            else if (type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8))
                throw Invalid("Animated artwork is not supported.");
            else if ((type[0] & 0x20) == 0 && !type.SequenceEqual("PLTE"u8)) throw Invalid("The PNG contains an unsupported critical chunk.");
            offset += length + 12;
        }
        throw Invalid("The PNG is missing its end marker.");
    }

    private static (int Width, int Height) Jpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes[^2] != 0xff || bytes[^1] != 0xd9) throw Invalid("The JPEG is missing its end marker.");
        var offset = 2;
        var width = 0;
        var height = 0;
        while (offset < bytes.Length - 2)
        {
            if (bytes[offset++] != 0xff) throw Invalid("The JPEG segment marker is invalid.");
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) throw Invalid("The JPEG marker is truncated.");
            var marker = bytes[offset++];
            if (marker is 0 or 0xd8 or 0xd9 || marker is >= 0xd0 and <= 0xd7) throw Invalid("An unexpected JPEG segment marker was found.");
            if (marker == 0x01) continue;
            if (offset > bytes.Length - 2) throw Invalid("The JPEG segment is truncated.");
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (length < 2 || length > bytes.Length - offset) throw Invalid("The JPEG segment length is invalid.");
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (width != 0 || length < 8 || bytes[offset + 2] != 8) throw Invalid("The JPEG frame header is invalid or unsupported.");
                height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]);
                var components = bytes[offset + 7];
                if (components is not (1 or 3 or 4) || length != 8 + 3 * components) throw Invalid("The JPEG component header is invalid.");
            }
            else if (marker == 0xda)
            {
                if (width == 0 || height == 0 || offset + length >= bytes.Length - 2) throw Invalid("The JPEG pixel scan is missing or empty.");
                return (width, height);
            }
            else if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
                throw Invalid("This JPEG coding mode is not supported.");
            offset += length;
        }
        throw Invalid("The JPEG is missing its image frame or pixel scan.");
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320U);
        }
        return ~crc;
    }

    private static ServiceException Invalid(string text) => new(ServiceError.InvalidInput, text);
}
