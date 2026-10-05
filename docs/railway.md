# Deploying on Railway

TalkWatch can run on [Railway](https://railway.com) rather than on a machine
at the site, which suits an installer looking after several sites as well as a
site with nowhere to run a container. The TalkWatch template deploys into your
own Railway account: the published image, a PostgreSQL database and a volume
for recordings, already wired together.

!!! warning "Railway can't see the site's LAN"
    TalkWatch reads the console over its LAN address. A Railway service runs in
    Railway's cloud, so it needs a private route to the console: a Tailscale
    tailnet that includes the site's LAN, bridged into the Railway project
    by Railtail (see [Reaching the console](#reaching-the-console)). Don't
    forward the console's port to the internet instead.

    TalkWatch has been run against a console on the same LAN, not yet through
    Railtail. If the console refuses it there, please
    [open an issue](https://github.com/TheCodeSaiyan/talkwatch/issues).

## What the template makes

| Service | What it is |
|---|---|
| **Postgres** | Railway's PostgreSQL. TalkWatch builds its connection string from this service's variables, so there's no password to copy. |
| **TalkWatch** | `ghcr.io/thecodesaiyan/talkwatch:latest`, with a volume at `/data/audio` for copied recordings and voicemail, a public HTTPS domain, and `/healthz` as its health check. |
| **Railtail** | Forwards one port from the Railway project to the console through your tailnet. |

## Deploying

1. Choose **Deploy on Railway** from TalkWatch's README, and sign in to your
   Railway account.
2. Fill in the variables Railway asks for (all are described in
   [Configuration](configuration.md)):
    - `Site__Name`, `Site__Region` and `Site__TimeZone` for the site.
    - `Talk__Username`, `Talk__Password` and `Talk__CertificateSha256`, as in
      steps 1 and 2 of [Installing](install.md). Get the fingerprint on the
      site's LAN, where you can check it against what the browser shows.
    - Railtail's `TARGET_ADDR`, `TS_AUTH_KEY` and `TS_HOSTNAME` (see below).
3. Deploy. TalkWatch migrates the database, creates the first admin and starts
   polling. The first poll copies the console's whole history without alerting
   on any of it, as on any first start.
4. Open the TalkWatch service's domain, and sign in as `admin` with the
   password in the service's `Bootstrap__AdminPassword` variable. The template
   generates it, so no two deployments share one. Change it once you're in,
   then delete the variable: it's only read while there are no users.

## Reaching the console

Two Tailscale pieces make the route:

- **On the site's LAN**, a Tailscale subnet router that advertises the
  console's network, with the route approved in the Tailscale admin console.
  Any always-on machine on that LAN can be it.
- **In the Railway project**, Railtail, joined to the same tailnet. It listens
  on `LISTEN_PORT` on Railway's private network and forwards each connection
  to `TARGET_ADDR`.

Set Railtail's variables:

| Variable | Value |
|---|---|
| `TARGET_ADDR` | The console's LAN address and HTTPS port, such as `192.168.1.1:443`. |
| `LISTEN_PORT` | `41641`, as the template has it. |
| `TS_AUTH_KEY` | A Tailscale auth key for the site's tailnet. |
| `TS_HOSTNAME` | A name for Railtail in the tailnet, such as `talkwatch-acme`. |

The template points TalkWatch at Railtail rather than at the console:

```text
Talk__ConsoleUrl=https://${{Railtail.RAILWAY_PRIVATE_DOMAIN}}:${{Railtail.LISTEN_PORT}}
```

The live feed and the API share that one port, so nothing else needs
forwarding. The certificate pin still holds: TalkWatch checks the certificate
by its fingerprint, so the console is trusted under Railtail's name only if
it's the certificate you pinned.

Railway's guide to Railtail, with the Tailscale side in more detail, is
[Bridge Railway to RDS with Tailscale](https://github.com/railwayapp/docs/blob/main/content/guides/bridge-railway-to-rds-with-tailscale.md).

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
