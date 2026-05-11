# One-shot migrator image. Generates an idempotent migration script at
# build time (so the runtime image needs no SDK) and applies it via psql
# against the prod database on first boot.
#
# Idempotent migrations: `dotnet ef migrations script --idempotent`
# emits per-statement IF NOT EXISTS guards, so a re-run after a partial
# failure is safe and re-runs after the API is already migrated are no-ops.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Copy the minimum surface needed for `dotnet ef migrations script` to
# resolve the model: the ORM project, the WebApi startup project (for
# `DbContext` discovery), and every transitive csproj reference.
COPY src/ ./src/
COPY Ambev.DeveloperEvaluation.sln ./
RUN dotnet tool install --global dotnet-ef --version 8.0.10
ENV PATH="${PATH}:/root/.dotnet/tools"
RUN dotnet ef migrations script \
        --project src/Ambev.DeveloperEvaluation.ORM \
        --startup-project src/Ambev.DeveloperEvaluation.WebApi \
        --idempotent \
        --output /tmp/migrations.sql \
    && test -s /tmp/migrations.sql

# Postgres image already ships `psql` — no need to install anything.
FROM postgres:16-alpine AS final
COPY --from=build /tmp/migrations.sql /migrations.sql
# Wait for the DB to accept connections (compose `depends_on:
# service_healthy` covers most of this, but pg_isready is cheap belt-
# and-braces against a brief post-healthcheck blip), then apply the
# idempotent script. PG* env vars are passed by the compose file.
ENTRYPOINT ["sh", "-c", "until pg_isready -h \"$PGHOST\" -U \"$PGUSER\" -d \"$PGDATABASE\" >/dev/null 2>&1; do sleep 1; done && psql -h \"$PGHOST\" -U \"$PGUSER\" -d \"$PGDATABASE\" -v ON_ERROR_STOP=1 -f /migrations.sql"]
