# Deploying on Railway

TalkWatch can run on [Railway](https://railway.com) rather than on a machine
at the site, which suits an installer looking after several sites as well as a
site with nowhere to run a container. The TalkWatch template deploys into your
own Railway account: the published image, a PostgreSQL database and a volume
for recordings, already wired together. The console is set up afterwards, on
TalkWatch's own Console page.

!!! warning "Railway can't see the site's network"
    TalkWatch reads the console over its address on the site's network, and a
    Railway service runs in Railway's cloud. TalkWatch reaches the console
    through a private VPN it runs itself: the site gateway's own WireGuard VPN,
    or Tailscale. See [Reaching a console from elsewhere](remote-console.md).
    Don't forward the console's port to the internet instead.

## What the template makes

| Service | What it is |
|---|---|
| **Postgres** | Railway's PostgreSQL. TalkWatch builds its connection string from this service's variables, so there's no password to copy. |
| **TalkWatch** | `ghcr.io/thecodesaiyan/talkwatch:latest`, with a volume at `/data/audio` for copied recordings and voicemail, a public HTTPS domain, and `/healthz` as its health check. |

The database and the recordings each need a volume, and Railway's Free plan
allows only one per project, so the template needs the Hobby plan or above,
or a trial that hasn't ended. Railway says so before it deploys anything.

## Deploying

1. Choose **Deploy on Railway** from TalkWatch's README, and sign in to your
   Railway account.
2. Fill in `Site__Name`, `Site__Region` and `Site__TimeZone` for the site, as
   Railway asks. Everything else is filled in for you.
3. Deploy. TalkWatch creates its database and the first admin, and waits for a
   console.
4. Open the TalkWatch service's domain, and sign in as `admin` with the
   password in the service's `Bootstrap__AdminPassword` variable. The template
   generates it, so no two deployments share one. Change it once you're in,
   then delete the variable: it's only read while there are no users.
5. Under **Configure → Console**, give the console's address on the site's
   network, the account TalkWatch signs in with and the certificate
   fingerprint (steps 1 and 2 of [Installing](install.md)), and the way there,
   as [Reaching a console from elsewhere](remote-console.md) describes. Save,
   then **Test connection**.

The first poll then copies the console's whole history without alerting on
any of it, as on any first start.

## Two differences from a site install

**The audio volume runs TalkWatch as root.** Railway mounts volumes as root,
and the image runs as an unprivileged user, so the template sets
`RAILWAY_RUN_UID=0` or TalkWatch couldn't write recordings. The image still
has no shell or package manager.

**Forwarded headers aren't trusted.** Railway's edge proxy terminates HTTPS,
and its addresses aren't published, so `Proxy__TrustedNetworks` stays unset
(see [Behind a reverse proxy](proxy.md)). Until it can be set:

- every visitor looks like Railway's proxy, so the sign-in limit (ten attempts
  a minute per address) is shared by everyone signing in to that TalkWatch;
- sign-in through an identity provider is refused, because the redirect back
  comes to `http://`. Use passwords and two-factor sign-in.

## Upgrades

The TalkWatch service follows `latest`, as `compose.yaml` does. Turn on
automatic updates in the service's **Settings**, under **Source**, with a
maintenance window out of the site's working hours: Railway then redeploys
when a release moves `latest`. A pre-release never moves `latest`. To take only
fixes, change the image to the minor version, such as
`ghcr.io/thecodesaiyan/talkwatch:0.2`; see [Upgrading](upgrading.md).

A redeploy has a short gap, under two minutes, because a service with a volume
can't run two deployments at once. The console keeps its log, so TalkWatch
catches up on calls from the gap.

## Backups

A backup is the database and the audio together, as [Backups](backup.md) says.
On Railway, `railway connect Postgres --tunnel-only` opens a tunnel to the
database for `pg_dump`, and `railway volume files` reaches the audio volume.
