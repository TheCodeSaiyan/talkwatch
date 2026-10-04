# The SDK stage runs on the build machine's own architecture and cross-compiles for the target,
# so an arm64 image does not need a whole build under emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
# Project files first, so the restore layer is reused until a dependency changes.
COPY src/TalkWatch.Core/TalkWatch.Core.csproj src/TalkWatch.Core/
COPY src/TalkWatch.Data/TalkWatch.Data.csproj src/TalkWatch.Data/
COPY src/TalkWatch.Replay/TalkWatch.Replay.csproj src/TalkWatch.Replay/
COPY src/TalkWatch.Web/TalkWatch.Web.csproj src/TalkWatch.Web/
RUN dotnet restore src/TalkWatch.Web -a $TARGETARCH

COPY src/ src/
# Restore again now the source is here: .NET 10 decides whether to pull in the Blazor framework scripts from the
# project's .razor files, which the restore above cannot see. Publishing with --no-restore shipped an image with no
# _framework/blazor.web.js. The check below fails the build if that ever happens again.
RUN dotnet publish src/TalkWatch.Web -c Release -a $TARGETARCH -o /app \
    && test -f /app/wwwroot/_framework/blazor.web.js

# Where copied recordings and voicemail live (Audio__Path). Made here because the runtime image has no shell, and
# handed to the app's user so it can write there; mount a volume over it to keep the audio.
RUN mkdir -p /data/audio

# chiseled-extra: no shell or package manager, but it keeps ICU and tzdata, which time-zone-aware
# alert rules and locale formatting need.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
COPY --from=build --chown=$APP_UID:$APP_UID /data /data
# The pseudonymised fixtures, for demo mode (Demo__Enabled): fictional numbers and synthetic audio only.
COPY tests/fixtures/talk-5.3.2 /app/demo/talk-5.3.2
COPY tests/fixtures/talk-5.3.2-voicemail /app/demo/talk-5.3.2-voicemail
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TalkWatch.Web.dll"]
