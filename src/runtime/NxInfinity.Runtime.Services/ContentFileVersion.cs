using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NxInfinity.Runtime.Services;

// A timestamp alone cannot validate a game. Require the file ID, volume ID,
// NTFS/ReFS change journal sequence and change/write times from the open handle.
// Unsupported filesystems, remote shares and disabled journals use SHA-256.
internal static class ContentFileVersion
{
    internal static string? Read(FileStream stream)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = stream.SafeFileHandle;
        byte[] id = new byte[24], basic = new byte[40], record = new byte[4096];
        if (!GetFileInformationByHandleEx(handle, 18, id, (uint)id.Length) ||
            !GetFileInformationByHandleEx(handle, 0, basic, (uint)basic.Length) ||
            !DeviceIoControl(handle, 0x000900eb, IntPtr.Zero, 0, record, (uint)record.Length, out uint read, IntPtr.Zero)) return null;
        long? usn = ParseUsn(record.AsSpan(0, (int)Math.Min(read, (uint)record.Length)));
        long changed = BinaryPrimitives.ReadInt64LittleEndian(basic.AsSpan(24));
        long written = BinaryPrimitives.ReadInt64LittleEndian(basic.AsSpan(16));
        uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(basic.AsSpan(32));
        if (usn is null || changed <= 0 || written <= 0 || (attributes & 0x410) != 0) return null;
        return $"win-usn-v1:{Convert.ToHexString(id)}:{usn}:{changed}:{written}:{stream.Length}";
    }

    internal static long? ParseUsn(ReadOnlySpan<byte> record)
    {
        if (record.Length < 8) return null;
        int version = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int offset = version switch { 2 => 24, 3 => 40, _ => -1 };
        int minimum = version == 2 ? 60 : 76;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(record);
        if (offset < 0 || length < minimum || length > record.Length) return null;
        long usn = BinaryPrimitives.ReadInt64LittleEndian(record[offset..]);
        return usn > 0 ? usn : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        [Out] byte[] information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, uint inputSize,
        [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
