// FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
// Derived from ZuTM's Fat32Image. Minimal FAT32 disk-image writer: MBR + one
// FAT32 partition + long-file-name directory entries, files laid out as
// single extents. Turns a Windows ISO's extracted tree into a bootable USB
// disk image so the installer never depends on El Torito/UDF/ATAPI — the
// layers that wedge on QEMU/Windows + EDK2. Pure file I/O, no admin rights.

using System.Text;

namespace FreshWin;

/// <summary>Builds a raw disk image containing one FAT32 volume from a staging directory.</summary>
public static class Fat32Image
{
    private const int BytesPerSector = 512;
    private const int ReservedSectors = 32;
    private const int NumberOfFats = 2;
    private const long PartitionStartLba = 2048; // 1 MiB alignment
    private const uint EndOfChain = 0x0FFFFFFF;
    private const long MinimumClusters = 65525 + 1024; // FAT32 spec floor + slack

    public sealed class Options
    {
        /// <summary>Volume label (≤11 chars).</summary>
        public string VolumeLabel { get; init; } = "FRESHWIN USB";

        public Action<string>? Log { get; init; }
    }

    /// <summary>Writes <paramref name="imagePath"/> containing the tree under <paramref name="contentRoot"/>.</summary>
    public static void Write(string imagePath, string contentRoot, Options? options = null)
    {
        options ??= new Options();
        var contentBytes = EstimateContentBytes(contentRoot);
        // 32 KiB clusters for the multi-GiB install media; 4 KiB keeps small
        // diagnostics above FAT32's minimum cluster count.
        var bytesPerCluster = contentBytes > 1024L * 1024 * 1024 ? 32 * 1024 : 4 * 1024;
        var sectorsPerCluster = bytesPerCluster / BytesPerSector;
        var minCapacity = PartitionStartLba * BytesPerSector + (MinimumClusters + ReservedSectors + 64) * bytesPerCluster;
        var capacityBytes = Math.Max(minCapacity, PartitionStartLba * BytesPerSector + contentBytes + 64L * 1024 * 1024);
        var totalSectors = capacityBytes / BytesPerSector;
        var partitionSectors = totalSectors - PartitionStartLba;

        // Fixed point: cluster count ↔ FAT size (FAT32 entries are 4 bytes).
        long fatSectors = 1;
        long clusterCount;
        while (true)
        {
            clusterCount = (partitionSectors - ReservedSectors - NumberOfFats * fatSectors) / sectorsPerCluster;
            var needed = (clusterCount + 2) * 4 / BytesPerSector + 1;
            if (needed <= fatSectors)
            {
                break;
            }

            fatSectors = needed;
        }

        if (clusterCount > 0xFFFF_FFF0)
        {
            throw new InvalidOperationException($"FAT32 geometry out of range: {clusterCount} clusters.");
        }

        var clusterAreaStart = PartitionStartLba + ReservedSectors + NumberOfFats * fatSectors;
        options.Log?.Invoke(
            $"FAT32: {totalSectors} sectors, {clusterCount} clusters of {bytesPerCluster / 1024} KiB, FAT {fatSectors} sectors");

        using var image = new FileStream(imagePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024);
        image.SetLength(totalSectors * BytesPerSector);

        // --- MBR: one partition, type 0x0C (FAT32 LBA). ---
        var mbr = new byte[BytesPerSector];
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        var entry = new byte[16];
        entry[1] = 0x20; entry[2] = 0x21; entry[3] = 0x00;   // CHS begin (unused with LBA)
        entry[4] = 0x0C;                                      // FAT32 LBA
        entry[5] = 0xFE; entry[6] = 0xFF; entry[7] = 0xFF;    // CHS end
        WriteUInt32(entry, 8, (uint)PartitionStartLba);
        WriteUInt32(entry, 12, (uint)partitionSectors);
        Array.Copy(entry, 0, mbr, 446, 16);
        WriteSectors(image, 0, mbr);

        // --- FAT32 boot sector + FSInfo ---
        var boot = new byte[BytesPerSector];
        boot[0] = 0xEB; boot[1] = 0x58; boot[2] = 0x90;       // jmp + nop
        Array.Copy(Encoding.ASCII.GetBytes("MSDOS5.0"), 0, boot, 3, 8);
        WriteUInt16(boot, 11, BytesPerSector);
        boot[13] = (byte)sectorsPerCluster;
        WriteUInt16(boot, 14, ReservedSectors);
        boot[16] = NumberOfFats;
        boot[21] = 0xF8;                                      // media: fixed disk
        WriteUInt16(boot, 24, 63);                            // sectors per track
        WriteUInt16(boot, 26, 255);                           // heads
        WriteUInt32(boot, 28, 32);                            // hidden sectors (pre-partition, fictional)
        WriteUInt32(boot, 32, (uint)partitionSectors);
        WriteUInt32(boot, 36, (uint)fatSectors);
        WriteUInt16(boot, 44, 2);                             // root cluster
        WriteUInt16(boot, 48, 1);                             // FSInfo sector
        WriteUInt16(boot, 50, 6);                             // backup boot sector
        // FAT32 extended boot record starts at offset 64 (36-61 are reserved).
        boot[64] = 0x80;                                      // BIOS drive number
        boot[66] = 0x29;                                      // extended boot signature
        WriteUInt32(boot, 67, 0x52455348);                    // volume id ("RESH")
        var label = options.VolumeLabel.PadRight(11)[..11].ToUpperInvariant().ToCharArray();
        for (var i = 0; i < 11; i++)
        {
            boot[71 + i] = (byte)label[i];
        }

        Array.Copy(Encoding.ASCII.GetBytes("FAT32   "), 0, boot, 82, 8);
        boot[510] = 0x55;
        boot[511] = 0xAA;
        WriteSectors(image, PartitionStartLba, boot);
        WriteSectors(image, PartitionStartLba + 6, boot);     // backup

        var fsInfo = new byte[BytesPerSector];
        WriteUInt32(fsInfo, 0, 0x41615252);
        WriteUInt32(fsInfo, 484, 0x61417272);
        WriteUInt32(fsInfo, 488, 0xFFFFFFFF);                 // free cluster count unknown
        WriteUInt32(fsInfo, 492, 0xFFFFFFFF);                 // next free hint unknown
        WriteSectors(image, PartitionStartLba + 1, fsInfo);
        WriteSectors(image, PartitionStartLba + 7, fsInfo);

        // --- File system state ---
        var fat = new uint[(int)(fatSectors * BytesPerSector / 4)];
        fat[0] = 0x0FFFFFF8;
        fat[1] = 0xFFFFFFFF;
        var nextCluster = 2u;
        var rootBlock = new DirBlock { FirstCluster = nextCluster };
        nextCluster = AllocateChain(fat, nextCluster, DirectorySectorsNeeded(contentRoot), sectorsPerCluster);

        WriteTree(image, contentRoot, rootBlock, 0, fat, ref nextCluster, clusterAreaStart, sectorsPerCluster, options);
        WriteDirectoryData(image, rootBlock, clusterAreaStart, fat, sectorsPerCluster);

        // --- Flush FATs ---
        var fatBytes = new byte[fatSectors * BytesPerSector];
        for (var i = 0; i < fat.Length; i++)
        {
            WriteUInt32(fatBytes, i * 4, fat[i]);
        }

        for (var i = 0; i < NumberOfFats; i++)
        {
            WriteSectors(image, PartitionStartLba + ReservedSectors + i * fatSectors, fatBytes);
        }
    }

