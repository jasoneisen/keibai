#!/usr/bin/env bash
set -uo pipefail

# test.sh — build + test gate for Keibai. Mirrors offmarket.deals' test.sh: self-provisions the
# 'keibai' database against the compose/devcontainer Postgres, then builds (style gate on) and runs
# both test projects. Because global.json pins the Microsoft.Testing.Platform runner and the current
# `dotnet test` CLI has a handshake bug with it, we run the produced MTP test executables directly.

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

SOLUTION="Keibai.slnx"
TEST_PROJECTS=(
    "tests/Keibai.Tests/Keibai.Tests.csproj"
    "tests/Keibai.Web.Tests/Keibai.Web.Tests.csproj"
)

probe_tcp() { timeout 2 bash -c "</dev/tcp/$1/$2" 2>/dev/null; }

# Honour a pre-set ConnectionStrings__Keibai (CI/another machine). Otherwise synthesize the local
# default: the devcontainer compose db service on db:5432, falling back to localhost:5432. docker-compose
# in this repo publishes 5432 on localhost; the devcontainer shares the db container's network so 'db'
# also resolves.
if [ -z "${ConnectionStrings__Keibai:-}" ]; then
    if probe_tcp db 5432; then
        DB_HOST=db
    elif probe_tcp localhost 5432; then
        DB_HOST=localhost
    else
        echo '{"passed":false,"steps":[{"name":"db","passed":false,"output":"No Postgres on db:5432 or localhost:5432. Run: docker compose up -d db (or use the devcontainer db), or export ConnectionStrings__Keibai."}]}'
        exit 1
    fi
    export ConnectionStrings__Keibai="Host=${DB_HOST};Port=5432;Database=keibai;Username=postgres;Password=postgres"

    # Create the 'keibai' database once, idempotently, before any host boots (Marten builds SCHEMA on
    # startup but cannot CREATE DATABASE).
    if command -v psql >/dev/null 2>&1; then
        exists=$(PGPASSWORD=postgres psql -h "${DB_HOST}" -p 5432 -U postgres -d postgres -tAc \
            "SELECT 1 FROM pg_database WHERE datname='keibai'" 2>/dev/null)
        if [ -z "$exists" ]; then
            PGPASSWORD=postgres psql -h "${DB_HOST}" -p 5432 -U postgres -d postgres \
                -v ON_ERROR_STOP=1 -c "CREATE DATABASE keibai" >/dev/null 2>&1
        fi
    fi
fi

export Logging__LogLevel__Default=Warning

fail() { echo "GATE FAILED: $1"; exit 1; }

echo "== build (style gate on) =="
dotnet build "$SOLUTION" -warnaserror >/tmp/keibai_build.log 2>&1 || { cat /tmp/keibai_build.log; fail "build"; }

overall=0
for proj in "${TEST_PROJECTS[@]}"; do
    name="$(basename "$proj" .csproj)"
    bin="$(find "$(dirname "$proj")/bin" -name "$name" -type f 2>/dev/null | head -1)"
    [ -n "$bin" ] || fail "no test binary for $name"
    echo "== test: $name =="
    "$bin" || overall=1
done

[ "$overall" -eq 0 ] || fail "tests"
echo "GATE PASSED"
