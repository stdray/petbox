# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update \
	&& apt-get install -y --no-install-recommends curl unzip \
	&& rm -rf /var/lib/apt/lists/*
RUN curl -fsSL https://bun.sh/install | bash
ENV PATH="/root/.bun/bin:$PATH"

WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Directory.Build.targets ./
COPY src/PetBox.Core/PetBox.Core.csproj ./src/PetBox.Core/
COPY src/PetBox.Config/PetBox.Config.csproj ./src/PetBox.Config/
COPY src/PetBox.Log.Core/PetBox.Log.Core.csproj ./src/PetBox.Log.Core/
COPY src/PetBox.Data/PetBox.Data.csproj ./src/PetBox.Data/
COPY src/PetBox.Dashboard/PetBox.Dashboard.csproj ./src/PetBox.Dashboard/
COPY src/PetBox.Tasks/PetBox.Tasks.csproj ./src/PetBox.Tasks/
COPY src/PetBox.Memory/PetBox.Memory.csproj ./src/PetBox.Memory/
COPY src/PetBox.Sessions/PetBox.Sessions.csproj ./src/PetBox.Sessions/
COPY src/PetBox.LlmRouter.Contract/PetBox.LlmRouter.Contract.csproj ./src/PetBox.LlmRouter.Contract/
COPY src/PetBox.LlmRouter/PetBox.LlmRouter.csproj ./src/PetBox.LlmRouter/
COPY src/PetBox.Web/PetBox.Web.csproj ./src/PetBox.Web/
COPY src/PetBox.Web/package.json src/PetBox.Web/bun.lock ./src/PetBox.Web/
RUN dotnet restore src/PetBox.Web/PetBox.Web.csproj -r linux-x64

COPY src/PetBox.Core/ ./src/PetBox.Core/
COPY src/PetBox.Config/ ./src/PetBox.Config/
COPY src/PetBox.Log.Core/ ./src/PetBox.Log.Core/
COPY src/PetBox.Data/ ./src/PetBox.Data/
COPY src/PetBox.Dashboard/ ./src/PetBox.Dashboard/
COPY src/PetBox.Tasks/ ./src/PetBox.Tasks/
COPY src/PetBox.Memory/ ./src/PetBox.Memory/
COPY src/PetBox.Sessions/ ./src/PetBox.Sessions/
COPY src/PetBox.LlmRouter.Contract/ ./src/PetBox.LlmRouter.Contract/
COPY src/PetBox.LlmRouter/ ./src/PetBox.LlmRouter/
COPY src/PetBox.Web/ ./src/PetBox.Web/
RUN dotnet publish src/PetBox.Web/PetBox.Web.csproj \
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
# volume here: `docker run ... -v /opt/petbox/data:/app/data ...` so state survives
# container restarts.
VOLUME ["/app/data"]
EXPOSE 8080
ENTRYPOINT ["./PetBox.Web"]
