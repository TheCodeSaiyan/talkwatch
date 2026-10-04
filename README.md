# TalkWatch

A self-hosted dashboard and alert router for UniFi Talk.

TalkWatch reads call history, extension and handset status, voicemail and recordings from a UniFi Talk console (UDM Pro or UDM SE), keeps its own copy, and works out what actually happened to each call, which Talk's own log doesn't say. On top of that it has two live boards (**Now**, and **Operator** for whoever looks after the phones), a call-back list, alert flows that route missed calls, new voicemail or a handset going offline to the people who should hear about them, and scheduled reports. Each person sees only the lines they've been given.

> **Status: pre-release.** It runs against one real console (UniFi OS 5.1.33, Talk 5.3.2) and has not had a tagged release yet. The documentation, starting with [Installing](docs/install.md), says what works and what doesn't yet.

## Not affiliated with Ubiquiti

TalkWatch is an independent project. It is not made, endorsed or supported by Ubiquiti Inc. UniFi and UniFi Talk are Ubiquiti's trademarks, used here only to say what TalkWatch works with. It relies on an undocumented API that Ubiquiti can change at any time, so a firmware update may stop parts of it working until a fix is released.

## Developing

Requires the .NET 10 SDK and Docker.

```sh
dotnet build
dotnet test
```

Enable the fixture guard once per clone:

```sh
git config core.hooksPath .githooks
```

The guard refuses any commit that puts a phone number outside the fictional ranges (Ofcom's drama numbers, North American 555-0100 to 555-0199) into `tests/fixtures`, or audio that is not listed in `tests/fixtures/synthetic-audio.sha256`. CI runs the same check. Test data from a real console is pseudonymised before it is committed and never goes in as it was captured. [docs/capture.md](docs/capture.md) explains how a capture is taken.

Day-to-day CI runs on the maintainer's Docker rather than GitHub Actions; a release, from a tag, runs on Actions (`.github/workflows/release.yml`). On a branch, run:

```sh
scripts/ci.sh
```

It tests the committed HEAD in a clean SDK container, runs the fixture guard and builds the image. It then posts the result to GitHub as the commit's `local-ci` status, which the pull request shows. After merging, run `scripts/ci.sh --publish` on an up-to-date `main`. That does the same and then pushes the image three times, as `edge`, `live` and `demo`, to the registry named by `REGISTRY_HOST` or `git config talkwatch.registry`, using Docker's own login to it. The maintainer's live and demo installs each follow their own tag, so one updating can't leave the other behind on a tag it already thinks is current. The Actions workflow is kept, and can be run by hand from the Actions tab.

## Configuration

Every setting, with its default and what it does, is in [docs/configuration.md](docs/configuration.md). The file is generated from the code, and a test keeps the two in step.

## Backups

[docs/backup.md](docs/backup.md) covers what to back up (the database and the audio folder), how to restore, and how TalkWatch puts right the few minutes between the two backups.

## Licence

MIT. See [LICENSE](LICENSE).
