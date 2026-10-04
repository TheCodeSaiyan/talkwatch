#!/usr/bin/env bash
# The upgrade check, shared by scripts/ci.sh and the release workflow:
#
#   scripts/upgrade-check.sh PREVIOUS CURRENT [LOG]
#
# PREVIOUS (the image last published) starts a clean install on environment variables alone, then CURRENT starts on the
# same database, migrating it, and must come up healthy with no errors logged. A PREVIOUS that can't be pulled is
# skipped, not failed, since the very first release has nothing to upgrade from. What went wrong is appended to LOG,
# or printed when there's none.
set -uo pipefail

previous="$1"
current="$2"
log="${3:-/dev/stderr}"

# Pulled first, so a stale local copy of a moving tag isn't what gets tested; a local-only image (an older build named
# with UPGRADE_FROM) is used as it is.
if ! docker pull -q "$previous" >/dev/null 2>&1 && ! docker image inspect "$previous" >/dev/null 2>&1; then
    echo "upgrade check: no previous image to upgrade from ($previous); skipped"
    exit 0
fi

run="talkwatch-upgrade-$$"
env=(-e "ConnectionStrings__TalkWatch=Host=$run-db;Database=talkwatch;Username=postgres;Password=ci-only"
    -e Bootstrap__AdminUsername=admin -e "Bootstrap__AdminPassword=ci only admin password")
docker network create "$run" >/dev/null
trap 'docker rm -f "$run-app" "$run-db" >/dev/null 2>&1; docker network rm "$run" >/dev/null 2>&1' EXIT
docker run -d --name "$run-db" --network "$run" -e POSTGRES_PASSWORD=ci-only -e POSTGRES_DB=talkwatch postgres:17-alpine >/dev/null
until docker exec "$run-db" pg_isready -U postgres -d talkwatch >/dev/null 2>&1; do sleep 1; done

for tag in "$previous" "$current"; do
    docker rm -f "$run-app" >/dev/null 2>&1 || true
    docker run -d --name "$run-app" --network "$run" -p 127.0.0.1::8080 "${env[@]}" "$tag" >/dev/null
    port=$(docker port "$run-app" 8080 | head -1 | sed 's/.*://')
    healthy=false
    for _ in $(seq 1 90); do
        if curl -fsS "http://127.0.0.1:$port/healthz" >/dev/null 2>&1; then healthy=true; break; fi
        sleep 1
    done
    if ! $healthy; then
        echo "upgrade check: $tag did not come up healthy" >> "$log"
        docker logs "$run-app" 2>&1 | tail -20 >> "$log"
        exit 1
    fi

    if [ "$tag" = "$current" ] && docker logs "$run-app" 2>&1 | grep -qE '^(fail|crit):'; then
        echo "upgrade check: $tag logged errors" >> "$log"
        docker logs "$run-app" 2>&1 | grep -E -A5 '^(fail|crit):' >> "$log"
        exit 1
    fi
done

applied=$(docker logs "$run-app" 2>&1 | grep -c "Applying migration" || true)
echo "upgrade check: upgraded from $previous cleanly, applying $applied migrations"
