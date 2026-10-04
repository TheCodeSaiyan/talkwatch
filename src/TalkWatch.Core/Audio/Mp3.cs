namespace TalkWatch.Core.Audio;

/// <summary>
/// How long an MP3 plays, by walking its frames: each frame header gives its length in bytes and its samples, so the
/// sum is exact for constant and variable bit rates alike, with no need to decode. Used for switchboard greetings.
/// </summary>
public static class Mp3
{
    // Kilobits per second by bit-rate index, for MPEG-1 and for MPEG-2 and 2.5, by layer.
    private static readonly int[] V1Layer1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
    private static readonly int[] V1Layer2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    private static readonly int[] V1Layer3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] V2Layer1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
    private static readonly int[] V2Layer23 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    /// <summary>The playing time, or null when no MPEG audio frame is found.</summary>
    public static TimeSpan? Duration(ReadOnlySpan<byte> data)
    {
        var at = SkipId3(data);
        double seconds = 0;
        var frames = 0;
        while (at + 4 <= data.Length)
        {
            if (Frame(data[at..]) is not { } frame)
            {
                // Not a frame header here: an ID3v1 tag at the end, padding, or junk. Look on byte by byte.
                at++;
                continue;
            }

            seconds += (double)frame.Samples / frame.SampleRate;
            frames++;
            at += frame.Length;
        }

        return frames == 0 ? null : TimeSpan.FromSeconds(seconds);
    }

    private static int SkipId3(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10 || data[0] != 'I' || data[1] != 'D' || data[2] != '3')
        {
            return 0;
        }

        // A syncsafe size: seven bits a byte. A footer adds ten more.
        var size = (data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9];
        return 10 + size + ((data[5] & 0x10) != 0 ? 10 : 0);
    }

    private readonly record struct FrameHeader(int Length, int Samples, int SampleRate);

    private static FrameHeader? Frame(ReadOnlySpan<byte> h)
    {
        if (h[0] != 0xFF || (h[1] & 0xE0) != 0xE0)
        {
            return null;
        }

        var version = (h[1] >> 3) & 3; // 3: MPEG-1, 2: MPEG-2, 0: MPEG-2.5, 1: reserved
        var layer = (h[1] >> 1) & 3;   // 3: layer I, 2: layer II, 1: layer III, 0: reserved
        var bitrateIndex = h[2] >> 4;
        var rateIndex = (h[2] >> 2) & 3;
        var padding = (h[2] >> 1) & 1;
        if (version == 1 || layer == 0 || bitrateIndex is 0 or 15 || rateIndex == 3)
        {
            return null;
        }

        int[] rates = [44100, 48000, 32000];
        var sampleRate = rates[rateIndex] / (version == 3 ? 1 : version == 2 ? 2 : 4);
        var mpeg1 = version == 3;
        var kbps = (mpeg1, layer) switch
        {
            (true, 3) => V1Layer1[bitrateIndex],
            (true, 2) => V1Layer2[bitrateIndex],
            (true, _) => V1Layer3[bitrateIndex],
            (false, 3) => V2Layer1[bitrateIndex],
            _ => V2Layer23[bitrateIndex],
        };
        var bitrate = kbps * 1000;

        var (samples, length) = layer switch
        {
            3 => (384, ((12 * bitrate / sampleRate) + padding) * 4),
            2 => (1152, (144 * bitrate / sampleRate) + padding),
            _ => (mpeg1 ? 1152 : 576, ((mpeg1 ? 144 : 72) * bitrate / sampleRate) + padding),
        };
        return length < 4 ? null : new FrameHeader(length, samples, sampleRate);
    }
}
