# Capturing the Talk API

TalkWatch reads an undocumented API, so its shapes are learned from a real console. This is how a capture is taken without real data ever reaching the repository.

There are two layers:

1. **Captures** are sealed with AES-GCM the moment they are recorded. The plaintext is never written to disk. They stay on the machine that took them, and `.gitignore` refuses `*.twcap` and `*.har`.
2. **Fixtures** are what gets committed. They are made from captures by the pseudonymiser, then checked by the fixture guard. If the guard finds anything, the output is deleted.

Everything below uses `talkwatch-capture`, run from the repository root:

```sh
dotnet run --project tools/TalkWatch.Capture -- <command>
```

## 1. Make a capture key, once

```sh
dotnet run --project tools/TalkWatch.Capture -- keygen
```

Store the printed value in your password manager, and set it as `TALKWATCH_CAPTURE_KEY` for each session. For example, with 1Password:

```sh
export TALKWATCH_CAPTURE_KEY=$(op read "op://<vault>/<item>/<field>")
```

One key does two jobs. The encryption key and the pseudonymisation key are both derived from it, and neither reveals the other. Lose it and the captures cannot be opened. Share it and the fixtures' mapping can be reversed. So it never goes near the repository.

## 2. Record what the Talk UI does

This is the most reliable way to find the endpoints, because it records exactly what Ubiquiti's own UI calls, WebSocket traffic included.

1. Open the Talk application on the console in Chrome or Edge, with devtools open on the Network tab and **Preserve log** ticked.
2. Visit everything: call history (page through it), extensions, devices, trunks, numbers, ring groups, voicemail (play one), recordings (play one), and the live status views. Leave the live view open through a call or two.
3. Export the log with **Save all as HAR with content**.
4. Seal it, then delete the HAR, which is not encrypted:

```sh
dotnet run --project tools/TalkWatch.Capture -- import-har talk.har --out talk-ui.twcap --unifi-os <version> --talk <version>
rm talk.har
```

The UniFi OS and Talk versions are shown in the console's settings. They become the supported baseline, so record them.

The import keeps response bodies and WebSocket messages only. It drops every request body and every header, and anything under an auth path, so the sign-in password, cookies and CSRF tokens never reach the archive.

## 3. Fetch the full data with TalkWatch's own account (optional)

The UI only loads what was on screen. To fetch every GET endpoint the HAR revealed, signed in as the dedicated local account TalkWatch will use:

```sh
dotnet run --project tools/TalkWatch.Capture -- cert https://<console>
# Check this fingerprint against the console's certificate before trusting it.
export TALKWATCH_CONSOLE_PASSWORD=...
dotnet run --project tools/TalkWatch.Capture -- fetch --console https://<console> --user <account> \
    --pin <fingerprint> --paths-from talk-ui.twcap --out fetched.twcap
```

This also shows whether that account's role is enough, which is one of the questions the spike has to answer. `--paths <file>` takes a hand-written list instead, one path per line.

## 4. Check what was captured

```sh
dotnet run --project tools/TalkWatch.Capture -- list talk-ui.twcap fetched.twcap
```

This shows each entry's method, status, size and path. The paths are pseudonymised and no bodies are shown, so the output is safe to paste into an issue or a conversation.

## 5. Make fixtures

```sh
dotnet run --project tools/TalkWatch.Capture -- fixtures talk-ui.twcap fetched.twcap --out tests/fixtures/<talk-version> --include /proxy/talk/
```

`--include` keeps only paths that start with one of the given prefixes, separated by commas. The console's own system events are large and are not what TalkWatch reads. Exact duplicate responses are always left out, because a capture repeats many requests.

- Phone numbers become fictional ones in the same style: a mobile stays a mobile, `+44` stays `+44`. The same real number gets the same fictional one in every file, so records still join up.
- Names and free text are found by property name and replaced. E-mail addresses, MAC addresses and public IP addresses are replaced wherever they appear.
- Audio is replaced and listed in `tests/fixtures/synthetic-audio.sha256`. WAV becomes a tone of the same format and length. MP3, which is what Talk serves, becomes silence with the same frames, so it has the same bit rate, size and decoded length. ID3 tags, which can carry names, are dropped. Other audio formats are refused, and other binary content (such as camera snapshots) is left out.
- Postal addresses, coordinates, birth dates, card digits, titles, secrets (tokens, keys, webhook and camera stream URLs), serials and SIDs, avatar links and public IPv6 addresses are replaced by fixed or keyed fakes. Keyed fakes keep their length, so records still join across files.
- Ids, extension numbers, timestamps, private IP addresses and the shape of every payload are kept, because the shape is what the fixtures are for. Times sent as strings are kept only in fields named as times, such as `received_at`.

**Before committing, read `fields.txt` in the output.** It lists every property name the capture contained, and no values. Name and free-text fields are recognised by their names, so a personal field under an unexpected name would get through. That list is how a person catches one. If you find one, add the name to the rules in `Pseudonymiser.cs` and run `fixtures` again into a new directory.

## Known limits

- **Timestamps.** A bare JSON number that reads as a time between 2000 and 2100, in seconds, milliseconds or microseconds, is treated as a timestamp and left alone, by both the pseudonymiser and the guard. APIs send times that way, and without this rule every time in the fixtures would be scrambled. The cost: a phone number sent as a bare JSON number, with no leading `0` or `+44`, that happens to fall in that range (a London `20…` number does) would pass as a time. Numbers in strings are never treated as times. Check `fields.txt` for a numeric field that holds phone numbers.
- **Names and free text are found by field name.** See step 5.
