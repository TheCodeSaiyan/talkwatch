# Configuration

<!-- Generated from the options classes in src/TalkWatch.Web/Services by ConfigurationReference; do not edit by hand. A test fails when this file and the code differ, and writes the new version. -->

TalkWatch is configured by environment variables, with `__` between a section and a setting: `Talk__ConsoleUrl` sets `ConsoleUrl` in the `Talk` section.

A setting can also come from a file in `/run/secrets` named after it, such as `/run/secrets/Talk__Password`, which overrides the environment. Use files for anything marked as a secret below; Docker and Compose secrets land there by default.

## Talk

The console TalkWatch reads from, and the way to it. Each of these but PollSeconds and CopyTranscripts can instead be set on the Console page, which wins, field by field.

| Setting | Default | Description |
|---|---|---|
| `Talk__ConsoleUrl` | none | The console's LAN address, such as https://10.0.0.1. Polling is off while this is unset. It must be https: the console's password goes only over TLS. Changing it on the Console page needs the password typed again, so a saved password never goes to an address it wasn't given for. |
| `Talk__Route` | `Direct` | How TalkWatch reaches the console: Direct, on the same network; WireGuard, through the site gateway's own WireGuard VPN server; or Tailscale, through a tailnet with a subnet router on the site's network. ConsoleUrl stays the console's LAN address whichever it is. |
| `Talk__WireGuardConfig` | none | For WireGuard: the client's .conf as the gateway's VPN server gives it out, whole. Its endpoint can be the site's dynamic DNS name, which TalkWatch looks up again every few minutes. **Secret:** give it as a file. |
| `Talk__TailscaleAuthKey` | none | For Tailscale: an auth key, or an OAuth client secret with the auth_keys scope, which does not expire. TalkWatch joins as an ephemeral node each time it starts. **Secret:** give it as a file. |
| `Talk__TailscaleTags` | none | For Tailscale: the tags TalkWatch's node advertises, comma-separated, such as tag:talkwatch. An OAuth client secret needs at least one. |
| `Talk__Username` | none | A console user with read-only access to Talk, signed in by username (not e-mail address). |
| `Talk__Password` | none | That user's password. **Secret:** give it as a file. |
| `Talk__CertificateSha256` | none | SHA-256 of the console's certificate. Consoles ship a self-signed certificate, so pinning it is how TalkWatch trusts the console without turning certificate checks off. |
| `Talk__PollSeconds` | `60` | Seconds between polls of the call log; the live feed brings calls between them. |
| `Talk__CopyTranscripts` | `true` | Copy the transcripts Talk makes of calls, when its AI transcription is on. They are readable only through a grant that allows transcripts. False leaves them on the console only. |

## Site

The one site 1.0 runs.

| Setting | Default | Description |
|---|---|---|
| `Site__Name` | `TalkWatch` | The site's name, used when it is first created. |
| `Site__Region` | `GB` | ISO 3166 region for numbers written nationally. |
| `Site__TimeZone` | `Europe/London` | IANA time zone for the dashboard, alert time windows and times in alert messages. |
| `Site__PublicUrl` | none | The address people reach TalkWatch at, such as https://talkwatch.example. Alerts link to it, and acknowledge and snooze links appear only when it is set. |

## Bootstrap

The first admin account, created at start-up when there are no users yet.

| Setting | Default | Description |
|---|---|---|
| `Bootstrap__AdminUsername` | none | The first admin's username. Ignored once anyone exists, so it can be removed after the first start. |
| `Bootstrap__AdminPassword` | none | The first admin's password, at least 12 characters. Ignored once anyone exists. **Secret:** give it as a file. |

## Database

The database password, kept apart from the connection string so that it can be a secret file.

| Setting | Default | Description |
|---|---|---|
| `Database__Password` | none | Added to ConnectionStrings__TalkWatch, which can then be written without it. **Secret:** give it as a file. |

## Audio

Where copied recordings and voicemail live.

| Setting | Default | Description |
|---|---|---|
| `Audio__Path` | `/data/audio` | The folder audio is copied to; mount a volume there, and back it up with the database. |

## Proxy

The reverse proxy in front of TalkWatch.

| Setting | Default | Description |
|---|---|---|
| `Proxy__TrustedNetworks` | none | The proxy's networks, comma-separated CIDRs such as 192.168.90.0/24. X-Forwarded-For and X-Forwarded-Proto are believed only from these, since from anyone else they would let a client pick its own address. Needed behind a proxy for the sign-in limit to tell visitors apart, and for sign-in through an identity provider. |

## SignIn

Limits on signing in, on top of locking an account after five wrong passwords.

| Setting | Default | Description |
|---|---|---|
| `SignIn__AttemptsPerMinute` | `10` | Sign-in attempts allowed per client address per minute. |

## Smtp

The mail server for email alerts and reports; each field can instead be set on the Alert channels page, which wins. Port 465 uses TLS from the first byte; any other port uses STARTTLS when StartTls says so. The ports for reading mail (993, 143, 995, 110) are refused, because a mail server never listens there and the attempt would only time out. TalkWatch greets the server by the host of Site__PublicUrl, or the From address's domain, never the machine's own name: in a container that is a bare id, which strict servers refuse as an invalid HELO name. With a username set and a server that offers sign-in only over TLS, a send fails saying so: turn StartTls on, or clear the username for a server that takes mail without signing in. A failed send is recorded on the alert or report copy and tried again; it never stops TalkWatch. Send test email, on the Alert channels page, sends one at once and shows the server's answer.

