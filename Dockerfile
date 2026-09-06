# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy just the project file first so `dotnet restore` is cached across builds.
COPY ScaleTrigger/ScaleTrigger.csproj ScaleTrigger/
RUN dotnet restore ScaleTrigger/ScaleTrigger.csproj

COPY ScaleTrigger/ ScaleTrigger/
RUN dotnet publish ScaleTrigger/ScaleTrigger.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# appsettings.json is never baked into the image; config comes from env vars
# (DatabaseProvider, ConnectionStrings__*, Jwt__*, ...) via the __ nesting
# convention documented in README.md's "appsettings.json on Azure" table.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# curl isn't in the base aspnet image - needed for HEALTHCHECK below.
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

RUN chown -R app:app /app
USER app

# /health/live only confirms the process is accepting requests (no DB round-trip) - a database
# outage shouldn't make Docker/Compose think the container itself needs restarting.
HEALTHCHECK --interval=10s --timeout=3s --start-period=10s --retries=3 \
    CMD ["curl", "-f", "http://localhost:8080/health/live"]

ENTRYPOINT ["dotnet", "ScaleTrigger.dll"]
