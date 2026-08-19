#!/usr/bin/env bash
# Required role-control-plane unit + real-PostgreSQL runner. Source remains
# read-only; only the two fail-closed TRX files leave the disposable container.
set -euo pipefail

readonly source_dir=/repo
readonly work_dir=/work
readonly results_dir=/results
readonly unit_project=tests/Saydin.DatabaseRoleBootstrap.Tests/Saydin.DatabaseRoleBootstrap.Tests.csproj
readonly integration_project=tests/Saydin.DatabaseRoleBootstrap.IntegrationTests/Saydin.DatabaseRoleBootstrap.IntegrationTests.csproj

[[ "${SAYDIN_ROLE_BOOTSTRAP_TEST_REQUIRED:-}" == "true" ]] || {
    echo "SAYDIN_ROLE_BOOTSTRAP_TEST_REQUIRED=true zorunludur." >&2
    exit 1
}
[[ "${SAYDIN_INTEGRATION_RUN_ID:-}" =~ ^[0-9a-f]{32}$ ]] || {
    echo "SAYDIN_INTEGRATION_RUN_ID 32 lowercase hex karakter olmalıdır." >&2
    exit 1
}
[[ -n "${SAYDIN_ROLE_BOOTSTRAP_TEST_ADMIN_CONNECTION_FILE:-}" ]] || {
    echo "SAYDIN_ROLE_BOOTSTRAP_TEST_ADMIN_CONNECTION_FILE zorunludur." >&2
    exit 1
}
[[ -r "${source_dir}/${unit_project}" && -r "${source_dir}/${integration_project}" ]] || {
    echo "Role-bootstrap test projeleri read-only source mount içinde bulunamadı." >&2
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
dotnet restore "${unit_project}" --force-evaluate
dotnet restore "${integration_project}" --force-evaluate
dotnet test "${unit_project}" \
    --configuration Release \
    --no-restore \
    --results-directory "${results_dir}" \
    --logger "trx;LogFileName=role-bootstrap-unit.trx" \
    --verbosity normal
dotnet test "${integration_project}" \
    --configuration Release \
    --no-restore \
    --results-directory "${results_dir}" \
    --settings "${work_dir}/.github/scripts/coverage.settings.xml" \
    --collect "XPlat Code Coverage" \
    --logger "trx;LogFileName=role-bootstrap-integration.trx" \
    --verbosity normal
mapfile -t coverage_reports < <(find "${results_dir}" -mindepth 2 -maxdepth 2 \
    -type f -name coverage.cobertura.xml -print | sort)
[[ "${#coverage_reports[@]}" -eq 1 ]] || {
    echo "role_bootstrap_integration_coverage_cardinality_invalid:${#coverage_reports[@]}" >&2
    exit 2
}
mv "${coverage_reports[0]}" "${results_dir}/role-bootstrap-integration.coverage.cobertura.xml"
