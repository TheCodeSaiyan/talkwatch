using System.Text.Json;
using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Core.Tests;

public class TranscriptTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Classes = ["positive", "neutral", "negative"];

    [Fact]
    public async Task The_transcript_list_reads_as_transcripts_with_lines_summary_and_sentiment()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        var talk = new TalkClient(console.CreateClient());
        await talk.SignInAsync(FixtureConsole.Username, FixtureConsole.Password, Ct);

        var (transcripts, total) = await talk.GetTranscriptsAsync(1, 25, Ct);

        Assert.Equal(55, total);
        Assert.Equal(25, transcripts.Count);
        // One failed transcription: listed, with nothing in it.
        Assert.Equal(24, transcripts.Count(t => t.IsComplete));
        Assert.All(transcripts.Where(t => t.IsComplete), t =>
        {
            Assert.Contains(t.SentimentClass, Classes);
            Assert.All(t.Lines, l => Assert.False(string.IsNullOrEmpty(l.Text)));
        });
        // Talk does not summarise every call: 18 of the 24 here have one.
        Assert.Equal(18, transcripts.Count(t => t.IsComplete && !string.IsNullOrEmpty(t.Summary)));
        Assert.Contains(transcripts, t => t.Lines.Any(l => l.Start is > 0 && l.End > l.Start && l.Speaker is { Length: > 0 }));
    }

    [Fact]
    public void A_live_message_that_a_transcript_is_finished_carries_the_transcript()
    {
        var message = LiveMessage.Parse(File.ReadAllText(Path.Combine(FixtureConsole.DefaultDirectory, "0184-ws-proxy-talk.json")))!;

        var transcript = message.Transcript();

        Assert.NotNull(transcript);
        Assert.True(transcript.IsComplete);
        Assert.Null(LiveMessage.Parse("""{"event": "CALL_LOG_UPDATED", "data": []}""")!.Transcript());
    }

    [Fact]
    public void Times_sent_as_strings_read_as_numbers()
    {
        using var document = JsonDocument.Parse("""
            {"id": "t1", "status": "COMPLETED", "context": {"call_uuid": "c1"},
             "analytics": {"summary": "s", "sentiment": -1.2, "sentimentClassification": "Negative",
               "lines": [{"speaker": "A", "line": "Hello", "line_start_time": "0.59", "line_end_time": 2.5}, {"speaker": "B", "line": ""}]}}
            """);

        var transcript = TalkTranscript.Parse(document.RootElement)!;

        Assert.Equal(("c1", -1.2, "negative"), (transcript.CallUuid, transcript.Sentiment!.Value, transcript.SentimentClass));
        Assert.Equal([new TranscriptLine("A", "Hello", 0.59, 2.5)], transcript.Lines);
    }
}
