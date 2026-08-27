#!/usr/bin/env bash
# Required real-TimescaleDB migrator runner. The TRX verifier in CI provides
# the second fail-closed layer: no discovery and any skipped test are failures.
set -euo pipefail

readonly source_dir=/repo
readonly work_dir=/work
readonly results_dir=/results
readonly project=tests/Saydin.DatabaseMigrator.Tests/Saydin.DatabaseMigrator.Tests.csproj

[[ "${SAYDIN_MIGRATOR_TEST_REQUIRED:-}" == "true" ]] || {
    echo "SAYDIN_MIGRATOR_TEST_REQUIRED=true zorunludur." >&2
    exit 1
}
[[ "${SAYDIN_INTEGRATION_RUN_ID:-}" =~ ^[0-9a-f]{32}$ ]] || {
    echo "SAYDIN_INTEGRATION_RUN_ID 32 lowercase hex karakter olmalıdır." >&2
    exit 1
}
[[ -n "${SAYDIN_MIGRATOR_TEST_DATABASE_FILE:-}" ]] || {
    echo "Primary migrator test database file zorunludur." >&2
    exit 1
}
[[ -n "${SAYDIN_MIGRATOR_SECONDARY_DATABASE_FILE:-}" ]] || {
    echo "Secondary migrator test database file zorunludur." >&2
    exit 1
}
[[ -r "${source_dir}/${project}" ]] || {
    echo "Migrator test projesi read-only source mount içinde bulunamadı." >&2
    exit 1
}
[[ -d "${results_dir}" && -w "${results_dir}" ]] || {
    echo "TRX results mount yazılabilir değil: ${results_dir}" >&2
    exit 1
}

mkdir -p "${work_dir}"
(
    cd "${source_dir}"
    tar --exclude=.git --exclude='*/bin' --exclude='*/obj' --exclude='*/TestResults' -cf - .
) | tar -xf - -C "${work_dir}"

cd "${work_dir}"
dotnet restore "${project}" --locked-mode
dotnet test "${project}" \
    --configuration Release \
    --no-restore \
    --results-directory "${results_dir}" \
    --settings "${work_dir}/.github/scripts/coverage.settings.xml" \
    --collect "XPlat Code Coverage" \
    --logger "trx;LogFileName=migrator.trx" \
    --verbosity normal
mapfile -t coverage_reports < <(find "${results_dir}" -mindepth 2 -maxdepth 2 \
    -type f -name coverage.cobertura.xml -print | sort)
[[ "${#coverage_reports[@]}" -eq 1 ]] || {
    echo "migrator_integration_coverage_cardinality_invalid:${#coverage_reports[@]}" >&2
    exit 2
}
mv "${coverage_reports[0]}" "${results_dir}/migrator-integration.coverage.cobertura.xml"
