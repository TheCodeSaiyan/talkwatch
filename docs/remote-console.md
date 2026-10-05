# Reaching a console from elsewhere

TalkWatch reads the console over its own network address. When TalkWatch runs
on that network, that's all there is to it. When it runs somewhere else, on a
cloud host such as [Railway](https://railway.com) or in another office, it
needs a private way onto the console's network. TalkWatch can make that way
itself, two ways:

- **The site gateway's WireGuard VPN.** Built into UniFi gateways, so free.
  The gateway needs a public address, or a router in front of it that forwards
  one UDP port to it.
- **Tailscale.** For a site whose internet connection has no public address
  (carrier-grade NAT, a WAN address from 100.64.0.0 to 100.127.255.255). Needs
  a machine on the site's network to act as a subnet router. Tailscale's free
  plan is for personal use only; a business needs a paid plan.

Either way, the console is never reached through Ubiquiti's cloud, and its
address stays its address on its own network: the certificate pin and host
name are exactly what they would be on the site. Nothing is forwarded to the
console from the internet.

Set it up on **Configure → Console**, as an admin, or with the `Talk__`
settings in [Configuration](configuration.md). What's saved on the page wins,
field by field. Saving reconnects straight away, and **Test connection** signs
in with what's saved and says which versions the console runs.

## The way it works

TalkWatch runs a small VPN client inside its own container, as its own
unprivileged user, with no extra permissions: wireproxy for WireGuard, or
Tailscale's own `tailscaled`. Either offers a proxy on the container's
loopback address that only TalkWatch's requests to the console go through.
Everything else TalkWatch sends (email, alerts, the database) goes the usual
way.

Everyone signed in sees in the rail when the VPN is down ("VPN down"), so
nobody wonders whether calls have stopped. Only admins see the details, on the
Console page.

## WireGuard, through the site's gateway

1. On the gateway, under the VPN settings, turn on the **WireGuard VPN
   server**, and add a client for TalkWatch.
2. If the site's public address changes, set up **Dynamic DNS** on the gateway
   and use that name as the client's server address. TalkWatch looks the name
   up again every few minutes, and reconnects when the site's address moves.
3. Add a firewall rule so the VPN client can reach only the console's address
   on port 443. By default a VPN client reaches the whole network.
4. Download the client's configuration file.
5. On TalkWatch's Console page, set the console's address on its own network,
   such as `https://192.168.1.1`, choose **The site gateway's WireGuard VPN**,
   and upload the file, or paste it, or type its settings in one by one.
6. Save, then **Test connection**.

The console's address has to be inside the client's allowed addresses; the
page says so if it isn't. Until the gateway answers, the Console page says
when it last did, and asks whether the UDP port is open to it.

## Tailscale

1. On a machine on the site's network that's always on, install Tailscale and
   [advertise the console's network as a subnet route](https://tailscale.com/kb/1019/subnets).
   Approve the route in the Tailscale admin console.
2. In the admin console, create an **OAuth client** with the `auth_keys` scope
   and a tag, such as `tag:talkwatch`. Its secret doesn't expire. An auth key
   works too, until it expires.
3. Let the tag reach the console's address on port 443 in the tailnet's access
   rules.
4. On TalkWatch's Console page, set the console's address on its own network,
   choose **Tailscale**, and give the OAuth client secret and the tag.
5. Save, then **Test connection**.

TalkWatch joins the tailnet as an ephemeral node called `talkwatch` each time
it starts, so old ones don't pile up, and Tailscale's own log upload is off.

## With environment variables

The same, without the page:

```yaml
    environment:
      Talk__ConsoleUrl: https://192.168.1.1
      Talk__Route: WireGuard
    secrets:
      - Talk__WireGuardConfig   # the client's .conf from the gateway, whole
```

For Tailscale, `Talk__Route: Tailscale`, with `Talk__TailscaleAuthKey` as a
secret and `Talk__TailscaleTags`. Keep the WireGuard config and the Tailscale
key in secret files: both let whoever holds them onto the site's network.
