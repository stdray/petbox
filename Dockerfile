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
COPY src/YobaBox.Web/YobaBox.Web.csproj ./src/YobaBox.Web/
COPY src/YobaBox.Web/package.json src/YobaBox.Web/bun.lock ./src/YobaBox.Web/
RUN dotnet restore src/YobaBox.Web/YobaBox.Web.csproj -r linux-x64

COPY src/YobaBox.Core/ ./src/YobaBox.Core/
COPY src/YobaBox.Config/ ./src/YobaBox.Config/
COPY src/YobaBox.Log.Core/ ./src/YobaBox.Log.Core/
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
WORKDIR /app
COPY --from=build --chown=app:app /app/publish .
VOLUME ["/app/data"]
EXPOSE 8080
ENTRYPOINT ["./YobaBox.Web"]
