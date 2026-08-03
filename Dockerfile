# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so dependency layers are cached independently of source changes.
COPY src/StaticSiteHost/StaticSiteHost.csproj src/StaticSiteHost/
RUN dotnet restore src/StaticSiteHost/StaticSiteHost.csproj

COPY src/ src/
RUN dotnet publish src/StaticSiteHost/StaticSiteHost.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish \
        -p:UseAppHost=false


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
