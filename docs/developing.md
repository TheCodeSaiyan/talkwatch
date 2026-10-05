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

## The Railway template

[Deploying on Railway](railway.md) is the guide for anyone deploying; this is
how the template behind it is built. Railway's template composer, in the
Railway dashboard, builds it once, and each deployment is a copy in the
deployer's own account. Railway reviews a template before verifying it, so
keep it and the guide in step. Each value below is what the template holds.

**Postgres**: add Railway's PostgreSQL database.

**TalkWatch**: a service from the Docker image
`ghcr.io/thecodesaiyan/talkwatch:latest`.

- **Settings**: health check path `/healthz`; public networking over HTTP with
  target port `8080`.
- **Volume**: attach one, mounted at `/data/audio`.
- **Variables**:

```text
ConnectionStrings__TalkWatch=Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Database=${{Postgres.PGDATABASE}};Username=${{Postgres.PGUSER}}
Database__Password=${{Postgres.PGPASSWORD}}
Site__PublicUrl=https://${{RAILWAY_PUBLIC_DOMAIN}}
Bootstrap__AdminUsername=admin
Bootstrap__AdminPassword=${{secret(32)}}
RAILWAY_RUN_UID=0
Site__Name=
Site__Region=
Site__TimeZone=
```

Railway wants a description on every variable before it publishes: the empty
ones take theirs from [Configuration](configuration.md), so the deployer knows
what to put there, and the rest say they're filled in. The service's icon is
`https://thecodesaiyan.github.io/talkwatch/assets/mark.svg`. There are no
`Talk__` variables: the console and the way to it are set on the Console page
after deploying, which keeps the WireGuard or Tailscale secrets out of
Railway's variables.

The template is published as
[TalkWatch](https://railway.com/deploy/talkwatch). The README's **Deploy on
Railway** button links to it with the maintainer's referral code, from the
**Share** button on the template's page in the dashboard.

### Redeploying your own Railway instance on a release

The release workflow can redeploy a TalkWatch you run on Railway as soon as
the new image is on GHCR, without waiting for a maintenance window. On the
repository, under **Settings → Secrets and variables → Actions**:

- the secret `RAILWAY_TOKEN`: a project token for that project and environment,
  made in the project's settings;
- the variable `RAILWAY_SERVICE`: the TalkWatch service's name.

With `RAILWAY_SERVICE` unset, the step is skipped. It runs for a release, not a
pre-release, since a pre-release doesn't move `latest`.