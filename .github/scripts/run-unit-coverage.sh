#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
output_root="${1:-}"
if [[ -z "$output_root" ]]; then
  echo "unit_coverage_failed:output_directory_required" >&2
  exit 64
fi
if [[ "$output_root" != /* ]]; then
  echo "unit_coverage_failed:absolute_output_directory_required" >&2
  exit 64
fi
if [[ -e "$output_root" ]] && [[ -n "$(find "$output_root" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ]]; then
  echo "unit_coverage_failed:output_directory_not_empty" >&2
  exit 64
fi

projects=(
  "tests/Saydin.Api.Tests/Saydin.Api.Tests.csproj"
  "tests/Saydin.PriceIngestion.Tests/Saydin.PriceIngestion.Tests.csproj"
  "tests/Saydin.DataQualityAudit.Tests/Saydin.DataQualityAudit.Tests.csproj"
  "tests/Saydin.DatabaseMigrator.Tests/Saydin.DatabaseMigrator.Tests.csproj"
  "tests/Saydin.DatabaseRoleBootstrap.Tests/Saydin.DatabaseRoleBootstrap.Tests.csproj"
  "tests/Saydin.DataRepair.Tests/Saydin.DataRepair.Tests.csproj"
  "tools/calendar-data/tests/Saydin.CalendarData.Tests/Saydin.CalendarData.Tests.csproj"
)
filters=(
  ""
  ""
  ""
  "Category=Unit"
  ""
  ""
  ""
)
minimum_tests=(658 182 97 78 98 29 94)

[[ "${#projects[@]}" -eq "${#filters[@]}" && "${#projects[@]}" -eq "${#minimum_tests[@]}" ]] || {
  echo "unit_coverage_failed:internal_project_contract" >&2
  exit 70
}

mapfile -t discovered_projects < <(
  find tests tools/calendar-data/tests -type f -name '*Tests.csproj' \
    ! -name '*IntegrationTests.csproj' -print | LC_ALL=C sort
)
mapfile -t configured_projects < <(printf '%s\n' "${projects[@]}" | LC_ALL=C sort)
if [[ "${#discovered_projects[@]}" -ne "${#configured_projects[@]}" ]] \
  || ! diff -u <(printf '%s\n' "${discovered_projects[@]}") \
    <(printf '%s\n' "${configured_projects[@]}") >/dev/null; then
  echo "unit_coverage_failed:unit_project_inventory_mismatch" >&2
  diff -u <(printf '%s\n' "${discovered_projects[@]}") \
    <(printf '%s\n' "${configured_projects[@]}") >&2 || true
  exit 70
fi

mkdir -p "$output_root"
cd "$repo_root"

for index in "${!projects[@]}"; do
  project="${projects[$index]}"
  filter="${filters[$index]}"
  [[ -f "$project" ]] || { echo "unit_coverage_failed:project_missing:$project" >&2; exit 2; }
  project_name="$(basename "$(dirname "$project")")"
  project_output="$output_root/$project_name"
  mkdir -p "$project_output"
  dotnet restore "$project" --locked-mode
  dotnet build "$project" --no-restore --configuration Release
  test_args=(
    "$project" --no-build --no-restore --configuration Release
    --settings "$repo_root/.github/scripts/coverage.settings.xml"
    --collect "XPlat Code Coverage"
    --logger "trx;LogFileName=$project_name.trx"
    --results-directory "$project_output"
  )
  if [[ -n "$filter" ]]; then
    test_args+=(--filter "$filter")
  fi
  dotnet test "${test_args[@]}"
  trx="$project_output/$project_name.trx"
  [[ -s "$trx" ]] || { echo "unit_coverage_failed:trx_missing:$project_name" >&2; exit 2; }
  python3 "$repo_root/.github/scripts/verify-integration-trx.py" "$trx" \
    --minimum-executed "${minimum_tests[$index]}"
  generated_reports=()
  while IFS= read -r report; do
    generated_reports+=("$report")
  done < <(
    find "$project_output" -mindepth 2 -maxdepth 2 -type f \
      -name coverage.cobertura.xml -print | sort
  )
  [[ "${#generated_reports[@]}" -eq 1 ]] || {
    echo "unit_coverage_failed:report_cardinality:$project_name:${#generated_reports[@]}" >&2
    exit 2
  }
  mv "${generated_reports[0]}" "$project_output/coverage.cobertura.xml"
  [[ -s "$project_output/coverage.cobertura.xml" ]] || {
    echo "unit_coverage_failed:report_missing:$project_name" >&2
    exit 2
  }
done

echo "unit_coverage_passed:projects=${#projects[@]}:output=$output_root"
