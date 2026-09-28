# syntax=docker/dockerfile:1

# Two images from one file:
#
#   runtime            ASP.NET runtime only (~220 MB). Static hosting; functions cannot be
#                      compiled, and the app says so rather than failing.
#   runtime-functions  The full .NET SDK (~900 MB), so uploaded functions can be built with
#                      `dotnet publish`. The default: it is the last stage, and compose
#                      selects it unless IMAGE_TARGET says otherwise.
#
# Only building a function needs the SDK. Running one that is already built needs just the
# runtime, because the build output is kept on the /data volume.

# The build stage runs on the build machine's own architecture even when the image is for
# another: the published app is architecture-neutral IL, so there is nothing to gain from
# compiling it under emulation, and a lot of time to lose.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so dependency layers are cached independently of source changes. That takes
# every project file restore reads: the host's, the StaticSiteHost.Abstractions project it
# references, and the Directory.Build.props they share.
COPY src/Directory.Build.props src/
COPY src/StaticSiteHost/StaticSiteHost.csproj src/StaticSiteHost/
COPY src/StaticSiteHost.Abstractions/StaticSiteHost.Abstractions.csproj src/StaticSiteHost.Abstractions/
RUN dotnet restore src/StaticSiteHost/StaticSiteHost.csproj

COPY src/ src/

# The release version, such as 1.2.3 from the tag the publish workflow runs for. It is also
# the StaticSiteHost.Abstractions version the server compiles functions against and reports.
# Declared here rather than at the top, because a changed build argument invalidates every
# RUN after it, and the restore above does not need it.
ARG VERSION=0.0.0-dev
RUN dotnet publish src/StaticSiteHost/StaticSiteHost.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish \
        -p:UseAppHost=false \
        -p:Version=$VERSION


FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    SiteHosting__DataRoot=/data

COPY --from=build /app/publish ./

# Uploads, extracted sites, users and keys all live on the /data volume.
RUN mkdir -p /data && chown -R app:app /data

USER app
VOLUME ["/data"]
EXPOSE 8080

ENTRYPOINT ["dotnet", "StaticSiteHost.dll"]


FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime-functions
WORKDIR /app

# NUGET_PACKAGES puts the package cache on the volume. Left in the container's home
# directory, it is lost whenever the container is recreated, and the next function build
# downloads every package (ScottPlot, SkiaSharp's native binaries, ...) all over again.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    SiteHosting__DataRoot=/data \
    NUGET_PACKAGES=/data/nuget \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

COPY --from=build /app/publish ./

# The SDK's first command for a user tries to verify workloads under /usr/share/dotnet,
# which a non-root user cannot write, and prints "An issue was encountered verifying
# workloads" into the build output an administrator reads. Function builds use no
# workloads, so: manifest mode (set as root), then one throwaway command as the app user
# so the first-use marker is baked into its home directory instead of being recreated,
# and the warning repeated, every time the container is.
#
# Fonts: the base image has none, and SkiaSharp (what ScottPlot and most .NET drawing
# libraries render with) then draws text as nothing at all, with no error. DejaVu is a sane
# default; Liberation matches Arial/Times metrics, so text lays out as it does on Windows.
RUN apt-get update \
 && apt-get install -y --no-install-recommends fontconfig fonts-dejavu-core fonts-liberation \
 && rm -rf /var/lib/apt/lists/* \
 && dotnet workload config --update-mode manifests \
 && mkdir -p /data/nuget && chown -R app:app /data

USER app
RUN dotnet workload list > /dev/null

VOLUME ["/data"]
EXPOSE 8080

ENTRYPOINT ["dotnet", "StaticSiteHost.dll"]
