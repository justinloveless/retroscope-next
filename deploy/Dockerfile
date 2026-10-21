# Built in CI only (DESIGN §i.1, F1); the VPS pulls the result by digest. Build context: repository root.

FROM node:22-alpine@sha256:0a7108bf6c7bf5de370ffb1a3ed6be93d405b43ff159f681a8d18c0e2bc2e402 AS web
WORKDIR /src
COPY package.json package-lock.json ./
COPY web/package.json web/
RUN npm ci --no-audit --no-fund --workspace web --include-workspace-root=false
COPY web/ web/
RUN node web/build.mjs

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
ARG TARGETARCH
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY db/ db/
COPY src/Retroscope.Core/ src/Retroscope.Core/
COPY src/Retroscope.Server/ src/Retroscope.Server/
RUN case "$TARGETARCH" in arm64) rid=linux-arm64 ;; *) rid=linux-x64 ;; esac \
    && dotnet publish src/Retroscope.Server -c Release -r "$rid" -p:PublishAot=true -p:StripSymbols=true -o /out

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled@sha256:8d23bb82cd8345ee51738b36c6da8e2f89ded7a547bc25fa52d5cc60e37f6d7a
WORKDIR /app
COPY --from=build /out/retroscope /app/retroscope
COPY --from=web /src/web/dist /app/wwwroot
USER 1654
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_hostBuilder__reloadConfigOnChange=false
EXPOSE 8080
ENTRYPOINT ["/app/retroscope"]
