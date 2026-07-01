# Standard multi-stage build: SDK image restores/publishes, slim ASP.NET runtime image runs.
# Targets Render.com's "Web Service, Dockerfile-based deploy" path (also portable to any other
# container host — Fly.io, Railway, a plain VM, etc.) rather than a PaaS-proprietary build config.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the project file first so `dotnet restore` layer-caches independently of source changes.
COPY ExpertLapApi.csproj .
RUN dotnet restore ExpertLapApi.csproj

COPY . .
RUN dotnet publish ExpertLapApi.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Render (and most container hosts) injects a $PORT environment variable at container *runtime*
# and expects the app to bind to it; ASP.NET Core's default Kestrel binding is
# http://localhost:8080 unless told otherwise. $PORT isn't known at image-build time, so this is
# resolved in a shell-form entrypoint (not a fixed ENV/EXPOSE value) reading $PORT at container
# start, falling back to 8080 for plain `docker run` without Render's environment.
EXPOSE 8080
ENV PORT=8080
ENTRYPOINT ASPNETCORE_URLS=http://+:$PORT dotnet ExpertLapApi.dll
