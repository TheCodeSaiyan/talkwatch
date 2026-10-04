# Upgrading

Pull the new image and restart:

```sh
docker compose pull talkwatch
docker compose up -d talkwatch
```

Each release is published as `ghcr.io/thecodesaiyan/talkwatch` for amd64 and arm64, under three tags: its version (`1.2.3`), its minor version (`1.2`), and `latest`. `latest` is what `compose.yaml` runs, so a pull moves you to the newest release. To take only fixes, set `TALKWATCH_IMAGE=ghcr.io/thecodesaiyan/talkwatch:1.2` in `.env`; to move only when you change it, give the full version. A pre-release (`1.3.0-rc.1`) gets only its own tag, so nobody is moved onto one without asking for it.

TalkWatch migrates its database when it starts, then checks that every copied recording and voicemail is where the database says. Take a [backup](backup.md) first: it's the only way back if an upgrade goes wrong.

## How upgrades are tested

Before an image is published, the build runs the previous published image against a clean database, then starts the new one on the same database: it has to migrate it and come up healthy with nothing logged as an error. Started from an image eleven migrations old, that check applied all eleven cleanly. Migrations that rewrite data, such as working out the outcome of calls stored before outcomes existed, have tests of their own.

## When the console changes

Ubiquiti can change the console's API in any update, and TalkWatch checks every response against the shape it expects. When one doesn't match, it stops writing rather than store something wrong. It puts a banner on every page naming the console update that came before it, and raises a **TalkWatch stopped copying calls** alert. The console keeps its call log, so once a fixed TalkWatch is out, it catches up without losing anything.

TalkWatch also records the console's UniFi OS and Talk versions and release channels, and warns when they're newer than the ones it was tested against, or on a pre-release channel, where changes arrive first.
