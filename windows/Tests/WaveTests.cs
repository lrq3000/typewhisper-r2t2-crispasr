using System.Buffers.Binary;
using Xunit;

namespace R2T2CrispASR.Tests;

public class WaveTests
{
    [Fact]
    public void DurationWalksPaddedMetadataChunks()
    {
        byte[] wave = new byte[56 + 32000];
        "RIFF"u8.CopyTo(wave); "WAVE"u8.CopyTo(wave.AsSpan(8));
        "fmt "u8.CopyTo(wave.AsSpan(12)); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), 32000);
        "JUNK"u8.CopyTo(wave.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), 3);
        "data"u8.CopyTo(wave.AsSpan(48)); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(52), 32000);
        Assert.Equal(1, WaveMetadata.Duration(wave));
    }

    [Fact]
    public void TruncatedDataChunkIsRejected()
    {
        byte[] wave = new byte[20]; "RIFF"u8.CopyTo(wave); "WAVE"u8.CopyTo(wave.AsSpan(8));
        "data"u8.CopyTo(wave.AsSpan(12)); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => WaveMetadata.Duration(wave));
    }
}
