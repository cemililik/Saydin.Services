#!/usr/bin/env bash
# Required real-TimescaleDB ingestion-ledger runner. Source is mounted read-only;
# only the fail-closed TRX result leaves the disposable SDK container.
set -euo pipefail

readonly source_dir=/repo
readonly work_dir=/work
readonly results_dir=/results
readonly project=tests/Saydin.PriceIngestion.IntegrationTests/Saydin.PriceIngestion.IntegrationTests.csproj

[[ "${SAYDIN_INGESTION_TEST_REQUIRED:-}" == "true" ]] || {
    echo "SAYDIN_INGESTION_TEST_REQUIRED=true zorunludur." >&2
    exit 1
}
[[ "${SAYDIN_INGESTION_TEST_RUN_ID:-}" =~ ^[0-9a-f]{32}$ ]] || {
    echo "SAYDIN_INGESTION_TEST_RUN_ID 32 lowercase hex karakter olmalıdır." >&2
    exit 1
}
[[ -n "${SAYDIN_INGESTION_TEST_DATABASE_FILE:-}" ]] || {
    echo "SAYDIN_INGESTION_TEST_DATABASE_FILE zorunludur." >&2
    exit 1
}
[[ "${SAYDIN_INGESTION_TEST_EXPECTED_HOST:-}" == "postgres" ]] || {
    echo "SAYDIN_INGESTION_TEST_EXPECTED_HOST yalnız disposable postgres olabilir." >&2
    exit 1
}
[[ -r "${source_dir}/${project}" ]] || {
    echo "Ingestion ledger test projesi read-only source mount içinde bulunamadı." >&2
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
    --logger "trx;LogFileName=ingestion-ledger.trx" \
    --verbosity normal