    private sealed class DirBlock
    {
        public uint FirstCluster;
        public List<byte> Data = [];
    }

    private static long EstimateContentBytes(string root)
    {
        // 32 KiB clusters bound the slack estimate for any cluster size we pick.
        const int estimateBytesPerCluster = 32 * 1024;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            total += (new FileInfo(file).Length + estimateBytesPerCluster - 1)
                / estimateBytesPerCluster * estimateBytesPerCluster;
        }

        total += 32L * 1024 * 1024; // directory clusters, slack
        return total;
    }

    private static void WriteTree(
        FileStream image,
        string directoryPath,
        DirBlock directory,
        uint parentFirstCluster,
        uint[] fat,
        ref uint nextCluster,
        long clusterAreaStart,
        int sectorsPerCluster,
        Options options)
    {
        AddDotEntries(directory, directory.FirstCluster, parentFirstCluster);

        foreach (var child in Directory.GetDirectories(directoryPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(child);
            var block = new DirBlock { FirstCluster = nextCluster };
            nextCluster = AllocateChain(fat, nextCluster, DirectorySectorsNeeded(child), sectorsPerCluster);
            AddEntry(directory, name, block.FirstCluster, 0, isDirectory: true);
            WriteTree(image, child, block, directory.FirstCluster, fat, ref nextCluster, clusterAreaStart, sectorsPerCluster, options);
            WriteDirectoryData(image, block, clusterAreaStart, fat, sectorsPerCluster);
        }

        foreach (var child in Directory.GetFiles(directoryPath).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(child);
            var info = new FileInfo(child);
            var first = 0u;
            if (info.Length > 0)
            {
                first = nextCluster;
                nextCluster = AllocateChain(fat, nextCluster, ClusterSectorsNeeded(info.Length, sectorsPerCluster), sectorsPerCluster);
                CopyFileClusters(image, child, first, info.Length, clusterAreaStart, fat, sectorsPerCluster);
            }

            AddEntry(directory, name, first, info.Length, isDirectory: false);
        }
    }

    private static void AddDotEntries(DirBlock directory, uint self, uint parent)
    {
        var dot = new byte[32];
        Array.Copy(Encoding.ASCII.GetBytes(".       "), 0, dot, 0, 8);
        dot[11] = 0x10;
        WriteUInt16(dot, 26, (ushort)self);
        directory.Data.AddRange(dot);

        var dotDot = new byte[32];
        Array.Copy(Encoding.ASCII.GetBytes("..      "), 0, dotDot, 0, 8);
        dotDot[11] = 0x10;
        WriteUInt16(dotDot, 26, (ushort)parent);
        directory.Data.AddRange(dotDot);
    }

    private static int DirectorySectorsNeeded(string dir)
    {
        var entries = Directory.GetFileSystemEntries(dir).Length + 2; // . and ..
        var withLfn = Directory.GetFileSystemEntries(dir)
            .Select(Path.GetFileName)
            .Where(n => n is not null && NeedsLongName(n!))
            .Sum(n => (n!.Length + 12) / 13);
        return (entries + withLfn) * 32 / BytesPerSector + 1;
    }

    private static int ClusterSectorsNeeded(long size, int sectorsPerCluster)
    {
        var bytesPerCluster = sectorsPerCluster * BytesPerSector;
        var clusters = (size + bytesPerCluster - 1) / bytesPerCluster;
        return (int)Math.Max(1, clusters) * sectorsPerCluster;
    }

    private static uint AllocateChain(uint[] fat, uint start, int sectors, int sectorsPerCluster)
    {
        var clusters = Math.Max(1, sectors / sectorsPerCluster);
        for (var i = 0; i < clusters; i++)
        {
            fat[start] = start + 1;
            start++;
        }

        fat[start - 1] = EndOfChain;
        return start;
    }

    private static void AddEntry(DirBlock directory, string name, uint firstCluster, long size, bool isDirectory)
    {
        var (shortStem, shortExt) = ShortName(name);
        if (NeedsLongName(name))
        {
            AppendLongName(directory, name, Checksum(shortStem, shortExt));
        }

        var entry = new byte[32];
        Array.Copy(Encoding.ASCII.GetBytes(shortStem.PadRight(8)[..8]), 0, entry, 0, 8);
        Array.Copy(Encoding.ASCII.GetBytes(shortExt.PadRight(3)[..3]), 0, entry, 8, 3);
        entry[11] = (byte)(isDirectory ? 0x10 : 0x20); // directory / archive
        WriteUInt16(entry, 20, (ushort)(firstCluster >> 16));
        WriteUInt16(entry, 26, (ushort)firstCluster);
        WriteUInt32(entry, 28, (uint)size);
        directory.Data.AddRange(entry);
    }

    private static void AppendLongName(DirBlock directory, string name, byte checksum)
    {
        var utf16 = Encoding.Unicode.GetBytes(name);
        var charCount = utf16.Length / 2;
        var entries = (charCount + 12) / 13;
        for (var e = entries; e >= 1; e--)
        {
            var block = new byte[32];
            block[0] = (byte)(e == entries ? e | 0x40 : e);
            block[11] = 0x0F; // LFN attribute
            block[12] = 0;    // entry type
            block[13] = checksum;
            var offset = (e - 1) * 13;
            WriteUtf16Chars(block, 1, utf16, offset, 5);
            WriteUtf16Chars(block, 14, utf16, offset + 5, 6);
            WriteUtf16Chars(block, 28, utf16, offset + 11, 2);
            directory.Data.AddRange(block);
        }
    }

    private static void WriteUtf16Chars(byte[] block, int at, byte[] utf16, int charOffset, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var source = (charOffset + i) * 2;
            if (source + 1 < utf16.Length)
            {
                block[at + i * 2] = utf16[source];
                block[at + i * 2 + 1] = utf16[source + 1];
            }
            else if (source == utf16.Length)
            {
                block[at + i * 2] = 0;
                block[at + i * 2 + 1] = 0; // null terminator
            }
            else
            {
                block[at + i * 2] = 0xFF;
                block[at + i * 2 + 1] = 0xFF; // padding
            }
        }
    }

    private static bool NeedsLongName(string name)
    {
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var ext = dot > 0 ? name[(dot + 1)..] : "";
        return stem.Length > 8 || ext.Length > 3
            || name.Contains(' ', StringComparison.Ordinal)
            || name.Any(c => c is >= 'a' and <= 'z');
    }

    private static (string Stem, string Ext) ShortName(string name)
    {
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var ext = dot > 0 ? name[(dot + 1)..] : "";
        var cleanStem = new string(stem.Where(c => !InvalidShortChars.Contains(c)).ToArray()).ToUpperInvariant();
        if (cleanStem.Length > 8)
        {
            cleanStem = cleanStem[..6] + "~1";
        }

        var cleanExt = new string(ext.Where(c => !InvalidShortChars.Contains(c)).ToArray()).ToUpperInvariant();
        if (cleanExt.Length > 3)
        {
            cleanExt = cleanExt[..3];
        }

        return (cleanStem, cleanExt);
    }

    private static readonly HashSet<char> InvalidShortChars =
    [
        '"', '*', '+', ',', '/', ':', ';', '<', '=', '>', '?', '[', '\\', ']', '|', '.',
    ];

    private static byte Checksum(string shortStem, string shortExt)
    {
        var name = (shortStem.PadRight(8) + shortExt.PadRight(3)).ToUpperInvariant();
        byte sum = 0;
        foreach (var c in name)
        {
            sum = (byte)(((sum & 1) << 7) + (sum >> 1) + (byte)c);
        }

        return sum;
    }

    private static void WriteDirectoryData(FileStream image, DirBlock directory, long clusterAreaStart, uint[] fat, int sectorsPerCluster)
    {
        var data = directory.Data.Count > 0 ? directory.Data.ToArray() : new byte[32];
        var sectors = (data.Length + BytesPerSector - 1) / BytesPerSector; // sector-align
        var padded = new byte[Math.Max(sectors, 1) * BytesPerSector];
        Array.Copy(data, padded, data.Length);
        var cluster = directory.FirstCluster;
        var at = 0;
        while (cluster >= 2 && cluster != EndOfChain && at < padded.Length)
        {
            image.Seek(ClusterLba(clusterAreaStart, cluster, sectorsPerCluster) * BytesPerSector, SeekOrigin.Begin);
            var chunk = new byte[sectorsPerCluster * BytesPerSector];
            Array.Copy(padded, at, chunk, 0, Math.Min(chunk.Length, padded.Length - at));
            image.Write(chunk, 0, chunk.Length);
            at += chunk.Length;
            cluster = fat[cluster];
        }
    }

    private static void CopyFileClusters(FileStream image, string source, uint firstCluster, long size, long clusterAreaStart, uint[] fat, int sectorsPerCluster)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024);
        var cluster = firstCluster;
        var remaining = size;
        var buffer = new byte[sectorsPerCluster * BytesPerSector];
        while (cluster >= 2 && cluster != EndOfChain && remaining > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                break;
            }

            image.Seek(ClusterLba(clusterAreaStart, cluster, sectorsPerCluster) * BytesPerSector, SeekOrigin.Begin);
            image.Write(buffer, 0, read);
            remaining -= read;
            cluster = fat[cluster];
        }
    }

    private static long ClusterLba(long clusterAreaStart, uint cluster, int sectorsPerCluster) =>
        clusterAreaStart + (cluster - 2L) * sectorsPerCluster;

    private static void WriteSectors(FileStream image, long lba, byte[] data)
    {
        image.Seek(lba * BytesPerSector, SeekOrigin.Begin);
        image.Write(data, 0, data.Length);
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
