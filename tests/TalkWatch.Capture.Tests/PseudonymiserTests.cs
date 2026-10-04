using System.Text.Json.Nodes;
using TalkWatch.Capture;
using TalkWatch.FixtureGuard;

namespace TalkWatch.Capture.Tests;

public class PseudonymiserTests
{
    private static readonly byte[] Key = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    private const string Call = """
        {
          "id": "123e4567-e89b-12d3-a456-426614174000",
          "user_id": "5f2a9c",
          "caller_number": "+44 20 7123 4567",
          "callee": { "number": 447911123456, "display_name": "Jane Smith" },
          "extension": "1001",
          "voicemail": { "transcription": "Hi, it's Jane, ring me on 07911 123456", "duration": 12 },
          "email": "jane.smith@example.co.uk",
          "device": { "mac": "74:83:c2:12:34:56", "ip": "192.168.1.20", "wan_ip": "81.2.69.160" },
          "by_number": { "+442071234567": 3 },
          "started_at": "2026-09-29T12:34:56Z"
        }
        """;

    [Fact]
    public void Personal_values_are_replaced_and_structure_is_kept()
    {
        var output = new Pseudonymiser(Key).Json(Call);
        var json = JsonNode.Parse(output)!;

        Assert.DoesNotContain("7123 4567", output, StringComparison.Ordinal);
        Assert.DoesNotContain("7911", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Jane", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Smith", output, StringComparison.Ordinal);
        Assert.DoesNotContain("example.co.uk", output, StringComparison.Ordinal);
        Assert.DoesNotContain("74:83:c2", output, StringComparison.Ordinal);
        Assert.DoesNotContain("81.2.69.160", output, StringComparison.Ordinal);
        Assert.Empty(Guard.ScanText("out.json", output.Split('\n'), new HashSet<string>()));

        // Identifiers, internal numbers, private addresses and timestamps are what the fixtures are for.
        Assert.Equal("123e4567-e89b-12d3-a456-426614174000", (string?)json["id"]);
        Assert.Equal("5f2a9c", (string?)json["user_id"]);
        Assert.Equal("1001", (string?)json["extension"]);
        Assert.Equal("192.168.1.20", (string?)json["device"]!["ip"]);
        Assert.Equal("2026-09-29T12:34:56Z", (string?)json["started_at"]);
        Assert.Equal(12, (int)json["voicemail"]!["duration"]!);

        // Types and lengths survive: a JSON number stays a number, free text keeps its length.
        Assert.Equal(System.Text.Json.JsonValueKind.Number, json["callee"]!["number"]!.GetValueKind());
        Assert.Equal("Hi, it's Jane, ring me on 07911 123456".Length, ((string)json["voicemail"]!["transcription"]!).Length);
        Assert.Matches("^02(:[0-9a-f]{2}){5}$", (string)json["device"]!["mac"]!);
        Assert.Contains("\"caller_number\": \"+44", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_person_gets_the_same_replacement_everywhere()
    {
        var pseudonymiser = new Pseudonymiser(Key);

        var first = JsonNode.Parse(pseudonymiser.Json(Call))!;
        var second = JsonNode.Parse(pseudonymiser.Json("""{"from":"020 7123 4567","name":"Jane Smith"}"""))!;

        Assert.Equal(((string)first["caller_number"]!)[3..].Replace(" ", "", StringComparison.Ordinal), ((string)second["from"]!)[1..]);
        Assert.Equal((string?)first["callee"]!["display_name"], (string?)second["name"]);
        Assert.Equal(first["by_number"]!.AsObject().Single().Key, (string)first["caller_number"]!);
    }

    [Fact]
    public void Csv_name_columns_and_numbers_are_replaced()
    {
        const string csv = "Date,From,Caller Name,Notes\n2026-09-29 10:00,+442071234567,\"Smith, Jane\",\"called about 07911 123456\"\n";

        var output = new Pseudonymiser(Key).Text(csv, isCsv: true);

        Assert.StartsWith("Date,From,Caller Name,Notes\n2026-09-29 10:00,+44", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Smith", output, StringComparison.Ordinal);
        Assert.DoesNotContain("7911", output, StringComparison.Ordinal);
        Assert.Empty(Guard.ScanText("out.csv", output.Split('\n'), new HashSet<string>()));
    }

    [Fact]
    public void Transcript_and_sms_words_are_replaced_whatever_the_field_is_called()
    {
        const string json = """
            {
              "segments": [ { "speaker": "Jane Smith", "text": "Hello, it is Jane about the invoice", "start": 1.5 } ],
              "sms": [ { "from": "+447911123456", "body": "Running late, Jane", "content_type": "text/plain" } ],
              "summary": "Jane called about an invoice",
              "words": [ { "word": "Jane" } ]
            }
            """;

        var output = new Pseudonymiser(Key).Json(json);
        var node = JsonNode.Parse(output)!;

        Assert.DoesNotContain("Jane", output, StringComparison.Ordinal);
        Assert.DoesNotContain("invoice", output, StringComparison.Ordinal);
        Assert.Equal("text/plain", (string?)node["sms"]![0]!["content_type"]);
        Assert.Equal(1.5, (double)node["segments"]![0]!["start"]!);
    }

    [Fact]
    public void A_name_field_holding_a_number_keeps_a_number()
    {
        var output = JsonNode.Parse(new Pseudonymiser(Key).Json("""{"contact":"+44 20 7123 4567","caller_name":"07911123456"}"""))!;

        Assert.StartsWith("+44", (string)output["contact"]!, StringComparison.Ordinal);
        Assert.StartsWith("07700", (string)output["caller_name"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_or_text_field_with_several_numbers_is_still_replaced()
    {
        var output = new Pseudonymiser(Key).Json("""{"contact":"+44 20 7123 4567 / 07911 123456","note":"ring 07911 123456 or 020 7123 4567"}""");

        Assert.DoesNotContain("7123 4567", output, StringComparison.Ordinal);
        Assert.DoesNotContain("7911", output, StringComparison.Ordinal);
        Assert.Empty(Guard.ScanText("out.json", output.Split('\n'), new HashSet<string>()));
    }

    [Fact]
    public void Whatever_the_digits_look_like_the_guard_accepts_the_output()
    {
        // Replacing a number can change its length, and a neighbouring run of digits can then join the
        // replacement into a new, real-looking number. Every output must still pass the guard.
        var random = new Random(2026);
        string[] separators = ["-", " ", ".", "/", "", "x", ", "];
        var pseudonymiser = new Pseudonymiser(Key);
        for (var i = 0; i < 3000; i++)
        {
            var parts = Enumerable.Range(0, random.Next(1, 6))
                .Select(_ => new string(Enumerable.Range(0, random.Next(1, 9)).Select(_ => (char)('0' + random.Next(10))).ToArray()));
            var value = (random.Next(4) == 0 ? "+" : "") + string.Join(separators[random.Next(separators.Length)], parts);

            var output = pseudonymiser.Json($$"""{"ref":"{{value}}"}""");

            Assert.True(!Guard.ScanText("out.json", output.Split('\n'), new HashSet<string>()).Any(), $"{value} -> {output}");
        }
    }

    [Fact]
    public void Whatever_json_numbers_there_are_the_guard_accepts_the_output()
    {
        var random = new Random(30);
        var pseudonymiser = new Pseudonymiser(Key);
        for (var i = 0; i < 3000; i++)
        {
            var whole = (char)('1' + random.Next(9)) + new string(Enumerable.Range(0, random.Next(6, 19)).Select(_ => (char)('0' + random.Next(10))).ToArray());
            var number = random.Next(3) switch
            {
                0 => whole,
                1 => whole + "." + random.Next(1, 999_999),
                _ => whole[..1] + "." + whole[1..] + "e" + random.Next(1, 30),
            };

            var output = pseudonymiser.Json($$"""{"n":{{number}},"list":[{{number}},1]}""");

            Assert.True(!Guard.ScanText("out.json", output.Split('\n'), new HashSet<string>()).Any(), $"{number} -> {output}");
        }
    }

    [Fact]
    public void Addresses_locations_secrets_and_identifiers_are_replaced()
    {
        const string json = """
            {
              "emergency_address": { "street": "12 Real Road", "city": "Leeds", "region": "West Yorkshire", "postalCode": "LS1 4AP", "isoCountry": "GB", "sid": "AD0123456789abcdef0123456789abcdef" },
              "address": { "customer_name": "Real Person Ltd", "street_secondary": "Flat 3", "state": "West Yorkshire", "postal_code": "LS1 4AP", "address_type": null },
              "controller_latitude": 53.7996, "controller_longitude": -1.5491,
              "birth_date": "1984-03-17", "last4": "4242",
              "cameraRtspUrl": "rtsps://10.10.10.1:7441/Ab12Cd34Ef56Gh78",
              "token": "3f2c1a9e-real-token-value-0000000000",
              "sharedTokens": true,
              "serialno": "F4E2C6A1B3D5",
              "uid_avatar": "https://images.svc.ui.com/avatar/real-user-id.png",
              "ipv6": "2a02:c7c:1234:5678:9abc:def0:1234:5678",
              "local": "fe80::1ff:fe23:4567:890a",
              "time": "12:34:56",
              "state_of_device": "connected"
            }
            """;

        var output = new Pseudonymiser(Key).Json(json);
        var node = JsonNode.Parse(output)!;

        foreach (var real in new[] { "Real Road", "Leeds", "Yorkshire", "LS1 4AP", "Real Person", "Flat 3", "53.79", "1.549", "1984",
                                     "4242", "Ab12Cd34", "real-token", "F4E2C6A1B3D5", "real-user-id", "2a02:c7c" })
        {
            Assert.DoesNotContain(real, output, StringComparison.Ordinal);
        }

        Assert.Equal("GB", (string?)node["emergency_address"]!["isoCountry"]);
        Assert.StartsWith("AD", (string)node["emergency_address"]!["sid"]!, StringComparison.Ordinal);
        Assert.Equal(34, ((string)node["emergency_address"]!["sid"]!).Length);
        Assert.Equal(0.0, (double)node["controller_latitude"]!);
        Assert.True((bool)node["sharedTokens"]!);
        Assert.Equal(12, ((string)node["serialno"]!).Length);
        Assert.StartsWith("2001:db8::", (string)node["ipv6"]!, StringComparison.Ordinal);
        Assert.Equal("fe80::1ff:fe23:4567:890a", (string?)node["local"]);
        Assert.Equal("12:34:56", (string?)node["time"]);
        Assert.Equal("connected", (string?)node["state_of_device"]);
    }

    [Fact]
    public void Keyed_identifiers_never_look_like_phone_numbers()
    {
        var pseudonymiser = new Pseudonymiser(Key);
        for (var i = 0; i < 3000; i++)
        {
            var output = pseudonymiser.Json($$"""{"sid":"PN{{i:D32}}","serialno":"{{i:X12}}","serial":"{{i:D8}}"}""");

            Assert.True(!Guard.ScanText("out.json", output.Split('\n'), new HashSet<string>()).Any(), output);
        }
    }

    [Fact]
    public void Transcript_lines_documents_vectors_greetings_and_organisations_are_replaced()
    {
        const string json = """
            {
              "transcripts": [ {
                "analytics": { "lines": [ { "line": "Hello, you're through to Acme Plumbing", "speaker": "agent", "start": 1.2 } ] },
                "document": "Hello you're through to Acme Plumbing how can I help",
                "vector": "'acm':5 'help':9 'hello':1 'plumb':6",
                "sum_vector": "'acm':1 'plumb':2"
              } ],
              "smart_attendant": { "greeting": "Welcome to Acme" },
              "contact": { "organization": "Acme Plumbing" }
            }
            """;

        var output = new Pseudonymiser(Key).Json(json);
        var node = JsonNode.Parse(output)!;

        Assert.DoesNotContain("Acme", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acm", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Plumb", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("through", output, StringComparison.Ordinal);
        Assert.Equal(1.2, (double)node["transcripts"]![0]!["analytics"]!["lines"]![0]!["start"]!);
    }

    [Theory]
    [InlineData("free_form", "please call me back tomorrow")]
    [InlineData("x", "Meeting with the Smith family")]
    public void Any_unhandled_prose_is_replaced_whatever_the_field(string field, string prose)
    {
        var output = new Pseudonymiser(Key).Json($$"""{"{{field}}":"{{prose}}"}""");

        Assert.Equal(prose.Length, ((string)JsonNode.Parse(output)![field]!).Length);
        Assert.DoesNotContain(prose.Split(' ')[1], output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("please call me back", true)]
    [InlineData("Hello, you're through to Acme Plumbing", true)]
    [InlineData("1 Example Street", false)]
    [InlineData("Alex Archer 12", false)]
    [InlineData("https://example.invalid/a b c", false)]
    [InlineData("Main menu", false)]
    public void The_prose_check_tells_leftover_words_from_the_tools_own_fakes(string text, bool leftover) =>
        Assert.Equal(leftover, Pseudonymiser.IsUnreplacedProse(text));

    [Fact]
    public void Filler_of_any_length_passes_the_prose_check()
    {
        var pseudonymiser = new Pseudonymiser(Key);
        for (var length = 12; length < 400; length += 7)
        {
            var output = pseudonymiser.Json($$"""{"transcript":"{{new string('a', length).Replace("aaaa", "aaa ", StringComparison.Ordinal)}}"}""");
            Assert.False(Pseudonymiser.IsUnreplacedProse((string)JsonNode.Parse(output)!["transcript"]!), $"length {length}");
        }
    }

    [Theory]
    [InlineData("status", "connected")]
    [InlineData("timezone", "Europe/London")]
    [InlineData("menu", "Main menu")]
    public void Short_values_that_are_not_prose_are_kept(string field, string value) =>
        Assert.Equal(value, (string?)JsonNode.Parse(new Pseudonymiser(Key).Json($$"""{"{{field}}":"{{value}}"}"""))![field]);

    [Fact]
    public void Times_in_strings_filenames_and_titles_are_handled()
    {
        const string json = """{"received_at":"1759194592","read_at":"0","recording_filename":"20260930_447911123456.mp3","to_smart_attendant_title":"Acme Plumbing Ltd","note_time":"hello"}""";

        var output = new Pseudonymiser(Key).Json(json);
        var node = JsonNode.Parse(output)!;

        Assert.Equal("1759194592", (string?)node["received_at"]);
        Assert.EndsWith(".mp3", (string)node["recording_filename"]!, StringComparison.Ordinal);
        Assert.DoesNotContain("7911", output, StringComparison.Ordinal);
        Assert.StartsWith("Title ", (string)node["to_smart_attendant_title"]!, StringComparison.Ordinal);
        Assert.DoesNotContain("Acme", output, StringComparison.Ordinal);
        Assert.Empty(Guard.ScanText("out.json", output.Split('\n'), new HashSet<string>()));
    }

    [Fact]
    public void Replaced_identifiers_still_join_across_files()
    {
        var pseudonymiser = new Pseudonymiser(Key);

        var first = JsonNode.Parse(pseudonymiser.Json("""{"sid":"AD0123456789abcdef0123456789abcdef","serial":"F4E2C6A1"}"""))!;
        var second = JsonNode.Parse(pseudonymiser.Json("""{"address_sid":"AD0123456789abcdef0123456789abcdef","serial_number":"F4E2C6A1"}"""))!;

        Assert.Equal((string?)first["sid"], (string?)second["address_sid"]);
        Assert.Equal((string?)first["serial"], (string?)second["serial_number"]);
    }

    [Fact]
    public void Repeated_keys_are_copied_rather_than_refused()
    {
        var output = new Pseudonymiser(Key).Json("""{"id":1,"id":2,"name":"Jane Smith","name":"Jane Smith"}""");

        Assert.Equal(2, output.Split("\"id\"").Length - 1);
        Assert.Equal(2, output.Split("\"name\"").Length - 1);
        Assert.DoesNotContain("Jane", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_number_timestamps_are_kept_and_other_numbers_become_valid_json_numbers()
    {
        const string json = """{"time":1759147200,"time_f":1759147200.123,"time_ms":1759147200123,"cc":447911123456,"short":207123456,"nanp":7911123456}""";

        var output = new Pseudonymiser(Key).Json(json);
        var node = JsonNode.Parse(output)!;

        Assert.Equal(1759147200L, (long)node["time"]!);
        Assert.Equal(1759147200123L, (long)node["time_ms"]!);
        Assert.Equal("1759147200.123", node["time_f"]!.ToJsonString());
        foreach (var field in new[] { "cc", "short", "nanp" })
        {
            Assert.Equal(System.Text.Json.JsonValueKind.Number, node[field]!.GetValueKind());
            Assert.True(ReservedNumbers.IsReserved(node[field]!.ToJsonString()), $"{field}: {node[field]}");
        }

        Assert.Empty(Guard.ScanText("out.json", output.Split('\n'), new HashSet<string>()));
    }

    [Fact]
    public void Field_names_are_listed_for_review_without_their_values()
    {
        var pseudonymiser = new Pseudonymiser(Key);
        pseudonymiser.Json(Call);

        Assert.Contains("display_name", pseudonymiser.FieldNames);
        Assert.Contains("wan_ip", pseudonymiser.FieldNames);
        Assert.DoesNotContain(pseudonymiser.FieldNames, f => f.Contains("Jane", StringComparison.Ordinal));
    }
}
