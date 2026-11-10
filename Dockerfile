FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /build
COPY . .
RUN ./build.sh && chmod -R a+rX out/.playwright && chmod a+rx out/.playwright/node/*/node
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble
USER root
# yt-dlp needs updates every few weeks and the Ubuntu package is a 2024 build, so the standalone release binary is
# pinned here with its hash from the release's SHA2-256SUMS. Bump both ARGs in one commit; the hash stays in the
# repository on purpose, because a sums file fetched at build time would prove nothing.
ARG YTDLP_VERSION=2026.10.26
ARG YTDLP_SHA256=767ba9dce7052aa196f8b12652f433d76de128ba168966fa84b6394d5e6c6795
RUN apt-get update && apt-get install -y --no-install-recommends curl xvfb x11vnc novnc websockify ffmpeg rsync sqlite3 fonts-noto fonts-noto-color-emoji && rm -rf /var/lib/apt/lists/* \
 && curl -fsSL -o /usr/local/bin/yt-dlp "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/yt-dlp_linux" \
 && echo "${YTDLP_SHA256}  /usr/local/bin/yt-dlp" | sha256sum -c - && chmod 0755 /usr/local/bin/yt-dlp
WORKDIR /app
COPY --from=build /build/out/ ./
COPY tools/novnc.sh tools/novnc.sh
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN dotnet Feed.Cli.dll browser install-deps && dotnet Feed.Cli.dll browser install && chmod -R a+rX /ms-playwright
# The image refuses to build without the tools that processing spawns; a missing one otherwise fails every item at run time.
RUN yt-dlp --version && ffmpeg -version
RUN mkdir /data && chown app:app /data
USER app
ENV FEED_DATA=/data ASPNETCORE_URLS=http://0.0.0.0:8000
EXPOSE 8000 6901
ENTRYPOINT ["dotnet","Feed.Web.dll"]
