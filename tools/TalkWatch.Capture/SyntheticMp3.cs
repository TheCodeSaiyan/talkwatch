namespace TalkWatch.Capture;

/// <summary>
/// Replaces MP3 audio with silence of exactly the same length, without an encoder.
/// </summary>
/// <remarks>
/// Each MPEG Layer III frame of the original is written again with its own header (so bit rate, sample rate and
/// frame size are unchanged, even in variable bit rate files) and every byte after the header set to zero. Zeroed
/// side information declares no audio data, which decoders play as silence. ID3 tags are dropped, because they can
/// carry names and numbers.
/// </remarks>
public static class SyntheticMp3
{
    private static readonly int[] Mpeg1Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[] Mpeg1SampleRates = [44100, 48000, 32000];

    public static bool IsMp3(ReadOnlySpan<byte> audio) =>
        (audio.Length >= 3 && audio[..3].SequenceEqual("ID3"u8)) || (audio.Length >= 4 && TryReadFrame(audio, out _));

    public static byte[] Replace(ReadOnlySpan<byte> original)
    {
        var output = new List<byte>(original.Length);
        foreach (var (offset, frame) in Frames(original))
        {
            // The encoder's Xing, Info or VBRI frame holds no audio, only frame counts and the padding decoders
            // trim for gapless playback. Kept as it is, it makes the silence exactly as long as the original.
            if (offset == SkipId3v2(original) && IsEncoderInfoFrame(original.Slice(offset, frame.Length)))
            {
                output.AddRange(original.Slice(offset, frame.Length).ToArray());
                continue;
            }

            var header = original.Slice(offset, 4).ToArray();
            header[1] |= 0x01;          // no CRC, so a zeroed body is still a consistent frame
            header[2] &= 0xFE;          // clear the private bit
            header[3] &= 0xF0;          // clear the copyright, original and emphasis fields
            output.AddRange(header);
            output.AddRange(new byte[frame.Length - 4]);
        }

        return output.Count > 0
            ? [.. output]
            : throw new NotSupportedException("No MPEG Layer III frames were found, so this audio cannot be replaced.");
    }

    public static TimeSpan Duration(ReadOnlySpan<byte> mp3)
    {
        double seconds = 0;
        foreach (var (_, frame) in Frames(mp3))
        {
            seconds += (double)frame.SamplesPerFrame / frame.SampleRate;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    public static int FrameCount(ReadOnlySpan<byte> mp3) => Frames(mp3).Count;

    internal readonly record struct Frame(int Length, int SampleRate, int SamplesPerFrame);

    private static List<(int Offset, Frame Frame)> Frames(ReadOnlySpan<byte> mp3)
    {
        var frames = new List<(int, Frame)>();
        var position = SkipId3v2(mp3);
        while (position + 4 <= mp3.Length && TryReadFrame(mp3[position..], out var frame) && position + frame.Length <= mp3.Length)
        {
            frames.Add((position, frame));
            position += frame.Length;
        }

        return frames;
    }

    private static bool IsEncoderInfoFrame(ReadOnlySpan<byte> frame)
    {
        var window = frame[..Math.Min(frame.Length, 48)];
        return window.IndexOf("Xing"u8) >= 0 || window.IndexOf("Info"u8) >= 0 || window.IndexOf("VBRI"u8) >= 0;
    }

    private static int SkipId3v2(ReadOnlySpan<byte> mp3)
    {
        if (mp3.Length < 10 || !mp3[..3].SequenceEqual("ID3"u8))
        {
            return 0;
        }

        // The tag size is 'syncsafe': seven bits per byte.
        var size = (mp3[6] << 21) | (mp3[7] << 14) | (mp3[8] << 7) | mp3[9];
        var footer = (mp3[5] & 0x10) != 0 ? 10 : 0;
        return 10 + size + footer;
    }

    private static bool TryReadFrame(ReadOnlySpan<byte> data, out Frame frame)
    {
        frame = default;
        if (data.Length < 4 || data[0] != 0xFF || (data[1] & 0xE0) != 0xE0)
        {
            return false;
        }

        var version = (data[1] >> 3) & 0x03;      // 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5, 1 = reserved
        var layer = (data[1] >> 1) & 0x03;        // 1 = Layer III
        var bitrateIndex = (data[2] >> 4) & 0x0F;
        var sampleRateIndex = (data[2] >> 2) & 0x03;
        var padding = (data[2] >> 1) & 0x01;
        if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || sampleRateIndex == 3)
        {
            return false;
        }

        var mpeg1 = version == 3;
        var bitrate = (mpeg1 ? Mpeg1Bitrates : Mpeg2Bitrates)[bitrateIndex] * 1000;
        var sampleRate = Mpeg1SampleRates[sampleRateIndex] / (mpeg1 ? 1 : version == 2 ? 2 : 4);
        var samples = mpeg1 ? 1152 : 576;
        var length = (samples / 8 * bitrate / sampleRate) + padding;

        frame = new Frame(length, sampleRate, samples);
        return length > 4;
    }
}
