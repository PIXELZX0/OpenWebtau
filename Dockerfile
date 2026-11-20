# The app compiles to static files (Blazor WebAssembly), so the build stage runs
# once on the build host's platform and only the nginx stage is per-architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
# emcc is a Python script; fetch-deps.sh needs curl, unzip and patch.
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 curl unzip patch \
 && rm -rf /var/lib/apt/lists/* \
 && dotnet workload install wasm-tools
WORKDIR /src
COPY native/*.sh native/
COPY vendor/OpenUtau/cpp vendor/OpenUtau/cpp
RUN native/fetch-deps.sh && native/build-worldline.sh
COPY . .
RUN dotnet publish src/OpenWebtau -c Release -o /out
# The headless host answers MCP calls with no editor tab open. Self-contained for
# the target architecture, so the runtime image needs no .NET install.
ARG TARGETARCH
RUN dotnet publish src/OpenWebtau.Headless -c Release --self-contained \
      -r linux-$([ "$TARGETARCH" = arm64 ] && echo arm64 || echo x64) -o /out/headless

# Debian rather than Alpine: the headless host's .NET runtime and Core's prebuilt
# libworldline.so are linked against glibc.
FROM nginx:1.27-bookworm
RUN apt-get update \
 && apt-get install -y --no-install-recommends nodejs \
 && rm -rf /var/lib/apt/lists/*
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY docker/40-data-dir.sh docker/50-mcp.sh /docker-entrypoint.d/
COPY mcp/server.mjs docker/auth.mjs /opt/openwebtau/
COPY --from=build /out/wwwroot /usr/share/nginx/html
COPY --from=build /out/headless /opt/openwebtau/headless
# Projects and voicebanks. Mount a volume here to keep them across containers.
VOLUME /data
EXPOSE 80
