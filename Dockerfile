# syntax=docker/dockerfile:1

# ---- build: SDK + Node (SPA build) + publish ------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build

RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY . .
RUN dotnet publish src/WebDataStudio.Server -c Release -o /app

# ---- runtime --------------------------------------------------------------
# P8 adds pg_dump, mysqldump, mongodump and redis-cli here for backup/restore.
# They stay out until that phase needs them.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DB_PATH=/data/webdatastudio.db

EXPOSE 8080
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "WebDataStudio.Server.dll"]
