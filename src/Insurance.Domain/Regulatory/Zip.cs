using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Insurance.Domain.Regulatory;

/// <summary>A ZIP entry as stored: its compressed bytes, CRC and sizes exactly as they were.</summary>
public sealed record ZipEntry(string Name, int Method, uint Crc, int Size, byte[] Compressed, int Time, int Date, int Flags);

/// <summary>
/// A minimal ZIP reader and writer for an .xlsx template (INS-REGULATORY-THREE-REPORT-GROUPS-EXISTING-DATA-20261019): every entry the
/// filling does not touch is written back with its compressed bytes, CRC and sizes exactly as they were; only a replaced entry is
/// compressed again. No ZIP64, no encryption (an Office template needs neither).
/// </summary>
public static class Zip
{
    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xffffffffu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xff] ^ (c >> 8);
        return c ^ 0xffffffffu;
    }

    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));

    /// <summary>The entries of a ZIP archive, in their central directory's order.</summary>
    public static List<ZipEntry> ReadZip(byte[] buf)
    {
        var end = -1;
        for (var i = buf.Length - 22; i >= Math.Max(0, buf.Length - 65_557); i--) if (U32(buf, i) == 0x06054b50) { end = i; break; }
        if (end < 0) throw new InvalidDataException("Not a ZIP archive: no end of central directory");
        var count = U16(buf, end + 10);
        var at = (int)U32(buf, end + 16);
        var entries = new List<ZipEntry>();
        for (var i = 0; i < count; i++)
        {
            if (at + 46 > buf.Length || U32(buf, at) != 0x02014b50) throw new InvalidDataException("Not a ZIP archive: a broken central directory");
            int flags = U16(buf, at + 8), method = U16(buf, at + 10), time = U16(buf, at + 12), date = U16(buf, at + 14);
            uint crc = U32(buf, at + 16);
            int compressedSize = (int)U32(buf, at + 20), size = (int)U32(buf, at + 24);
            int nameLength = U16(buf, at + 28), extraLength = U16(buf, at + 30), commentLength = U16(buf, at + 32);
            var local = (int)U32(buf, at + 42);
            var name = Encoding.UTF8.GetString(buf, at + 46, nameLength);
            if (local + 30 > buf.Length || U32(buf, local) != 0x04034b50) throw new InvalidDataException($"Not a ZIP archive: no local header for {name}");
            var data = local + 30 + U16(buf, local + 26) + U16(buf, local + 28);
            entries.Add(new ZipEntry(name, method, crc, size, buf.AsSpan(data, compressedSize).ToArray(), time, date, flags & 0x0800));
            at += 46 + nameLength + extraLength + commentLength;
        }
        return entries;
    }

    /// <summary>An entry's content.</summary>
    public static byte[] EntryData(ZipEntry e)
    {
        if (e.Method == 0) return e.Compressed;
        if (e.Method == 8)
        {
            using var input = new MemoryStream(e.Compressed);
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflate.CopyTo(output);
            return output.ToArray();
        }
        throw new InvalidDataException($"ZIP entry {e.Name}: compression method {e.Method} not supported");
    }

    /// <summary>The same entry with new content, compressed again (deflate).</summary>
    public static ZipEntry Replaced(ZipEntry e, byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(data);
        return e with { Method = 8, Crc = Crc32(data), Size = data.Length, Compressed = output.ToArray() };
    }

    /// <summary>The archive of these entries, in this order.</summary>
    public static byte[] WriteZip(IReadOnlyList<ZipEntry> entries)
    {
        using var output = new MemoryStream();
        var central = new MemoryStream();
        var offset = 0;
        Span<byte> local = stackalloc byte[30];
        Span<byte> dir = stackalloc byte[46];
        foreach (var e in entries)
        {
            var name = Encoding.UTF8.GetBytes(e.Name);
            local.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(local, 0x04034b50); BinaryPrimitives.WriteUInt16LittleEndian(local[4..], 20);
            BinaryPrimitives.WriteUInt16LittleEndian(local[6..], (ushort)e.Flags); BinaryPrimitives.WriteUInt16LittleEndian(local[8..], (ushort)e.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(local[10..], (ushort)e.Time); BinaryPrimitives.WriteUInt16LittleEndian(local[12..], (ushort)e.Date);
            BinaryPrimitives.WriteUInt32LittleEndian(local[14..], e.Crc); BinaryPrimitives.WriteUInt32LittleEndian(local[18..], (uint)e.Compressed.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(local[22..], (uint)e.Size); BinaryPrimitives.WriteUInt16LittleEndian(local[26..], (ushort)name.Length);
            dir.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(dir, 0x02014b50); BinaryPrimitives.WriteUInt16LittleEndian(dir[4..], 20); BinaryPrimitives.WriteUInt16LittleEndian(dir[6..], 20);
            BinaryPrimitives.WriteUInt16LittleEndian(dir[8..], (ushort)e.Flags); BinaryPrimitives.WriteUInt16LittleEndian(dir[10..], (ushort)e.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(dir[12..], (ushort)e.Time); BinaryPrimitives.WriteUInt16LittleEndian(dir[14..], (ushort)e.Date);
            BinaryPrimitives.WriteUInt32LittleEndian(dir[16..], e.Crc); BinaryPrimitives.WriteUInt32LittleEndian(dir[20..], (uint)e.Compressed.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(dir[24..], (uint)e.Size); BinaryPrimitives.WriteUInt16LittleEndian(dir[28..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(dir[42..], (uint)offset);
            output.Write(local); output.Write(name); output.Write(e.Compressed);
            central.Write(dir); central.Write(name);
            offset += 30 + name.Length + e.Compressed.Length;
        }
        var directory = central.ToArray();
        Span<byte> end = stackalloc byte[22];
        end.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50); BinaryPrimitives.WriteUInt16LittleEndian(end[8..], (ushort)entries.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], (ushort)entries.Count); BinaryPrimitives.WriteUInt32LittleEndian(end[12..], (uint)directory.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], (uint)offset);
        output.Write(directory); output.Write(end);
        return output.ToArray();
    }
}
