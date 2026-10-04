using System.Globalization;
using System.Text;
using TalkWatch.Core.Talk;

namespace TalkWatch.Core.Calls;

/// <summary>What a call forwarded to an outside answering line turned out to be, once its transcript was read.</summary>
public enum OutsideFinding
{
    /// <summary>A person took it: none of the line's greeting phrases is in what was said.</summary>
    Person,

    /// <summary>The provider's greeting answered and the caller spoke after it: a message was left there.</summary>
    MessageLeft,

    /// <summary>The provider's greeting answered and the caller said nothing after it.</summary>
    HungUpAtGreeting,
}

/// <summary>
/// Tells from a call's transcript whether a forward to an outside Contact reached a person or the provider's voicemail.
/// Talk logs such a call as accepted the moment anything picks up, so only what was said can tell. A greeting phrase
/// matches loosely: case and punctuation do not count, and a couple of words may come between its words. Whatever is
/// said after the last greeting phrase is the caller's, and a few words of it make a message.
/// </summary>
public static class OutsideVoicemail
{
    /// <summary>Words that may come between two words of a phrase and still match it.</summary>
    public const int Gap = 2;

    /// <summary>The fewest words after the greeting that count as a message left.</summary>
    public const int MessageWords = 3;

    /// <summary>
    /// The finding, with the phrase that matched; null when the transcript is too short to tell (under
    /// <see cref="MessageWords"/> words in all) or no phrases are given, so the call is left as it was.
    /// </summary>
    public static (OutsideFinding Finding, string? Phrase)? Judge(IReadOnlyList<TranscriptLine> lines, IEnumerable<string> phrases)
    {
        var words = new List<(string Word, int Line)>();
        for (var i = 0; i < lines.Count; i++)
        {
            words.AddRange(Words(lines[i].Text).Select(w => (w, i)));
        }

        var wanted = phrases.Select(p => (Phrase: p.Trim(), Words: Words(p))).Where(p => p.Words.Count > 0).ToList();
        if (wanted.Count == 0 || words.Count < MessageWords)
        {
            return null;
        }

        // The greeting ends where the last phrase found in it ends.
        (int End, string Phrase)? greeting = null;
        foreach (var (phrase, target) in wanted)
        {
            if (Find(words, target) is { } end && (greeting is null || end > greeting.Value.End))
            {
                greeting = (end, phrase);
            }
        }

        if (greeting is not { } found)
        {
            return (OutsideFinding.Person, null);
        }

        // After the greeting: the caller's words. When the transcript names speakers, the greeting's own speaker carrying
        // on (a provider reading out its options) is not the caller.
        var provider = lines[words[found.End].Line].Speaker;
        var after = words.Skip(found.End + 1)
            .Count(w => provider is null || lines[w.Line].Speaker is null || !string.Equals(lines[w.Line].Speaker, provider, StringComparison.Ordinal));
        return (after >= MessageWords ? OutsideFinding.MessageLeft : OutsideFinding.HungUpAtGreeting, found.Phrase);
    }

    // The index of the last word of the first place the phrase's words come in order, with at most Gap words between.
    private static int? Find(List<(string Word, int Line)> words, List<string> target)
    {
        for (var start = 0; start < words.Count; start++)
        {
            if (words[start].Word != target[0])
            {
                continue;
            }

            var at = start;
            var matched = 1;
            while (matched < target.Count)
            {
                var next = -1;
                for (var k = at + 1; k <= Math.Min(words.Count - 1, at + 1 + Gap); k++)
                {
                    if (words[k].Word == target[matched])
                    {
                        next = k;
                        break;
                    }
                }

                if (next < 0)
                {
                    break;
                }

                (at, matched) = (next, matched + 1);
            }

            if (matched == target.Count)
            {
                return at;
            }
        }

        return null;
    }

    // Lower case, letters and digits only, an apostrophe dropped rather than splitting the word ("you're" is "youre"). The
    // letter o and the digit 0 count as one: speech to text writes a brand however it hears it, and the O2 greeting comes
    // out as "02", so a phrase typed as it is said ("O2 messaging") would otherwise never match.
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(c == '0' ? 'o' : char.ToLower(c, CultureInfo.InvariantCulture));
            }
            else if (c is '\'' or '’')
            {
                continue;
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return words;
    }
}
