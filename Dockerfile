FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /build
COPY . .
RUN ./build.sh && chmod -R a+rX out/.playwright && chmod a+rx out/.playwright/node/*/node
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble
USER root
RUN apt-get update && apt-get install -y --no-install-recommends xvfb x11vnc novnc websockify ffmpeg yt-dlp rsync sqlite3 fonts-noto fonts-noto-color-emoji && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /build/out/ ./
COPY tools/novnc.sh tools/novnc.sh
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN dotnet Feed.Cli.dll browser install-deps && dotnet Feed.Cli.dll browser install && chmod -R a+rX /ms-playwright
RUN mkdir /data && chown app:app /data
USER app
ENV FEED_DATA=/data ASPNETCORE_URLS=http://0.0.0.0:8000
EXPOSE 8000 6901
ENTRYPOINT ["dotnet","Feed.Web.dll"]
