using System.Buffers.Binary;

namespace TalkWatch.Capture;

/// <summary>
/// Replaces recorded audio with a tone of the same format and length. Voices and spoken numbers cannot be
/// pseudonymised, but the copy, checksum and playback paths only need audio of the right shape and size.
/// </summary>
public static class SyntheticAudio
{
    public static bool IsWav(ReadOnlySpan<byte> audio) =>
        audio.Length >= 12 && audio[..4].SequenceEqual("RIFF"u8) && audio[8..12].SequenceEqual("WAVE"u8);

    public static byte[] ReplaceWav(ReadOnlySpan<byte> original)
    {
        if (!IsWav(original))
        {
            throw new NotSupportedException("Only WAV audio can be replaced so far. Other formats need adding once the spike shows which Talk uses.");
        }

        var (channels, sampleRate, bitsPerSample, dataLength) = ReadFormat(original);
        if (bitsPerSample != 16 && bitsPerSample != 8)
        {
            throw new NotSupportedException($"WAV with {bitsPerSample}-bit samples is not supported yet.");
        }

        var bytesPerSample = bitsPerSample / 8;
        var frames = dataLength / (bytesPerSample * channels);
        var data = new byte[frames * bytesPerSample * channels];

        // A quiet 440 Hz tone. Anything deterministic would do; a tone is easy to recognise as synthetic.
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = Math.Sin(2 * Math.PI * 440 * frame / sampleRate) * 0.2;
            for (var channel = 0; channel < channels; channel++)
            {
                var offset = ((frame * channels) + channel) * bytesPerSample;
                if (bytesPerSample == 2)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset), (short)(sample * short.MaxValue));
                }
                else
                {
                    data[offset] = (byte)(128 + (sample * 127));
                }
            }
        }

        return Build(channels, sampleRate, bitsPerSample, data);
    }

    public static TimeSpan Duration(ReadOnlySpan<byte> wav)
    {
        var (channels, sampleRate, bitsPerSample, dataLength) = ReadFormat(wav);
        return TimeSpan.FromSeconds((double)dataLength / (sampleRate * channels * (bitsPerSample / 8)));
    }

    public static byte[] Build(int channels, int sampleRate, int bitsPerSample, byte[] data)
    {
        var wav = new byte[44 + data.Length];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + data.Length);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * channels * bitsPerSample / 8);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(channels * bitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], (short)bitsPerSample);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], data.Length);
        data.CopyTo(span[44..]);
        return wav;
    }

    private static (int Channels, int SampleRate, int BitsPerSample, int DataLength) ReadFormat(ReadOnlySpan<byte> wav)
    {
        int? channels = null, sampleRate = null, bits = null, dataLength = null;
        var position = 12;
        while (position + 8 <= wav.Length && (channels is null || dataLength is null))
        {
            var id = wav.Slice(position, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav[(position + 4)..]);
            if (id.SequenceEqual("fmt "u8))
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(wav[(position + 10)..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav[(position + 12)..]);
                bits = BinaryPrimitives.ReadInt16LittleEndian(wav[(position + 22)..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                dataLength = Math.Min(size, wav.Length - position - 8);
            }

            position += 8 + size + (size % 2);
        }

        return channels is null || dataLength is null
            ? throw new InvalidDataException("The WAV has no fmt or data chunk.")
            : (channels.Value, sampleRate!.Value, bits!.Value, dataLength.Value);
    }
}
