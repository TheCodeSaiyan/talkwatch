# Security

## Reporting a vulnerability

Please report security problems privately, through GitHub's **Report a vulnerability** button on this repository's Security tab, rather than in an issue. Say what you found, how to reproduce it, and what version or commit you tested.

You'll get an acknowledgement within a week. This is a project maintained in spare time, so a fix can take longer; you'll be kept told how it's going, and credited in the release notes unless you'd rather not be.

## What's in scope

TalkWatch itself: the web app, its API, the image, and the capture tool. Problems in UniFi Talk or the console belong with Ubiquiti; problems in a dependency belong with that project, though a note here helps if TalkWatch is affected.

## What TalkWatch assumes

- **It runs behind a reverse proxy with TLS.** It serves plain HTTP on port 8080 and should not be exposed without one. See the proxy guide in the docs.
- **The console is trusted by its pinned certificate** (`Talk__CertificateSha256`), not by turning certificate checks off.
- **Secrets are given as files** in `/run/secrets` in production, not as environment variables, which leak into process listings and container inspection. The bundled `compose.yaml` does this, signs in to PostgreSQL as a role that owns TalkWatch's database and nothing else, and runs TalkWatch read-only with no Linux capabilities.
- **On Railway, TalkWatch runs as root** (`RAILWAY_RUN_UID=0`), because Railway mounts volumes owned by root and the image's own user couldn't write recordings. The image has no shell or package manager, but a fault that ran code in TalkWatch there would run it as root inside its container.
- **Stored credentials are encrypted** with keys that are themselves encrypted with `DataProtection__Key`, or a key TalkWatch makes on the data volume, never kept in the database. A copy of the database alone reads no credential; keep that key apart from database backups.
- **The console account is read only.** TalkWatch never writes to the console, and doesn't need an account that can.

Supported versions: only the newest release, and `edge` until the first one.
