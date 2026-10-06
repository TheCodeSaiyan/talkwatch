# Backing up and restoring TalkWatch

TalkWatch keeps three things, and a backup needs all of them:

1. **The PostgreSQL database**: calls, lines, people, grants, alerts, settings, and the keys that encrypt saved credentials and sign alert links and sign-ins. Without those keys, saved secrets and outstanding links stop working, so back up the database rather than recreating it.
2. **The audio folder** (`Audio__Path`, `/data/audio` in the image): the copied recordings and voicemail. The database indexes each file with its SHA-256.
3. **The key those keys are encrypted with.** It's never in the database, so a copy of the database alone, a stray dump say, reads none of the saved passwords and tokens. Given as the `DataProtection__Key` secret, it's yours to keep with your other secrets. Otherwise TalkWatch made it on first start, in `.keys/data-protection.key` in the audio folder, and the audio backup below carries it. Lose it and the saved passwords on the Console and alert pages have to be typed again, and everyone signs in again; nothing else is lost.

Nothing else is needed. Settings given as environment variables or secret files belong to your deployment, not to TalkWatch's backup.

Keeping the database backup and the key apart is what keeps the credentials safe in a backup: anyone with both can read them, so store them in different places, or give the key as a secret and keep it with your other secrets.

## Taking a backup

With Docker Compose, using the names in the bundled `compose.yaml` (the `db` service, the `talkwatch` database user, and the `talkwatch_audio` volume):

```sh
# The database, in PostgreSQL's custom format (compressed, and restorable table by table).
docker compose exec -T db pg_dump -U talkwatch -Fc talkwatch > talkwatch-$(date +%F).dump

# The audio folder.
docker run --rm -v talkwatch_audio:/data/audio:ro -v "$PWD":/backup alpine \
    tar -czf /backup/talkwatch-audio-$(date +%F).tar.gz -C /data audio
```

Take both close together, database first. They do not have to be taken at the same instant: see below for why a gap of a few minutes is fine.

Any tool that backs up PostgreSQL and a Docker volume will do (restic, Proxmox Backup Server, a nightly cron job). What matters is that each night's database and audio are kept together.

## Restoring

1. Stop TalkWatch, so that nothing writes while the restore runs: `docker compose stop talkwatch`.
2. Restore the database into an empty one:
   ```sh
   docker compose exec -T db dropdb -U postgres --if-exists talkwatch
   docker compose exec -T db createdb -U postgres -O talkwatch talkwatch
   docker compose exec -T db pg_restore -U talkwatch --no-owner -d talkwatch < talkwatch-2026-09-30.dump
   ```
   On an install made before `compose.yaml` had a `postgres` superuser, use `-U talkwatch` in the first two lines instead.
3. Restore the audio folder into the volume:
   ```sh
   docker run --rm -v talkwatch_audio:/data/audio -v "$PWD":/backup alpine \
       sh -c 'find /data/audio -mindepth 1 -delete && tar -xzf /backup/talkwatch-audio-2026-09-30.tar.gz -C /data'
   ```
   The volume is mounted where the backup took it from, so the files come back where they were; what was there before is emptied first.
4. Start TalkWatch: `docker compose start talkwatch`. It applies any newer migrations, then checks every copied file is where the database says. It logs the result.
5. On **Retention** under Admin, press **Check audio**. This reads every file against its checksum, which start-up skips because it is slow on a large store. The same check runs from the command line, and exits 0 when all is well:
   ```sh
   docker compose run --rm talkwatch check-audio
   ```

## Why a few minutes' gap does no harm

The database and the audio folder are rarely backed up at the same instant, so after a restore they can disagree slightly:

- **A file the database expects is missing, or no longer matches its checksum.** Perhaps the retention sweep removed it between the two backups, or the file backup was taken first. The check marks it to be copied again, and the next poll fetches it from the console. That works while the console still has the recording; consoles prune old audio, which is why TalkWatch copies it in the first place.
- **A file nothing refers to.** It was copied after the database backup was taken. The check reports it and leaves it. Its call comes back from the console at the next poll, and the copy is then referred to again.

So restore the database and the audio from the same night, and let the check put right whatever happened between the two.

## Before an upgrade

TalkWatch migrates its database when a new version starts. Migrations are tested against the previous version's schema, but take a backup first anyway: it is the only way back if something goes wrong.
