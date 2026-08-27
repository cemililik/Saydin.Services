#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"

if (($# == 0)); then
  output_root="$(mktemp -d /tmp/saydin-unit-coverage.XXXXXX)"
  exec "$repo_root/.github/scripts/run-unit-coverage.sh" "$output_root"
fi

# Commands that perform project discovery must name an explicit project. Otherwise
# the SDK discovers Saydin.Services.sln from /src and can report a misleading green
# run after integration projects dynamically skip.
case "$1" in
  test|build)
    explicit_project=false
    for argument in "${@:2}"; do
      case "$argument" in
        *.csproj)
          if [[ -f "$repo_root/$argument" ]]; then
            explicit_project=true
          fi
          break
          ;;
      esac
    done
    if [ "$explicit_project" != true ]; then
      printf '%s\n' "local_test_scope_rejected:explicit_project_required:$1" >&2
      exit 64
    fi
    ;;
esac

# The root development Compose service has no purpose-specific PostgreSQL credentials.
# Refuse commands which would otherwise produce skips or fixture failures and look like a
# successful local integration gate. Required integration uses compose.integration.yml.
for argument in "$@"; do
  case "$argument" in
    *Saydin.Services.sln*|*IntegrationTests*|*Saydin.DatabaseMigrator.Tests*)
      printf '%s\n' "local_test_scope_rejected:use_required_integration_stack:$argument" >&2
      exit 64
      ;;
  esac
done

exec dotnet "$@"
