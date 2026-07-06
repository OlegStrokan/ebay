#!/usr/bin/env bash
#
# EF Core migration helper for every database-backed .NET service.
#
# Migrations are authored manually with this script and applied automatically on
# service startup via `DbContext.Database.MigrateAsync()` (see each Api/Program.cs).
# This replaces the previous EnsureDeleted/EnsureCreated dev-loop behaviour.
#
# The EF Core CLI is pinned as a local tool in .config/dotnet-tools.json so every
# developer uses the same version (8.0.22). The script restores it automatically.
#
# Usage:
#   scripts/ef-migrations.sh list
#   scripts/ef-migrations.sh add <MigrationName> [service]
#   scripts/ef-migrations.sh update [service]
#   scripts/ef-migrations.sh remove [service]
#   scripts/ef-migrations.sh sql [service]        # idempotent .sql into migrations-sql/
#
#   <service> is one of the labels printed by `list` (default: all services).
#
# Examples:
#   scripts/ef-migrations.sh add InitialCreate            # all services
#   scripts/ef-migrations.sh add AddCompanyId user        # one service
#   scripts/ef-migrations.sh update payment               # apply to one database
#   scripts/ef-migrations.sh sql                          # production-ready SQL scripts
#
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

# label | infrastructure (migrations) project | startup (Api) project | DbContext | output dir
# Empty DbContext/output-dir fields fall back to EF Core defaults (single context,
# "Migrations/" folder). The Order service has one row per DbContext because it
# uses a write model and a separate CQRS read model.
SERVICES=(
  "user|User/User/Infrastructure/Infrastructure.csproj|User/User/Api/Api.csproj||"
  "auth|Auth/Auth/Infrastructure/Infrastructure.csproj|Auth/Auth/Api/Api.csproj||"
  "product|Product/Product/Infrastructure/Infrastructure.csproj|Product/Product/Api/Api.csproj||"
  "payment|Payment/Payment/Infrastructure/Infrastructure.csproj|Payment/Payment/Api/Api.csproj||"
  "inventory|Inventory/Inventory/Infrastructure/Infrastructure.csproj|Inventory/Inventory/Api/Api.csproj||"
  "order-write|Order/Order/Infrastructure/Infrastructure.csproj|Order/Order/Api/Api.csproj|AppDbContext|Persistence/Migrations/Write"
  "order-read|Order/Order/Infrastructure/Infrastructure.csproj|Order/Order/Api/Api.csproj|ReadDbContext|Persistence/Migrations/Read"
)

SQL_OUT_DIR="migrations-sql"

ef() {
  dotnet tool run dotnet-ef "$@"
}

usage() {
  sed -n '2,30p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

# Echo the SERVICES rows matching an optional label filter; fail on unknown label.
resolve_rows() {
  local wanted="${1:-}" matched=0 row label
  for row in "${SERVICES[@]}"; do
    label="${row%%|*}"
    if [[ -z "$wanted" || "$label" == "$wanted" ]]; then
      echo "$row"
      matched=1
    fi
  done
  if [[ -n "$wanted" && $matched -eq 0 ]]; then
    echo "error: unknown service '$wanted' (run 'list' to see options)" >&2
    exit 1
  fi
}

cmd_list() {
  echo "Database-backed services:"
  local row label
  for row in "${SERVICES[@]}"; do
    label="${row%%|*}"
    echo "  - $label"
  done
}

cmd_add() {
  local name="${1:-}" filter="${2:-}"
  if [[ -z "$name" ]]; then
    echo "error: migration name required -> add <MigrationName> [service]" >&2
    exit 1
  fi
  local label project startup context outdir
  while IFS='|' read -r label project startup context outdir; do
    [[ -z "$label" ]] && continue
    echo ""
    echo "==> [$label] add migration '$name'"
    local args=(migrations add "$name" --project "$project" --startup-project "$startup")
    [[ -n "$context" ]] && args+=(--context "$context")
    [[ -n "$outdir" ]] && args+=(--output-dir "$outdir")
    ef "${args[@]}"
  done < <(resolve_rows "$filter")
}

cmd_update() {
  local filter="${1:-}"
  # Applying migrations needs a reachable database and a connection string.
  # Product/Order keep their local connection strings in appsettings.Development.json,
  # so default to the Development environment unless the caller overrides it.
  : "${ASPNETCORE_ENVIRONMENT:=Development}"
  export ASPNETCORE_ENVIRONMENT
  local label project startup context outdir
  while IFS='|' read -r label project startup context outdir; do
    [[ -z "$label" ]] && continue
    echo ""
    echo "==> [$label] apply migrations (ASPNETCORE_ENVIRONMENT=$ASPNETCORE_ENVIRONMENT)"
    local args=(database update --project "$project" --startup-project "$startup")
    [[ -n "$context" ]] && args+=(--context "$context")
    ef "${args[@]}"
  done < <(resolve_rows "$filter")
}

cmd_remove() {
  local filter="${1:-}"
  local label project startup context outdir
  while IFS='|' read -r label project startup context outdir; do
    [[ -z "$label" ]] && continue
    echo ""
    echo "==> [$label] remove last migration"
    local args=(migrations remove --project "$project" --startup-project "$startup")
    [[ -n "$context" ]] && args+=(--context "$context")
    ef "${args[@]}"
  done < <(resolve_rows "$filter")
}

cmd_sql() {
  local filter="${1:-}"
  mkdir -p "$SQL_OUT_DIR"
  local label project startup context outdir
  while IFS='|' read -r label project startup context outdir; do
    [[ -z "$label" ]] && continue
    local out="$SQL_OUT_DIR/$label.sql"
    echo ""
    echo "==> [$label] generate idempotent SQL -> $out"
    local args=(migrations script --idempotent --output "$out" --project "$project" --startup-project "$startup")
    [[ -n "$context" ]] && args+=(--context "$context")
    ef "${args[@]}"
  done < <(resolve_rows "$filter")
}

main() {
  local command="${1:-}"
  [[ $# -gt 0 ]] && shift || true

  echo "Restoring local dotnet tools..."
  dotnet tool restore >/dev/null

  case "$command" in
    list)            cmd_list "$@" ;;
    add)             cmd_add "$@" ;;
    update)          cmd_update "$@" ;;
    remove)          cmd_remove "$@" ;;
    sql)             cmd_sql "$@" ;;
    -h|--help|help|"") usage 0 ;;
    *)               echo "error: unknown command '$command'" >&2; usage 1 ;;
  esac
}

main "$@"
