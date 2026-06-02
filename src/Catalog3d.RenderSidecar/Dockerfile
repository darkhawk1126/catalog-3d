# Build stage — publish Catalog3d.RenderSidecar
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and all project files first for layer-cached restore.
COPY Catalog3d.slnx ./
COPY src/Catalog3d.Domain/Catalog3d.Domain.csproj                 src/Catalog3d.Domain/
COPY src/Catalog3d.Application/Catalog3d.Application.csproj       src/Catalog3d.Application/
COPY src/Catalog3d.Infrastructure/Catalog3d.Infrastructure.csproj src/Catalog3d.Infrastructure/
COPY src/Catalog3d.Web/Catalog3d.Web.csproj                       src/Catalog3d.Web/
COPY src/Catalog3d.RenderSidecar/Catalog3d.RenderSidecar.csproj   src/Catalog3d.RenderSidecar/
COPY tests/Catalog3d.Tests/Catalog3d.Tests.csproj                 tests/Catalog3d.Tests/

RUN dotnet restore src/Catalog3d.RenderSidecar/Catalog3d.RenderSidecar.csproj

COPY . .
RUN dotnet publish src/Catalog3d.RenderSidecar/Catalog3d.RenderSidecar.csproj \
    --no-restore \
    -c Release \
    -o /app/publish

# Runtime stage — mcr.microsoft.com/dotnet/aspnet:10.0 is Ubuntu 24.04 (noble).
# We layer stl-thumb + its OSMesa runtime deps on top for headless STL→PNG rendering
# without a GPU or display server.
#
# Rendering approach: stl-thumb 0.5.0 + OSMesa software-GL fallback
#   stl-thumb (glutin/winit) tries Wayland → X11 → surfaceless EGL → OSMesa in order.
#   In a no-GPU, no-display container the first three all fail; OSMesa catches it via
#   libOSMesa.so (dlopen'd at runtime). Exit code is 0 and a valid PNG is written even
#   though winit emits a panic-style log on stderr before the fallback succeeds — this
#   is a known stl-thumb v0.5.0 quirk; the render result is correct.
#
#   Verified: 512×512 PNG (6 528 bytes) from a binary-STL cube; exit code 0.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install stl-thumb and its headless (OSMesa) runtime dependencies.
#
# libosmesa6   — Mesa software-GL offscreen renderer (OSMesaCreateContext family).
#                stl-thumb dlopen's "libOSMesa.so" (unversioned); libosmesa6 ships
#                libOSMesa.so.8 only, so we create the unversioned symlink below.
# libgl1       — libGL.so stub (required by glutin even when falling back to OSMesa).
# libgcc-s1    — Rust runtime dependency (libgcc_s.so.1).
# wget         — used only during this layer to fetch the stl-thumb .deb; removed after.
RUN apt-get update -qq \
 && apt-get install -y --no-install-recommends \
        wget \
        libosmesa6 \
        libgl1 \
        libgcc-s1 \
 && ln -s /usr/lib/x86_64-linux-gnu/libOSMesa.so.8 \
          /usr/lib/x86_64-linux-gnu/libOSMesa.so \
 && wget -q \
        "https://github.com/unlimitedbacon/stl-thumb/releases/download/v0.5.0/stl-thumb_0.5.0_amd64.deb" \
        -O /tmp/stl-thumb.deb \
 && dpkg --force-depends -i /tmp/stl-thumb.deb \
 && rm /tmp/stl-thumb.deb \
 && dpkg --purge wget \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

# Non-root user (matches app container convention).
RUN groupadd --system --gid 1001 rendersidecar \
 && useradd  --system --uid 1001 --gid rendersidecar --no-create-home rendersidecar

COPY --from=build /app/publish .

# Shared blob volume mount point.
RUN mkdir -p /data && chown rendersidecar:rendersidecar /data
VOLUME ["/data"]

USER rendersidecar

# Disable Mesa's shader disk cache. The rendersidecar user has no home directory, so
# the cache directory ($HOME/.cache/mesa_shader_cache) is not writable. The cache
# provides no persistent benefit in a container anyway.
ENV MESA_SHADER_CACHE_DISABLE=true

# Sidecar config defaults — overridden by SIDECAR__ env vars in compose/k8s.
ENV Sidecar__VolumeRoot=/data
ENV Sidecar__RendererBinary=/usr/bin/stl-thumb
ENV Sidecar__RenderWidth=512
ENV Sidecar__RenderHeight=512
ENV ASPNETCORE_HTTP_PORTS=5200

EXPOSE 5200

ENTRYPOINT ["dotnet", "Catalog3d.RenderSidecar.dll"]
