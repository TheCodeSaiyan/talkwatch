# Behind a reverse proxy

TalkWatch serves plain HTTP on port 8080 and leaves TLS to a reverse proxy in front of it. Five things matter whichever proxy you use.

## What the proxy has to get right

**WebSockets.** Parts of every page talk to the server over a WebSocket at `/_blazor`: the rail's counts and its pop-ups (unread alerts, who's free, the console's state) on every page, and the whole of **Now**, **Operator** and the flow editor, including its **Try it on the last 7 days** button. A proxy that doesn't pass the upgrade through leaves pages loaded but frozen: the rail stops counting, Now and Operator stop moving, and the flow editor won't take a click. Traefik and Caddy pass WebSockets without being asked; nginx needs the `Upgrade` and `Connection` headers set.

**Forwarded headers, from the proxy only.** Set `Proxy__TrustedNetworks` to the network the proxy reaches TalkWatch from, such as `192.168.90.0/24`. TalkWatch then believes `X-Forwarded-For` and `X-Forwarded-Proto` from there and from nowhere else. Without it, two things go wrong:

- every visitor looks like the proxy, so the sign-in limit (ten attempts a minute per address) is shared by everyone, and one person guessing passwords locks the rest out for a minute;
- TalkWatch thinks requests arrived over `http://`, so sign-in through an identity provider sends people back to an `http://` address, which the provider refuses.

Trusting forwarded headers from anyone would let a visitor choose their own address, which is why it's a list, not a switch.

**The public address.** Set `Site__PublicUrl` to the address people use, such as `https://talkwatch.example`. Alerts link back to it, and acknowledge and snooze links only appear when it's set.

**Only the proxy reaches the plain HTTP.** `compose.yaml` publishes port 8080 on every address the host has, so a first run works from anywhere on the LAN. With the proxy on the same host, set `TALKWATCH_BIND=127.0.0.1` in `.env`, so nothing but the proxy can reach TalkWatch without TLS; with the proxy on another host, let only it through a firewall.

**Alert links past any sign-in gate.** If the proxy puts a sign-in in front of every page (Authentik or Authelia forward auth, for instance), let `/a/` through without it. ntfy's **Acknowledge** button posts to `/a/…` from a phone that holds no session with your gate. Each link carries its own signed token for one alert, valid for seven days, and that token is the permission.

## Traefik

Labels on the `talkwatch` service in `compose.yaml`, with Traefik on the same Docker network:

```yaml
    labels:
      - traefik.enable=true
      - traefik.http.routers.talkwatch.rule=Host(`talkwatch.example`)
      - traefik.http.routers.talkwatch.entrypoints=https
      - traefik.http.routers.talkwatch.tls.certresolver=letsencrypt
      - traefik.http.services.talkwatch.loadbalancer.server.port=8080
    environment:
      Proxy__TrustedNetworks: 172.18.0.0/16   # the Docker network Traefik shares with TalkWatch
      Site__PublicUrl: https://talkwatch.example
```

With a forward-auth middleware on the main router, add a second router for `/a/` without it, at a higher priority:

```yaml
      - traefik.http.routers.talkwatch-links.rule=Host(`talkwatch.example`) && PathPrefix(`/a/`)
      - traefik.http.routers.talkwatch-links.priority=15
      - traefik.http.routers.talkwatch-links.entrypoints=https
      - traefik.http.routers.talkwatch-links.tls.certresolver=letsencrypt
```

## Caddy

```caddy
talkwatch.example {
    reverse_proxy talkwatch:8080
}
```

Caddy gets a certificate, passes WebSockets and sets the forwarded headers by itself. Set `Proxy__TrustedNetworks` to the network Caddy reaches TalkWatch on.

## Without a proxy

On a LAN, for a first run, the published port works as it is. Don't expose it beyond the LAN: sign-in cookies and passwords would cross the network in the clear.
