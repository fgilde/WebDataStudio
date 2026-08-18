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
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Backup and restore shell out to the engines' own dump tools. pg_dump refuses to dump a server
# newer than itself, so PostgreSQL's client comes from PostgreSQL's own repository rather than
# Debian's older package; mongodb-database-tools likewise. A missing tool is reported by the
# backup endpoint rather than crashing it, so a slimmer image still runs.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        default-mysql-client redis-tools ca-certificates curl gnupg \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
        | gpg --dearmor -o /usr/share/keyrings/pgdg.gpg \
    && echo "deb [signed-by=/usr/share/keyrings/pgdg.gpg] https://apt.postgresql.org/pub/repos/apt \
$(. /etc/os-release && echo $VERSION_CODENAME)-pgdg main" > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client-18 \
    && curl -fsSL https://pgp.mongodb.com/server-8.0.asc \
        | gpg --dearmor -o /usr/share/keyrings/mongodb.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/mongodb.gpg] \
https://repo.mongodb.org/apt/debian bookworm/mongodb-org/8.0 main" \
        > /etc/apt/sources.list.d/mongodb.list \
    && apt-get update \
    # MongoDB does not publish the tools for every architecture. A missing tool is reported by
    # the backup endpoint, so the image is still worth building without them.
    && (apt-get install -y --no-install-recommends mongodb-database-tools \
        || echo "mongodb-database-tools are not available for $(dpkg --print-architecture)") \
    && apt-get purge -y gnupg \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DB_PATH=/data/webdatastudio.db

EXPOSE 8080
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "WebDataStudio.Server.dll"]
