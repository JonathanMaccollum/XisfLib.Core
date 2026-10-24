using Xunit;

namespace XisfLib.Core.Tests;

/// <summary>
/// Regression reads of real-world files from the owner's library. The files are never copied into
/// the repository: set XISF_FIXTURES_DIR to a folder holding them, either under the relative paths
/// below or flat by file name. Unset, every case is skipped.
/// </summary>
public sealed class RealFileFixtureTests
{
    private const string FixturesVariable = "XISF_FIXTURES_DIR";

    private static readonly string? FixturesDirectory = Environment.GetEnvironmentVariable(FixturesVariable);

    public static TheoryData<string> FullReadFixtures() => new()
    {
        @"Framing\Barnard 3 and 4 with ngc1333.xisf",
        @"950mm\Flats\20240907\_B_FLAT_2024-09-07_19-46-56_0007_0.19s_6.90_High Gain 2CMS_0.00.xisf",
        @"DarkLibrary\ZWO ASI678MC\20220902.MasterDark.Gain.200.Offset.25.0C.1132x30s.xisf",
        @"2mm\All-Sky 2024-05-12 - Aurora Night 3 KP6\15.00s\All-Sky 2024-05-12 - Aurora Night 3 KP6__LIGHT_2024-05-12_21-13-24_6051_15.00s_15.10_0.00.xisf",
    };

    /// <summary>
    /// PixInsight splits a block into subblocks only past the codec limit, so every two-subblock
    /// PixInsight file holds a block over 2 GiB uncompressed, which one managed array cannot hold.
    /// </summary>
    private const string TwoSubblockFixture =
        @"950mm\Properller and Smaug\Processing\Eye of Smaug Take 2P2.RGB.R.26x180s.G.33x180s.PB.35x180s.PSFSW.ESD.LN.LSPR.MSGR.SCC.Aligned.BlurXCorrectOnly.xisf";

    public sealed class FixtureTheoryAttribute : TheoryAttribute
    {
        public FixtureTheoryAttribute()
        {
            if (string.IsNullOrEmpty(FixturesDirectory))
                Skip = $"{FixturesVariable} is not set";
        }
    }

    public sealed class FixtureFactAttribute : FactAttribute
    {
        public FixtureFactAttribute()
        {
            if (string.IsNullOrEmpty(FixturesDirectory))
                Skip = $"{FixturesVariable} is not set";
        }
    }

    private static string Resolve(string fixture)
    {
        var relative = fixture.Replace('\\', Path.DirectorySeparatorChar);
        var nested = Path.Combine(FixturesDirectory!, relative);
        if (File.Exists(nested))
            return nested;

        var flat = Path.Combine(FixturesDirectory!, Path.GetFileName(relative));
        Assert.True(File.Exists(flat), $"Fixture not found under {FixturesDirectory}: {fixture}");
        return flat;
    }

    private static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    private static XisfMetadataUnit ReadHeaderOnly(string path)
    {
        byte[] headerOnly;
        using (var file = OpenRead(path))
        {
            var preamble = new byte[TestKit.HeaderOffset];
            file.ReadExactly(preamble);
            headerOnly = new byte[TestKit.HeaderOffset + (int)BitConverter.ToUInt32(preamble, 8)];
            preamble.CopyTo(headerOnly, 0);
            file.ReadExactly(headerOnly.AsSpan(TestKit.HeaderOffset));
        }

        using var stream = new MemoryStream(headerOnly, writable: false);
        using var reader = new XisfReader();
        return reader.ReadMetadataFromStream(stream);
    }

    private static int SampleSize(XisfSampleFormat format) => format switch
    {
        XisfSampleFormat.UInt8 => 1,
        XisfSampleFormat.UInt16 => 2,
        XisfSampleFormat.UInt32 or XisfSampleFormat.Float32 => 4,
        XisfSampleFormat.UInt64 or XisfSampleFormat.Float64 or XisfSampleFormat.Complex32 => 8,
        _ => 16
    };

    private static long ByteLength(XisfImageGeometry geometry, XisfSampleFormat format)
        => geometry.Dimensions.Aggregate(1L, (product, dimension) => product * dimension)
           * geometry.ChannelCount * SampleSize(format);

    [FixtureTheory]
    [MemberData(nameof(FullReadFixtures))]
    public async Task Fixture_FullReadAndHeaderOnlyMetadataRead_Succeed(string fixture)
    {
        var path = Resolve(fixture);

        var metadata = ReadHeaderOnly(path);
        Assert.NotEmpty(metadata.Images);

        using var stream = OpenRead(path);
        using var reader = new XisfReader();
        var unit = await reader.ReadAsync(stream, path);

        Assert.Equal(metadata.Images.Count, unit.Images.Count);
        for (var i = 0; i < unit.Images.Count; i++)
        {
            Assert.Equal(metadata.Images[i].Geometry.Dimensions, unit.Images[i].Geometry.Dimensions);
            Assert.Equal(ByteLength(unit.Images[i].Geometry, unit.Images[i].SampleFormat),
                TestKit.Pixels(unit.Images[i]).Length);
        }
    }

    [FixtureFact]
    public async Task Fixture_TwoSubblockPixInsightFile_HeaderOnlyReadsAndFullReadIsNotSupported()
    {
        var path = Resolve(TwoSubblockFixture);

        var info = Assert.Single(ReadHeaderOnly(path).Images).DataBlockInfo;
        var compression = Assert.IsType<XisfCompression>(info.Compression);
        var subblocks = Assert.IsAssignableFrom<IReadOnlyList<XisfCompressionSubblock>>(compression.Subblocks);
        Assert.Equal(2, subblocks.Count);
        Assert.Equal(info.Size, subblocks.Aggregate(0UL, (sum, s) => sum + s.CompressedLength));
        Assert.Equal(compression.UncompressedSize, subblocks.Aggregate(0UL, (sum, s) => sum + s.UncompressedLength));
        Assert.True(compression.UncompressedSize > (ulong)Array.MaxLength);

        using var stream = OpenRead(path);
        using var reader = new XisfReader();
        await Assert.ThrowsAsync<NotSupportedException>(() => reader.ReadAsync(stream, path));
    }
}
