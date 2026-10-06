# Installing

You need Docker with Compose, a UniFi console running Talk, and a machine on the same network as the console. TalkWatch talks to the console over its LAN address, directly or through a private VPN, never through Ubiquiti's cloud. To run TalkWatch somewhere else, see [Reaching a console from elsewhere](remote-console.md).

To look round before you point it at a console, [try the demo](demo.md):
the same image, with a capture from a real console replayed in place of yours.
To run it on Railway rather than on the site's LAN, see
[Deploying on Railway](railway.md).

## 1. A console account for TalkWatch

Create a user on the console that can see Talk but change nothing, and give TalkWatch that user's username and password. Use a separate user rather than your own: it's signed in all the time, and you'll want to be able to change or remove it without touching yours.

TalkWatch signs in by **username**, not e-mail address. It reads the call log, users, ring groups, numbers, voicemail and recordings, and listens to the console's live updates. It never writes anything to the console.

## 2. The console's certificate

Consoles ship a self-signed certificate, so TalkWatch pins it by fingerprint rather than turning certificate checks off. Get the fingerprint with the capture tool, then check it against the certificate your browser shows for the console before you trust it:

```sh
dotnet run --project tools/TalkWatch.Capture -- cert https://192.168.1.1
```

The fingerprint changes if the console's certificate is regenerated, and TalkWatch then refuses to connect until you give it the new one. That's the point: a changed certificate is exactly what a pin is for noticing.

## 3. Start it

Passwords go in files, one each in `secrets/` beside `compose.yaml`, never in `.env` or the environment, where anyone who can list processes or inspect a container reads them. Git ignores the folder.

```sh
mkdir -p secrets
# The database's: PostgreSQL's own superuser, and the role TalkWatch signs in as. No line ending in any of these:
# it would be part of the password.
openssl rand -base64 32 | tr -d '\r\n/+=' > secrets/db_admin_password
openssl rand -base64 32 | tr -d '\r\n/+=' > secrets/db_password
# The console account's password, or an empty file to give it on the Console page instead.
printf '%s' 'the console password' > secrets/talk_password
# The first admin's, at least 12 characters, read on the first start only.
printf '%s' 'a long first password' > secrets/admin_password
chmod 600 secrets/*

cp .env.example .env    # then fill it in
docker compose up -d
```

In `.env`, set `TALK_CONSOLE_URL`, `TALK_USERNAME` and `TALK_CERTIFICATE_SHA256`, `BOOTSTRAP_ADMIN_USERNAME`, and `SITE_PUBLIC_URL` once a reverse proxy is in front. The console's address, account and fingerprint can instead be given after the first start, on **Configure → Console**, which wins over these. Every setting is in [Configuration](configuration.md); any of them can be a file in `/run/secrets` named after it, such as `Talk__Password`, which wins over the environment.

On the first start, PostgreSQL makes a `talkwatch` role that owns TalkWatch's database and nothing else, so a fault in TalkWatch can't reach the rest of the server. TalkWatch makes a key for the credentials it saves, in `.keys` on the audio volume; to keep it with your other secrets instead, add `DataProtection__Key` as a secret before the first start (see [Backing up](backup.md)). The TalkWatch container runs with a read-only file system and no Linux capabilities, and reports its health to Docker.

## 4. The first start

TalkWatch creates its database schema, the site and the first admin, then starts polling. The first poll copies the console's whole call history, a page at a time, and alerts on none of it: only calls from the last 30 minutes raise alerts, so an import doesn't wake anyone. Recordings and voicemail follow, 20 of each per poll, so a long history is copied over a few hours rather than hammering the console.

Sign in at `http://<host>:8080` with the bootstrap admin. TalkWatch opens on **Now**. Add people under **Configure → People** and give each the lines they should see, or a role on a number (see [Signing in and access](access.md)).

The port is published for a first run on the LAN. Before anyone reaches TalkWatch from outside, put a reverse proxy with TLS in front of it: see [Behind a reverse proxy](proxy.md).
