#!/usr/bin/env bash
# Required CI integration runner. The repository is mounted read-only; build
# outputs stay in this disposable container; TRX and one Cobertura report leave via /results.
set -euo pipefail

readonly source_dir=/repo
readonly work_dir=/work
readonly results_dir=/results
readonly project=tests/Saydin.Api.IntegrationTests/Saydin.Api.IntegrationTests.csproj

[[ "${SAYDIN_INTEGRATION_REQUIRED:-}" == "true" ]] || {
    echo "SAYDIN_INTEGRATION_REQUIRED=true zorunludur." >&2
    exit 1
}
[[ "${SAYDIN_INTEGRATION_RUN_ID:-}" =~ ^[0-9a-f]{32}$ ]] || {
    echo "SAYDIN_INTEGRATION_RUN_ID 32 lowercase hex karakter olmalıdır." >&2
    exit 1
}
[[ -r "${source_dir}/${project}" ]] || {
    echo "Integration test projesi read-only source mount içinde bulunamadı." >&2
    exit 1
}
[[ -d "${results_dir}" && -w "${results_dir}" ]] || {
    echo "TRX results mount yazılabilir değil: ${results_dir}" >&2
    exit 1
}
[[ -r "${SAYDIN_REDIS_CONFIG_FILE:-}" ]] || {
    echo "Redis config secret file okunabilir olmalıdır." >&2
    exit 1
}
redis_password="$(sed -n 's/^requirepass //p' "$SAYDIN_REDIS_CONFIG_FILE")"
[[ -n "$redis_password" && "$redis_password" != *$'\n'* ]]
export ConnectionStrings__Redis="redis:6379,password=$redis_password,connectTimeout=5000"
unset redis_password

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
    --logger "trx;LogFileName=integration.trx" \
    --verbosity normal
mapfile -t coverage_reports < <(find "${results_dir}" -mindepth 2 -maxdepth 2 \
    -type f -name coverage.cobertura.xml -print | sort)
[[ "${#coverage_reports[@]}" -eq 1 ]] || {
    echo "integration_coverage_cardinality_invalid:${#coverage_reports[@]}" >&2
    exit 2
}
mv "${coverage_reports[0]}" "${results_dir}/api-integration.coverage.cobertura.xml"
