using TalkWatch.Capture;

namespace TalkWatch.Capture.Tests;

public class SyntheticAudioTests
{
    [Theory]
    [InlineData(1, 8000, 16, 2.5)]
    [InlineData(2, 16000, 16, 1.0)]
    [InlineData(1, 8000, 8, 0.75)]
    public void The_replacement_has_the_same_format_and_length_and_none_of_the_content(int channels, int rate, int bits, double seconds)
    {
        var data = new byte[(int)(rate * seconds) * channels * bits / 8];
        new Random(1).NextBytes(data);
        var original = SyntheticAudio.Build(channels, rate, bits, data);

        var replaced = SyntheticAudio.ReplaceWav(original);

        Assert.Equal(original.Length, replaced.Length);
        Assert.Equal(original[..44], replaced[..44]);
        Assert.Equal(SyntheticAudio.Duration(original), SyntheticAudio.Duration(replaced));
        Assert.NotEqual(original[44..], replaced[44..]);
    }

    [Fact]
    public void Audio_that_is_not_wav_is_refused_rather_than_passed_through() =>
        Assert.Throws<NotSupportedException>(() => SyntheticAudio.ReplaceWav("ID3\u0004mp3 data"u8));
}
