# Installing

You need Docker with Compose, a UniFi console running Talk, and a machine on the same network as the console. TalkWatch talks to the console over its LAN address, never through the cloud.

## Trying it first, without a console

Set `Demo__Enabled=true` (and the bootstrap admin) instead of the console settings, and TalkWatch replays a capture from a real console that ships in the image: 53 calls, with the newest moved to an hour ago so the dashboard has something to show. Every number in it is fictional and every recording synthetic, and every page says so.

A replay on its own would sit still, so the demo keeps it moving: a new call arrives every four minutes (`Demo__CallEveryMinutes`), one of each kind in turn, from missed to answered to hung up in the menu, and calls ring and are answered on **Now** and **Operator** as you watch, so the example flows have something to fire on. It starts with example flows, alerts, people holding roles on the number, and two reports with a copy each to open.

The sign-in page offers a shared **guest** account, with its password on the page and a **Sign in as guest** button. The guest is an admin, so every page and button is there to try. What would change the site for whoever comes next (people, roles, roles on numbers, group mappings, retention, the mail and Telegram settings) is refused when it's saved, with a message saying why, rather than hidden. Nothing leaves the browser: alerts arrive only in TalkWatch, channels other than the browser's own can't be added, test sends are refused, and reports are kept to read but never emailed. Every 30 minutes (`Demo__ResetMinutes`) the demo starts over, clearing the calls, alerts, flows, channels and reports it has gathered and setting the examples up again; a banner counts down to it. People and roles are kept, so nobody is signed out.

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

```sh
cp .env.example .env    # then fill it in
docker compose up -d
```

`.env` holds passwords, so git ignores it. At a minimum, set:

- `DB_PASSWORD` — any long random value; it never leaves the two containers.
- `TALK_CONSOLE_URL`, `TALK_USERNAME`, `TALK_PASSWORD` and `TALK_CERTIFICATE_SHA256`.
- `BOOTSTRAP_ADMIN_USERNAME` and `BOOTSTRAP_ADMIN_PASSWORD` (at least 12 characters) — the first admin account, made on the first start only. Remove the password afterwards.

Every setting is in [Configuration](configuration.md). In production, give the passwords as files rather than environment variables: a file in `/run/secrets` named after the setting, such as `Talk__Password`, overrides the environment, and Docker secrets land there by default.

## 4. The first start

TalkWatch creates its database schema, the site and the first admin, then starts polling. The first poll copies the console's whole call history, a page at a time, and alerts on none of it: only calls from the last 30 minutes raise alerts, so an import doesn't wake anyone. Recordings and voicemail follow, 20 of each per poll, so a long history is copied over a few hours rather than hammering the console.

Sign in at `http://<host>:8080` with the bootstrap admin. TalkWatch opens on **Now**. Add people under **Configure → People** and give each the lines they should see, or a role on a number (see [Signing in and access](access.md)).

The port is published for a first run on the LAN. Before anyone reaches TalkWatch from outside, put a reverse proxy with TLS in front of it: see [Behind a reverse proxy](proxy.md).
