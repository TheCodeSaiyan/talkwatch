# Using TalkWatch's data elsewhere

TalkWatch's pages are for people. For everything else, a helpdesk that should
open a ticket for a voicemail, a spreadsheet the accounts team already keeps, a
Grafana board on the wall, there are three ways out, each going through the
same access as the pages: a webhook pushes alerts as they happen, the API reads
calls and figures, and an export hands over a date range in one file.

## Webhooks: alerts as they happen

A **Webhook** channel, under **Configure → Alert channels**, POSTs each alert a
flow sends it as JSON. Give it a signing secret and every body is signed, so the
receiver can check it came from TalkWatch:

| Header | Holds |
|---|---|
| `X-TalkWatch-Signature` | `sha256=` and the HMAC-SHA256 of the body, with the secret, as lowercase hex |
| `X-TalkWatch-Event` | the alert's type (`MissedCall`, `Voicemail` and so on), `Acknowledged` for the follow-up when someone acknowledges, or `Bundle` |
| `X-TalkWatch-Delivery` | the delivery's id, the same on every retry, so a receiver can drop repeats |

Check the signature over the exact bytes received, before parsing them. In
Python:

```python
import hashlib, hmac

def from_talkwatch(body: bytes, header: str, secret: str) -> bool:
    expected = "sha256=" + hmac.new(secret.encode(), body, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, header or "")
```

And in Node:

```js
import { createHmac, timingSafeEqual } from 'node:crypto';

function fromTalkWatch(body, header, secret) {
  const expected = Buffer.from('sha256=' + createHmac('sha256', secret).update(body).digest('hex'));
  const given = Buffer.from(header ?? '');
  return given.length === expected.length && timingSafeEqual(given, expected);
}
```

The body carries the call, links to acknowledge or snooze the alert, and, when
the flow's notify step asks for them, Talk's summary, the transcript and a link
to the voicemail. The full shape is under [Channels](../alerts.md#channels).
Deliveries that fail are retried after 1, 5 and 15 minutes, then an hour, then
every 6 hours, and given up after 8 tries, so a receiver that's down for a
restart misses nothing.

**Send test** on the channel sends one straight away, to check the receiver
before a real alert depends on it.

## The API: calls and figures on request

Make a token on your **Account** page. It's shown once; TalkWatch keeps only a
hash of it, so a lost one is revoked and replaced, not read back. It acts as
you: it reads exactly what you could read signed in, and nothing is writable
through it.

![Account: API tokens](../images/account-tokens.png)

```sh
# This week's calls, newest first, up to 500 a page.
curl -H "Authorization: Bearer $TOKEN" \
  "https://talkwatch.example/api/v1/calls?since=2026-09-28T00:00:00Z&pageSize=500"

# September's figures, as the Analytics page counts them.
curl -H "Authorization: Bearer $TOKEN" \
  "https://talkwatch.example/api/v1/stats?from=2026-09-01&to=2026-09-30"
```

`/api/v1/stats` returns the inbound count, answer rate, average answered call,
calls by outcome, by hour, by day and by line, for up to 92 whole days in the
site's time zone: enough for a wall board, or a monthly figure in a spreadsheet
that updates itself. Every endpoint is listed under [Export and
API](../api.md#endpoints).

## Exports: a date range in one file

**Export** on **Calls** downloads up to a year of calls as CSV or Parquet, with
one row a call:

`uuid`, `time_utc`, `time_local`, `direction`, `outcome`, `status`, `from`,
`from_e164`, `caller_name`, `to`, `to_e164`, `answered_by`, `duration_seconds`,
`has_recording`

`outcome` is TalkWatch's, worked out from what happened to the call (`Answered`,
`Missed`, `Voicemail`, `HungUpAtSwitchboard`, `OutsideVoicemail`,
`OutsideMissed`, `Outbound` and a few rarer ones), not Talk's `status`, which is
`accepted` for nearly everything. That column is most of the reason to export
from here rather than from Talk.

The CSV opens in Excel as it is. Parquet keeps the types, so nothing has to be
parsed, and suits DuckDB:

```sql
-- Missed calls by weekday, from an export.
SELECT dayname(time_utc) AS day, count(*) AS missed
FROM 'talkwatch-calls-2026-09-01-to-2026-09-30.parquet'
WHERE direction = 'in' AND outcome IN ('Missed', 'OutsideMissed')
GROUP BY day ORDER BY missed DESC;
```

Every export goes in the audit log, with who took it and what it covered.

## What none of them do

None of them change anything in Talk or TalkWatch: the API is read-only, and a
webhook only receives. Nor do they reach further than the person behind them.
A token or an export sees only that person's lines, and a channel given to one
person hears only of their calls, whatever a flow says.

## See also

- [Export and API](../api.md).
- [Alerts → Channels](../alerts.md#channels), for ntfy, email and Telegram.
- [Traces and metrics](../observability.md), for TalkWatch's own health.
