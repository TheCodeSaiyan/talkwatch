# Developing

You need the .NET 10 SDK and Docker: the tests start PostgreSQL in a container,
so they run against the database TalkWatch actually uses rather than a stand-in.

```sh
dotnet build
dotnet test
git config core.hooksPath .githooks   # once per clone: the fixture guard
```

A change comes with a test that fails without it. For a bug fix, write the test
first and watch it fail. [CONTRIBUTING.md](https://github.com/TheCodeSaiyan/talkwatch/blob/main/CONTRIBUTING.md)
says what makes a contribution easy to take.

## Test data from a real console

TalkWatch is built against a capture of a real console's traffic, replayed in
the tests and in the demo. Nothing captured from a console goes in as it came:
[Capturing the Talk API](capture.md) explains how a capture is taken, sealed
and pseudonymised into fixtures.

The fixture guard refuses any commit that puts a phone number outside the
fictional ranges (Ofcom's drama numbers, North American 555-0100 to 555-0199)
into `tests/fixtures`, or audio that isn't listed in
`tests/fixtures/synthetic-audio.sha256`. It runs as a commit hook and again on
every release. It can't catch everything, so read `fields.txt` in any fixtures
you make before committing them.

## CI

Day-to-day CI runs on the maintainer's own Docker rather than on hosted
runners, so it costs nothing per push. On a branch:

```sh
scripts/ci.sh
```

It tests the committed HEAD in a clean SDK container (through `git archive`, so
stray local edits can't make a run pass), runs the fixture guard, builds the
documentation strictly, builds the image and checks the upgrade from the last
release. It posts the result to GitHub as the commit's `local-ci` status, which
the pull request shows.

After a merge, `scripts/ci.sh --publish` on an up-to-date `main` does the same
and pushes the image as `edge`, `live` and `demo` to the registry named by
`REGISTRY_HOST` or `git config talkwatch.registry`. The maintainer's live and
demo installs each follow their own tag, so one updating can't leave the other
behind on a tag it already thinks is current.

A release is a tag. Pushing `v1.2.3` runs `.github/workflows/release.yml` on
GitHub Actions: the tests and fixture guard again, the upgrade from the last
release, images for amd64 and arm64 on GHCR, this site, and a GitHub release.

## The pictures in these pages

The screenshots and animations here are taken of the demo, in Windows Sandbox
rather than on anyone's own desktop, so no browser window opens and nothing is
clicked on the machine doing it:

```powershell
pwsh tools/docs-capture/run.ps1    # after scripts/ci.sh has built talkwatch:ci
```

It starts the demo from the image with a throwaway database, hands Sandbox a
copy of Node and Playwright, which drive Sandbox's own Edge headless, and makes
the GIFs from the recorded frames once Sandbox has closed. The animations of
Now are cut from four minutes of the board left running, around the first ride,
arrival and missed call it catches, so a run that catches none of one kind says
so rather than inventing it. Everything lands in a staging folder to look over
before it's copied into `docs/images`.
