using System.Text;
using TalkWatch.Capture;

namespace TalkWatch.Capture.Tests;

public class SyntheticMp3Tests
{
    // MPEG1 Layer III headers at 44.1 kHz with no CRC: 128 kbit/s makes 417-byte frames, 64 kbit/s 208-byte ones.
    private static readonly byte[] Header128 = [0xFF, 0xFB, 0x90, 0x44];
    private static readonly byte[] Header64 = [0xFF, 0xFB, 0x50, 0x44];

    internal static byte[] Frames(int count, bool variable = false, byte[]? id3 = null)
    {
        var random = new Random(7);
        var bytes = new List<byte>(id3 ?? []);
        for (var i = 0; i < count; i++)
        {
            var header = variable && i % 2 == 1 ? Header64 : Header128;
            var body = new byte[(header == Header64 ? 208 : 417) - 4];
            random.NextBytes(body);
            bytes.AddRange(header);
            bytes.AddRange(body);
        }

        return [.. bytes];
    }

    private static byte[] Id3(string text)
    {
        var frame = Encoding.ASCII.GetBytes("TIT2").Concat(new byte[] { 0, 0, 0, (byte)(text.Length + 1), 0, 0, 0 }).Concat(Encoding.ASCII.GetBytes(text)).ToArray();
        return [.. "ID3"u8.ToArray(), 3, 0, 0, 0, 0, 0, (byte)frame.Length, .. frame];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_replacement_keeps_every_frame_and_the_duration(bool variable)
    {
        var original = Frames(40, variable);

        var replaced = SyntheticMp3.Replace(original);

        Assert.Equal(original.Length, replaced.Length);
        Assert.Equal(40, SyntheticMp3.FrameCount(replaced));
        Assert.Equal(SyntheticMp3.Duration(original), SyntheticMp3.Duration(replaced));
        Assert.Equal(TimeSpan.FromSeconds(40 * 1152 / 44100.0), SyntheticMp3.Duration(replaced));
    }

    [Fact]
    public void Nothing_of_the_original_audio_survives()
    {
        var replaced = SyntheticMp3.Replace(Frames(3));

        for (var frame = 0; frame < 3; frame++)
        {
            Assert.Equal([0xFF, 0xFB, 0x90, 0x40], replaced.AsSpan(frame * 417, 4).ToArray());
            Assert.All(replaced.AsSpan((frame * 417) + 4, 413).ToArray(), b => Assert.Equal(0, b));
        }
    }

    [Fact]
    public void Id3_tags_are_dropped()
    {
        var original = Frames(5, id3: Id3("Jane Smith voicemail"));

        var replaced = SyntheticMp3.Replace(original);

        Assert.True(SyntheticMp3.IsMp3(original));
        Assert.Equal(5 * 417, replaced.Length);
        Assert.DoesNotContain("Jane", Encoding.ASCII.GetString(replaced), StringComparison.Ordinal);
    }

    [Fact]
    public void The_encoder_info_frame_is_kept_so_decoders_trim_the_same_padding()
    {
        var original = Frames(4);
        "Info"u8.CopyTo(original.AsSpan(4 + 32));

        var replaced = SyntheticMp3.Replace(original);

        Assert.Equal(original.AsSpan(0, 417).ToArray(), replaced.AsSpan(0, 417).ToArray());
        Assert.All(replaced.AsSpan(417 + 4, 413).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_crc_protected_frame_is_written_without_its_crc_flag()
    {
        var original = Frames(2);
        original[1] = 0xFA;

        Assert.Equal(0xFB, SyntheticMp3.Replace(original)[1]);
    }

    [Fact]
    public void Something_that_is_not_mp3_is_refused()
    {
        Assert.False(SyntheticMp3.IsMp3("{\"calls\":[]}"u8));
        Assert.Throws<NotSupportedException>(() => SyntheticMp3.Replace("not audio at all"u8));
    }
}
