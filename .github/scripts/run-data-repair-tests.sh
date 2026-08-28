#!/usr/bin/env bash
# Required DataRepair real-TimescaleDB runner. Source is mounted read-only;
# build output stays in /work and only fail-closed TRX/Cobertura artifacts leave it.
set -euo pipefail

readonly source_dir=/repo
readonly work_dir=/work
readonly results_dir=/results
readonly project=tests/Saydin.DataRepair.IntegrationTests/Saydin.DataRepair.IntegrationTests.csproj

[[ "${SAYDIN_REPAIR_TEST_REQUIRED:-}" == "true" ]] || {
    echo "SAYDIN_REPAIR_TEST_REQUIRED=true zorunludur." >&2
    exit 1
}
[[ "${SAYDIN_REPAIR_TEST_RUN_ID:-}" =~ ^[0-9a-f]{32}$ ]] || {
    echo "SAYDIN_REPAIR_TEST_RUN_ID 32 lowercase hex karakter olmalıdır." >&2
    exit 1
}
[[ "${SAYDIN_REPAIR_TEST_EXPECTED_HOST:-}" == "postgres" ]] || {
    echo "SAYDIN_REPAIR_TEST_EXPECTED_HOST yalnız disposable postgres olabilir." >&2
    exit 1
}
[[ -n "${SAYDIN_REPAIR_TEST_ADMIN_CONNECTION_FILE:-}" ]] || {
    echo "SAYDIN_REPAIR_TEST_ADMIN_CONNECTION_FILE zorunludur." >&2
    exit 1
}
[[ -r "${source_dir}/${project}" ]] || {
    echo "DataRepair integration projesi read-only source mount içinde bulunamadı." >&2
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
    --logger "trx;LogFileName=data-repair-integration.trx" \
    --verbosity normal
mapfile -t coverage_reports < <(find "${results_dir}" -mindepth 2 -maxdepth 2 \
    -type f -name coverage.cobertura.xml -print | sort)
[[ "${#coverage_reports[@]}" -eq 1 ]] || {
    echo "data_repair_integration_coverage_cardinality_invalid:${#coverage_reports[@]}" >&2
    exit 2
}
mv "${coverage_reports[0]}" "${results_dir}/data-repair-integration.coverage.cobertura.xml"
