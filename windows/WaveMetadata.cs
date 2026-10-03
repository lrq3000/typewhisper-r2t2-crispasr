using System.Buffers.Binary;

namespace R2T2CrispASR;
public static class WaveMetadata
{
    public static double Duration(ReadOnlySpan<byte> wave)
    {
        if (wave.Length < 12 || !wave[..4].SequenceEqual("RIFF"u8) || !wave.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Expected a RIFF/WAVE recording.");
        uint byteRate = 0;
        long dataSize = 0;
        for (int offset = 12; offset + 8 <= wave.Length;)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(wave.Slice(offset + 4, 4));
            long next = offset + 8L + size;
            if (next > wave.Length) throw new InvalidDataException("Truncated WAV chunk.");
            if (wave.Slice(offset, 4).SequenceEqual("fmt "u8) && size >= 16)
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(wave.Slice(offset + 16, 4));
            if (wave.Slice(offset, 4).SequenceEqual("data"u8)) dataSize += size;
            next += size & 1;
            if (next > int.MaxValue) throw new InvalidDataException("WAV is too large.");
            offset = (int)next;
        }
        if (byteRate == 0) throw new InvalidDataException("WAV has no valid format chunk.");
        return (double)dataSize / byteRate;
    }
}
