# PesaCore — Linux container (Kestrel). The practical local-dev / CI artifact.
# Runs NATIVELY on arm64 (Apple Silicon) and amd64 — no emulation.
#
# This is the "containerized front door": Kestrel listens directly, web.config
# is inert (it only matters to IIS on the on-prem Windows host). Same DLL as the
# IIS deploy — only the process host changes. See Dockerfile.windows for the
# IIS-faithful Windows-container mirror of the on-prem target.
#
# Build context is the REPO ROOT:
#   docker build -t pesacore .
#   docker run --rm -p 8080:8080 pesacore   # -> http://localhost:8080/scalar/v1

# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj first and restore — keeps the restore layer cached across code-only changes.
COPY PesaCore/PesaCore.csproj PesaCore/
RUN dotnet restore PesaCore/PesaCore.csproj

# Now the source, then publish a Release build.
COPY PesaCore/ PesaCore/
RUN dotnet publish PesaCore/PesaCore.csproj -c Release -o /app /p:UseAppHost=false

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl for the container HEALTHCHECK only (the aspnet image ships none). One small
# layer; in a stricter banking image you'd map a real /healthz and drop this.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

# The COPY above runs as root, so /app is root-owned. The app writes its SQLite
# DB (BankDb.db + WAL sidecars) into the content root at startup, so the non-root
# runtime user must own /app — otherwise: "SQLite Error 14: unable to open database file".
RUN chown -R app:app /app

# Kestrel binds to 8080 (non-privileged port — required when running as non-root).
ENV ASPNETCORE_URLS=http://+:8080
# Development keeps the Scalar UI (/scalar/v1) + OpenAPI mounted for demos and
# avoids HTTPS redirection in a HTTP-only container. Flip to Production for a
# prod-shaped run: docker run -e ASPNETCORE_ENVIRONMENT=Production ...
ENV ASPNETCORE_ENVIRONMENT=Development

# The aspnet image ships a non-root "app" user (UID 64198). Banking-grade default:
# never run the workload as root. SQLite (BankDb.db) is written under /app, which
# this user owns via the COPY above.
USER app

EXPOSE 8080

# Liveness: probe a real, always-mapped endpoint (/Accounts/best works in every
# environment; /healthz is NOT mapped despite the OTel filter referencing one).
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
  CMD curl -fsS http://localhost:8080/Accounts/best || exit 1

ENTRYPOINT ["dotnet", "PesaCore.dll"]
