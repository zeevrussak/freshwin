// FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
// Derived from ZuTM's Fat32ImageTests. Round-trip tests for the FAT32 image
// writer: build a small image, then parse it back (BPB, directory entries,
// LFN, cluster chains, file bytes) exactly the way a consumer reads it.

using System.Text;
using Xunit;

namespace FreshWin.Tests;

public class Fat32ImageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("freshwin-fat32-tests-").FullName;

    private byte[] BuildImage(params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(_root, "content", path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        var imagePath = Path.Combine(_root, "test.img");
        Fat32Image.Write(imagePath, Path.Combine(_root, "content"));
        return File.ReadAllBytes(imagePath);
    }

    [Fact]
    public void BootSector_HasValidFat32Geometry()
    {
        var image = BuildImage(("readme.txt", "hello"));
        var boot = image[(2048 * 512)..];

        Assert.Equal(0x55, boot[510]);
        Assert.Equal(0xAA, boot[511]);
        Assert.Equal(512, BitConverter.ToUInt16(boot, 11));           // bytes/sector
        Assert.Equal(0x08, boot[13]);                                 // sectors/cluster (4 KiB)
        Assert.Equal(32, BitConverter.ToUInt16(boot, 14));            // reserved
        Assert.Equal(2, boot[16]);                                    // FATs
        Assert.Equal(0, BitConverter.ToUInt16(boot, 17));             // root entries
        Assert.Equal(0, BitConverter.ToUInt16(boot, 19));             // total sectors 16
        Assert.Equal(0xF8, boot[21]);                                 // media
        Assert.Equal(0, BitConverter.ToUInt16(boot, 22));             // FAT size 16
        Assert.Equal(2u, BitConverter.ToUInt32(boot, 44));            // root cluster
        Assert.Equal(1, BitConverter.ToUInt16(boot, 48));             // FSInfo sector
        Assert.Equal(6, BitConverter.ToUInt16(boot, 50));             // backup boot sector
        Assert.Equal(0x29, boot[66]);                                 // extended boot signature
        Assert.StartsWith("FAT32   ", Encoding.ASCII.GetString(boot, 82, 8));
        Assert.Equal(0x41615252u, BitConverter.ToUInt32(image, 2049 * 512)); // FSInfo lead signature
    }

    [Fact]
    public void Mbr_PointsAtThePartition()
    {
        var image = BuildImage(("readme.txt", "hello"));
        var mbr = image[..512];

        Assert.Equal(0x55, mbr[510]);
        Assert.Equal(0xAA, mbr[511]);
        Assert.Equal(0x0C, mbr[446 + 4]);                                 // FAT32 LBA type
        Assert.Equal(2048u, BitConverter.ToUInt32(mbr, 446 + 8));         // start LBA
        Assert.Equal(BitConverter.ToUInt32(image, 2048 * 512 + 32),       // total sectors matches BPB
            BitConverter.ToUInt32(mbr, 446 + 12));
    }

    [Fact]
    public void DirectoryEntries_ResolveShortAndLongNames()
    {
        var image = BuildImage(
            ("readme.txt", "short name"),
            ("a-very-long-file-name.txt", "long name"));

        var volume = new FatVolume(image);
        var root = volume.ReadDirectory(2);

        Assert.Contains(root, e => e.ShortName == "README.TXT" && e.LongName == "readme.txt");
        var lfn = Assert.Single(root, e => e.LongName == "a-very-long-file-name.txt");
        Assert.Equal("A-VERY~1.TXT", lfn.ShortName);

        Assert.Equal("long name", volume.ReadFile(lfn));
    }

    [Fact]
    public void Subdirectories_AreTraversable()
    {
        var image = BuildImage(
            ("EFI/BOOT/BOOTX64.EFI", "pe-placeholder"),
            ("sources/install.swm", new string('x', 5 * 1024))); // spans clusters

        var volume = new FatVolume(image);
        var root = volume.ReadDirectory(2);
        var efi = Assert.Single(root, e => e.ShortName == "EFI");
        var efiDir = volume.ReadDirectory(efi.FirstCluster);

        var boot = Assert.Single(efiDir, e => e.LongName == "boot");
        var bootDir = volume.ReadDirectory(boot.FirstCluster);
        var bootFile = Assert.Single(bootDir, e => e.LongName == "bootx64.efi");
        Assert.Equal("pe-placeholder", volume.ReadFile(bootFile));

        var sources = Assert.Single(root, e => e.LongName == "sources");
        var sourcesDir = volume.ReadDirectory(sources.FirstCluster);
        var swm = Assert.Single(sourcesDir, e => e.LongName == "install.swm");
        var content = volume.ReadFile(swm);
        Assert.Equal(5 * 1024, content.Length);
        Assert.All(content, c => Assert.Equal('x', c));
    }

    [Fact]
    public void FileContents_RoundTripAcrossClusterChains()
    {
        // 300 KiB spans many 4 KiB clusters with a partial tail cluster.
        var payload = new string('Z', 300 * 1024);
        var image = BuildImage(("big.txt", payload));

        var volume = new FatVolume(image);
        var entry = Assert.Single(volume.ReadDirectory(2), e => e.LongName == "big.txt");
        Assert.Equal(payload, volume.ReadFile(entry));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Minimal read-only FAT32 parser mirroring what a consumer does.</summary>
    private sealed class FatVolume(byte[] image)
    {
        private readonly byte[] _boot = image[(2048 * 512)..(2048 * 512 + 512)];

        private int BytesPerSector => BitConverter.ToUInt16(_boot, 11);

        private int SectorsPerCluster => _boot[13];

        private int ReservedSectors => BitConverter.ToUInt16(_boot, 14);

        private int FatSectors => (int)BitConverter.ToUInt32(_boot, 36);

        private long ClusterAreaStart => 2048L + ReservedSectors + 2L * FatSectors;

        public List<Entry> ReadDirectory(uint firstCluster)
        {
            var raw = ReadChain(firstCluster).SelectMany(b => b).ToArray();
            var result = new List<Entry>();
            var pending = new List<(byte[] Entry, int Sequence)>();

            for (var at = 0; at + 32 <= raw.Length; at += 32)
            {
                if (raw[at] == 0)
                {
                    break;
                }

                if (raw[at + 11] == 0x0F)
                {
                    pending.Add((raw[at..(at + 32)], raw[at] & 0x3F));
                    continue;
                }

                var stem = Encoding.ASCII.GetString(raw, at, 8).TrimEnd();
                var ext = Encoding.ASCII.GetString(raw, at + 8, 3).TrimEnd();
                var shortName = ext.Length > 0 ? stem + "." + ext : stem;
                var longName = AssembleLongName(pending);
                if (longName.Length == 0)
                {
                    longName = shortName.ToLowerInvariant(); // no LFN entries = plain 8.3 name
                }

                pending.Clear();
                var cluster = BitConverter.ToUInt16(raw, at + 26)
                    | (BitConverter.ToUInt16(raw, at + 20) << 16);
                var size = BitConverter.ToInt32(raw, at + 28);
                var isDirectory = (raw[at + 11] & 0x10) != 0;
                if (size > 0 || isDirectory || shortName.StartsWith('.'))
                {
                    result.Add(new Entry(shortName, longName, (uint)cluster, size, isDirectory));
                }
            }

            return result;
        }

        private static string AssembleLongName(List<(byte[] Entry, int Sequence)> entries)
        {
            if (entries.Count == 0)
            {
                return "";
            }

            var chars = new List<char>();
            foreach (var (entry, _) in entries.OrderBy(e => e.Sequence))
            {
                foreach (var (offset, count) in new[] { (1, 5), (14, 6), (28, 2) })
                {
                    for (var i = 0; i < count; i++)
                    {
                        var value = BitConverter.ToChar(entry, offset + i * 2);
                        if (value == '\0')
                        {
                            return new string(chars.ToArray());
                        }

                        if (value != '\uFFFF')
                        {
                            chars.Add(value);
                        }
                    }
                }
            }

            return new string(chars.ToArray());
        }

        public string ReadFile(Entry entry)
        {
            var taken = 0;
            var output = new StringBuilder();
            foreach (var block in ReadChain(entry.FirstCluster))
            {
                foreach (var b in block)
                {
                    if (taken++ >= entry.Size)
                    {
                        return output.ToString();
                    }

                    output.Append((char)b);
                }
            }

            return output.ToString();
        }

        private List<byte[]> ReadChain(uint firstCluster)
        {
            var clusterBytes = SectorsPerCluster * BytesPerSector;
            var result = new List<byte[]>();
            uint cluster = firstCluster;
            while (cluster >= 2 && cluster != 0x0FFFFFFF)
            {
                var offset = (int)((ClusterAreaStart + (cluster - 2L) * SectorsPerCluster) * BytesPerSector);
                result.Add(image[offset..(offset + clusterBytes)]);
                var fatOffset = (int)((2048L + ReservedSectors) * BytesPerSector + cluster * 4);
                cluster = BitConverter.ToUInt32(image, fatOffset) & 0x0FFFFFFF;
            }

            return result;
        }

        public sealed record Entry(string ShortName, string LongName, uint FirstCluster, int Size, bool IsDirectory);
    }
}
