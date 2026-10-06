# The SDK stage runs on the build machine's own architecture and cross-compiles for the target,
# so an arm64 image does not need a whole build under emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
ARG TARGETARCH
# The release's tag, such as v0.5.0, for the version the app reports; a local build says it is one. Not called VERSION:
# a build argument is in every RUN's environment, and MSBuild takes an environment variable named VERSION as the
# project's version, which then failed every restore without a word.
ARG TALKWATCH_VERSION=v0.0.0-local
# Restores are held to the lock files (CI=true): a package that changed, or would resolve differently, fails the build.
ENV CI=true
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
# Project files first, so the restore layer is reused until a dependency changes. This first restore isn't held to
# the lock files: .NET 10 adds a package for the Blazor scripts only once it sees the .razor files, which aren't here
# yet. The restore that counts comes with the publish below.
COPY src/TalkWatch.Core/TalkWatch.Core.csproj src/TalkWatch.Core/
COPY src/TalkWatch.Data/TalkWatch.Data.csproj src/TalkWatch.Data/
COPY src/TalkWatch.Replay/TalkWatch.Replay.csproj src/TalkWatch.Replay/
COPY src/TalkWatch.Web/TalkWatch.Web.csproj src/TalkWatch.Web/
RUN dotnet restore src/TalkWatch.Web -a $TARGETARCH -p:RestoreLockedMode=false

COPY src/ src/
# Restore again now the source is here, lock files and all, held to them: .NET 10 decides whether to pull in the Blazor
# framework scripts from the project's .razor files, which the restore above cannot see. Publishing with --no-restore
# shipped an image with no _framework/blazor.web.js. The check below fails the build if that ever happens again.
RUN dotnet publish src/TalkWatch.Web -c Release -a $TARGETARCH -o /app -p:Version=${TALKWATCH_VERSION#v} \
    && test -f /app/wwwroot/_framework/blazor.web.js

# Where copied recordings and voicemail live (Audio__Path). Made here because the runtime image has no shell, and
# handed to the app's user so it can write there; mount a volume over it to keep the audio.
RUN mkdir -p /data/audio

# The helpers for a console that isn't on TalkWatch's own network (Talk__Route): wireproxy, a userspace WireGuard client,
# and Tailscale's own tailscaled and tailscale. Built from source at pinned versions, static, for the target architecture,
# and run as the app's unprivileged user: neither needs root, a TUN device or a shell.
FROM --platform=$BUILDPLATFORM golang:1.27.1@sha256:1e93e00a31255c07e9a34c4207f3006e1501730c5323697cee7dfb827fdae44c AS tunnel
ARG TARGETARCH
ENV CGO_ENABLED=0 GOOS=linux GOARCH=$TARGETARCH GOFLAGS=-trimpath
RUN go install -ldflags="-s -w" github.com/windtf/wireproxy/cmd/wireproxy@v1.1.3 \
    && go install -ldflags="-s -w" tailscale.com/cmd/tailscaled@v1.104.0 tailscale.com/cmd/tailscale@v1.104.0 \
    && mkdir /out \
    # Cross-compiled binaries land in a folder named for their platform; native ones don't.
    && if [ -d "/go/bin/linux_$TARGETARCH" ]; then cp /go/bin/linux_$TARGETARCH/* /out/; else cp /go/bin/wireproxy /go/bin/tailscaled /go/bin/tailscale /out/; fi \
    && cp /go/pkg/mod/github.com/windtf/wireproxy@v1.1.3/LICENSE /out/LICENSE-wireproxy \
    && cp /go/pkg/mod/tailscale.com@v1.104.0/LICENSE /out/LICENSE-tailscale

# chiseled-extra: no shell or package manager, but it keeps ICU and tzdata, which time-zone-aware
# alert rules and locale formatting need.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:00e0ad6a7ef8c0c1391b87f05c7ac757a15740455688f2bfcd146a3f4b987efd
WORKDIR /app
COPY --from=build /app .
COPY --from=tunnel /out /app/tunnel
COPY --from=build --chown=$APP_UID:$APP_UID /data /data
# The pseudonymised fixtures, for demo mode (Demo__Enabled): fictional numbers and synthetic audio only.
COPY tests/fixtures/talk-5.3.2 /app/demo/talk-5.3.2
COPY tests/fixtures/talk-5.3.2-voicemail /app/demo/talk-5.3.2-voicemail
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TalkWatch.Web.dll"]
