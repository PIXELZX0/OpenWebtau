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

FROM nginx:1.27-alpine
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /out/wwwroot /usr/share/nginx/html
EXPOSE 80
