# Export and API

Both give you the calls you can see and nothing else: the same grants and roles on numbers apply as on the calls page.

The number chosen in the rail's switcher doesn't apply to a token. It's for your pages, and a script whose answers changed with whatever you last looked at in the browser couldn't be relied on, so a token always sees every call you're granted. The CSV and Parquet exports below are downloaded from the calls page, so they do follow it.

## CSV export

On **Calls**, choose a date range (up to a year) and download it. The file starts with a byte order mark, so Excel reads it as UTF-8, and every export goes into the audit log.

Caller names come from outside, and a spreadsheet runs a cell starting `=`, `+`, `-` or `@` as a formula. TalkWatch puts a `'` in front of such text, so a caller called `=HYPERLINK(...)` stays text. A plain phone number such as `+441144960042` is left alone.

## Parquet export

The same calls, with the same columns, also come as Parquet, for tools that read it (DuckDB, pandas, Power BI and others). The columns are typed: `time_utc` is a timestamp, `duration_seconds` a number and `has_recording` true or false, so nothing has to be parsed from text. It's audited like the CSV. A Parquet file isn't opened as spreadsheet cells, so it isn't given the formula guard.

## API tokens

Make a token on your **Account** page. It's shown once; TalkWatch keeps only its SHA-256, so a lost token can't be read back, only revoked and replaced. The page shows when each token was last used. An admin can see and revoke anyone's tokens from their page, and a locked account's tokens stop working with it.

A token acts as you. It reads exactly what you could read signed in, and nothing is writable through it.

## Endpoints

Send the token as `Authorization: Bearer tw_…`. The API doesn't accept the sign-in cookie, only tokens.

| Endpoint | Returns |
|---|---|
| `GET /api/v1/calls?since=&until=&page=&pageSize=` | Calls, newest first. `since` and `until` are ISO 8601 instants, the last seven days by default; up to 500 a page. Each call lists the lines you can see on it. |
| `GET /api/v1/calls/{uuid}` | One call, with its events. `404` if it's not one of yours. |
| `GET /api/v1/lines` | The lines you can see. |
| `GET /api/v1/stats?from=yyyy-MM-dd&to=yyyy-MM-dd` | The dashboard's figures for whole days in the site's time zone, up to 92 days. |

```sh
curl -H "Authorization: Bearer $TOKEN" "https://talkwatch.example/api/v1/calls?since=2026-09-01T00:00:00Z"
```
