#!/usr/bin/env bash
# CI on this machine's Docker, in place of GitHub Actions. Runs what .github/workflows/ci.yml runs:
#
#   scripts/ci.sh            test the committed HEAD in a clean SDK container, build the image, and post the result
#                            to GitHub as the 'local-ci' status of that commit, so a pull request shows it
#   scripts/ci.sh --publish  the same, then push the image as the registry's 'edge' tag, and as 'live' and 'demo' for the
#                            two stacks; run it on main after a merge
#
# Only committed work is tested: HEAD is exported with git archive, so uncommitted edits and local bin/obj folders
# play no part, as on a hosted runner. Tests start PostgreSQL with Testcontainers on the same Docker, through its
# socket. The push uses Docker's own stored login for the registry; this script never handles credentials.
set -euo pipefail

# The maintainer's own registry, kept out of the repository: REGISTRY_HOST, or `git config talkwatch.registry` in the
# clone that publishes. Without one there's nothing to publish to, and the upgrade check starts from the last release.
registry="${REGISTRY_HOST:-$(git config --get talkwatch.registry || true)}"
publish=false
[ "${1:-}" = "--publish" ] && publish=true

cd "$(git rev-parse --show-toplevel)"
sha=$(git rev-parse HEAD)
short=${sha:0:7}
repo=$(gh repo view --json nameWithOwner -q .nameWithOwner)

# Git Bash rewrites /var/run/... into a Windows path; Docker needs it as written.
export MSYS_NO_PATHCONV=1

status() {
    gh api -X POST "repos/$repo/statuses/$sha" -f state="$1" -f context=local-ci -f description="$2" >/dev/null || true
}

fail() {
    status failure "$1"
    echo "local-ci: $1" >&2
    exit 1
}

status pending "Running on local Docker"
echo "local-ci: testing $short"

# The whole run is kept, so a failure can be read afterwards however the output was shown.
log="TestResults/local-ci.log"
mkdir -p TestResults

# The NuGet cache is kept in a volume between runs; everything else starts clean.
# The tests connect to each database container at its own address on Docker's bridge, where this container sits
# too. Through the ports Testcontainers publishes, reachable from here only at host.docker.internal, out by way of
# Windows and back, about one run in three a connection hung for the whole 60-second timeout and failed a test.
# Testcontainers' own reaper is still reached that way, once per test project.
git archive --format=tar HEAD | docker run --rm -i \
    -v /var/run/docker.sock:/var/run/docker.sock \
    -v talkwatch-ci-nuget:/root/.nuget/packages \
    -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal \
    -e TALKWATCH_TEST_DATABASES=container-address \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    sh -c 'mkdir /src && tar -x -C /src && cd /src \
        && dotnet build -c Release \
        && dotnet test -c Release --no-build \
        && dotnet run --project tools/TalkWatch.FixtureGuard -c Release --no-build -- .' 2>&1 \
    | tee "$log" \
    || fail "Tests failed at $short; see $log"

# The documentation site, strictly: a broken link or page fails the build. The image is pinned, since MkDocs 2.0 is
# announced as breaking themes and plugins.
echo "local-ci: building the documentation"
git archive --format=tar HEAD | docker run --rm -i --entrypoint sh squidfunk/mkdocs-material:9.7.7 \
    -c 'mkdir /site-src && tar -x -C /site-src && cd /site-src && mkdocs build --strict --site-dir /tmp/site' >> "$log" 2>&1 \
    || fail "The documentation did not build at $short; see $log"

echo "local-ci: building the image"
image="talkwatch:ci"
git archive --format=tar HEAD | docker build -q -t "$image" \
    --label "org.opencontainers.image.source=https://github.com/$repo" \
    --label "org.opencontainers.image.revision=$sha" \
    - >/dev/null \
    || fail "Image build failed at $short"

# The upgrade: the published edge image (the previous release) starts a clean install, then this build starts on the
# same database and must migrate it and come up healthy. UPGRADE_FROM names another image to start from, such as an
# older local build. The release workflow runs the same script against the last release.
echo "local-ci: checking the upgrade from the previous release"
previous="${UPGRADE_FROM:-${registry:+$registry/talkwatch/talkwatch:edge}}"
scripts/upgrade-check.sh "${previous:-ghcr.io/thecodesaiyan/talkwatch:latest}" "$image" "$log" | sed 's/^upgrade check: /local-ci: /' \
    || fail "The upgrade from the previous edge image failed at $short; see $log"

if $publish; then
    [ "$(git rev-parse --abbrev-ref HEAD)" = "main" ] || fail "--publish is for main; HEAD is on $(git rev-parse --abbrev-ref HEAD)"
    [ "$sha" = "$(git rev-parse origin/main)" ] || fail "--publish needs HEAD to be origin/main; pull or push first"
    [ -n "$registry" ] || fail "--publish needs a registry: set REGISTRY_HOST, or git config talkwatch.registry <host>"
    target="$registry/talkwatch/talkwatch:edge"
    docker tag "$image" "$target"
    docker push -q "$target" || fail "Push of $short to $registry failed"
    # The same image under a tag for each stack that runs it. Both stacks ran edge from one Docker host: whichever pulled
    # it first left the other comparing the registry with a tag already up to date, so that one never redeployed. Each
    # on its own tag sees its own change.
    for stack in live demo; do
        docker tag "$image" "$registry/talkwatch/talkwatch:$stack"
        docker push -q "$registry/talkwatch/talkwatch:$stack" || fail "Push of $short as $stack to $registry failed"
    done
    status success "Tested, built and pushed to $target, live and demo"
    echo "local-ci: pushed $short as $target, live and demo"
else
    status success "Tested and built on local Docker"
    echo "local-ci: $short passed"
fi
