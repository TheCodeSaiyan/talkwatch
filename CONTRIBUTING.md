# Contributing

Thanks for looking. A few things make a contribution easy to take.

## Before you start

Open an issue first for anything bigger than a small fix, so we can agree the approach before you spend time on it. TalkWatch reads an undocumented API, and a change that works on one console can break on another; the issue is where that comes out.

## Building and testing

You need the .NET 10 SDK and Docker (the tests start PostgreSQL in a container).

```sh
dotnet build
dotnet test
git config core.hooksPath .githooks   # once per clone: the fixture guard
```

A change comes with a test that fails without it. For a bug fix, write the test first and watch it fail.

[Building and testing](https://thecodesaiyan.github.io/talkwatch/developing/) covers the fixture guard, local CI, releases and how the pictures in the docs are made.

## Test data from a real console

Never commit anything captured from a real console as it came. [docs/capture.md](docs/capture.md) explains how captures are taken, sealed and pseudonymised into fixtures. The fixture guard, run as a commit hook and in CI, refuses real-looking phone numbers and unlisted audio, but it can't catch everything: read `fields.txt` in any fixtures you make before committing them.

## Style

Match the code around yours. Comments say why, not what. The docs use British spelling.

## Support

Issues are for bugs and features in TalkWatch. For help setting up UniFi Talk, or anything the console itself does, Ubiquiti's community is the right place. TalkWatch isn't affiliated with Ubiquiti.