| Setting | Default | Description |
|---|---|---|
| `Smtp__Host` | none | The mail server's host name. |
| `Smtp__Port` | `587` | The mail server's port. |
| `Smtp__From` | none | The address alerts are sent from. |
| `Smtp__Username` | none | The username, if the server needs one. |
| `Smtp__Password` | none | The password, if the server needs one. **Secret:** give it as a file. |
| `Smtp__StartTls` | `true` | Whether to use STARTTLS, on any port but 465. |

## Telegram

Site-wide Telegram defaults. A Telegram channel uses its own bot token and chat id when it has them, and these when it does not; the alerts page can set them too, and wins.

| Setting | Default | Description |
|---|---|---|
| `Telegram__BotToken` | none | The bot token from @BotFather. **Secret:** give it as a file. |
| `Telegram__ChatId` | none | The chat to send to: a number such as -1001234567890, or @channelname. |

## Oidc

Sign-in through an OpenID Connect provider such as Authentik, alongside passwords. Set Oidc__Authority, Oidc__ClientId and Oidc__ClientSecret (a secret file in production) to turn it on. Groups decide access: the group mappings page gives groups roles and lines, Oidc__AdminGroups make someone an Admin whatever is mapped, and Oidc__ViewerGroups let someone in without setting a role (comma-separated names). Someone whose groups match nothing is turned away. Behind a reverse proxy, Proxy__TrustedNetworks must include the proxy, or the redirect back comes to http:// and the provider refuses it.

| Setting | Default | Description |
|---|---|---|
| `Oidc__Authority` | none | The provider's issuer, such as https://auth.example/application/o/talkwatch/. Sign-in through it is off while unset. |
| `Oidc__ClientId` | none | The client id registered with the provider. |
| `Oidc__ClientSecret` | none | The client secret registered with the provider. **Secret:** give it as a file. |
| `Oidc__DisplayName` | `Authentik` | What the sign-in button says: 'Sign in with ...'. |
| `Oidc__AdminGroups` | none | Groups whose members are Admins, comma-separated. Admin follows these at every sign-in. |
| `Oidc__ViewerGroups` | none | Groups whose members may sign in without a mapping setting their role: a new account comes in as a Viewer. Comma-separated. Anyone their groups match nothing for, here or on the group mappings page, is turned away. |
| `Oidc__GroupsClaim` | `groups` | The claim holding group names. |
| `Oidc__UsernameClaim` | `preferred_username` | The claim holding the username, matched to an existing account's on first sign-in. |
| `Oidc__EmailClaim` | `email` | The claim with the person's email: kept on their account at each sign-in, for reports and email alerts. |

## Demo

Demo mode: TalkWatch reads a console replayed from the fixtures that ship in the image, so it can be tried without one. Every number in them is fictional and every recording synthetic.

| Setting | Default | Description |
|---|---|---|
| `Demo__Enabled` | `false` | Run on the replayed console instead of a real one, with call times moved so the newest is an hour old. The live feed is off, and every page says the data is made up. |
| `Demo__Fixtures` | `/app/demo/talk-5.3.2` | The fixtures to replay: a folder of captured responses, and its '-voicemail' sibling if there is one. |
| `Demo__CallEveryMinutes` | `4` | How often a new call arrives on the replay, stamped now, so alerts fire and Now has something happening; 0 for none. Each kind of call comes in turn: missed, the same caller again, voicemail, answered, poor quality, hung up in the menu. |
| `Demo__Seed` | `true` | Set the demo up with something to look at: alerts in TalkWatch and example flows for the bootstrap admin, who is linked to someone in a ring group and carries an outside phone, as the guest does; people holding roles on the number; and two reports, each run once so there is a copy to open. Each part is set up only while there is none, so changes made in the demo stay, and a demo from before a part existed gets it at its next start. |
| `Demo__GuestUsername` | `guest` | A shared account for anyone trying the demo, shown on the sign-in page with a button that signs in as it. It is an admin, short of API tokens, so every page and button is there to try; what would change the site for whoever comes next (people, roles, roles on numbers, group mappings, retention, the mail and Telegram settings) is refused when saved, as is any channel but the browser's own. It can't turn on two-factor sign-in, and its password is set back to this one each start. Empty for no guest. |
| `Demo__ResetMinutes` | `30` | How often the demo starts over: every call, alert, flow, channel and report it has gathered is cleared and the example set up again, and the banner counts down to it. People and roles are kept, so nobody is signed out. 0 for never. |
| `Demo__GuestPassword` | `try-talkwatch` | The guest's password, published on the sign-in page: the data is made up, and nothing the guest changes outlasts the next start-over. **Secret:** give it as a file. |

## Not in a section

| Setting | Default | Description |
|---|---|---|
| `ConnectionStrings__TalkWatch` | required | The PostgreSQL connection string, such as `Host=db;Database=talkwatch;Username=talkwatch`. Its password can be left out and given as `Database__Password`. |
| `TALKWATCH_SECRETS_DIR` | `/run/secrets` | Where secret files are read from. An environment variable only, since it says where the other settings come from. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | none | Where to send traces and metrics, such as `http://otel-collector:4317`. Both are off while this is unset. The other standard `OTEL_` settings apply as usual. |
| `OTEL_SERVICE_NAME` | `talkwatch` | The service name traces and metrics carry. |
