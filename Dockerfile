# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update \
	&& apt-get install -y --no-install-recommends curl unzip \
	&& rm -rf /var/lib/apt/lists/*
RUN curl -fsSL https://bun.sh/install | bash
ENV PATH="/root/.bun/bin:$PATH"

WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Directory.Build.targets ./
COPY src/YobaBox.Core/YobaBox.Core.csproj ./src/YobaBox.Core/
COPY src/YobaBox.Config/YobaBox.Config.csproj ./src/YobaBox.Config/
COPY src/YobaBox.Log.Core/YobaBox.Log.Core.csproj ./src/YobaBox.Log.Core/
COPY src/YobaBox.Data/YobaBox.Data.csproj ./src/YobaBox.Data/
COPY src/YobaBox.Dashboard/YobaBox.Dashboard.csproj ./src/YobaBox.Dashboard/
COPY src/YobaBox.Web/YobaBox.Web.csproj ./src/YobaBox.Web/
COPY src/YobaBox.Web/package.json src/YobaBox.Web/bun.lock ./src/YobaBox.Web/
RUN dotnet restore src/YobaBox.Web/YobaBox.Web.csproj -r linux-x64

COPY src/YobaBox.Core/ ./src/YobaBox.Core/
COPY src/YobaBox.Config/ ./src/YobaBox.Config/
COPY src/YobaBox.Log.Core/ ./src/YobaBox.Log.Core/
COPY src/YobaBox.Data/ ./src/YobaBox.Data/
COPY src/YobaBox.Dashboard/ ./src/YobaBox.Dashboard/
COPY src/YobaBox.Web/ ./src/YobaBox.Web/
RUN dotnet publish src/YobaBox.Web/YobaBox.Web.csproj \
	-c Release \
	-r linux-x64 \
	--self-contained \
	--no-restore \
	-o /app/publish

RUN mkdir -p /app/publish/data

FROM mcr.microsoft.com/dotnet/nightly/runtime-deps:10.0-noble-chiseled AS final
ARG APP_VERSION
ARG GIT_SHORT_SHA
ARG GIT_COMMIT_DATE
ENV APP_VERSION=$APP_VERSION
ENV GIT_SHORT_SHA=$GIT_SHORT_SHA
ENV GIT_COMMIT_DATE=$GIT_COMMIT_DATE
ENV ASPNETCORE_URLS=http://+:8080
# Persist ASP.NET DataProtection keys on the mounted data volume so cookies and
# antiforgery tokens survive container restarts. The keys dir sits alongside the
# SQLite file under /app/data (chowned to app:app via the COPY --chown below).
# Locally or in tests — env unset → default in-memory keyring kicks in, no file I/O.
ENV DataProtection__KeysDirectory=/app/data/keys
WORKDIR /app
COPY --from=build --chown=app:app /app/publish .
# `data/` holds the SQLite file (Workspaces, Projects, Services, ApiKeys, etc.) +
# DataProtection keys + per-project Log/Config/Data DB files. In prod mount a host
# volume here: `docker run ... -v /opt/yobabox/data:/app/data ...` so state survives
# container restarts.
VOLUME ["/app/data"]
EXPOSE 8080
ENTRYPOINT ["./YobaBox.Web"]
