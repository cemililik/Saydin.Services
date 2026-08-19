#!/usr/bin/env bash
# Required CAL-001 replay/unit runner. The caller's TRX verifier rejects zero,
# skipped and non-success results; source stays read-only.
set -euo pipefail

readonly source_dir=/repo
readonly work_dir=/work
readonly results_dir=/results
readonly project=tools/calendar-data/tests/Saydin.CalendarData.Tests/Saydin.CalendarData.Tests.csproj
readonly tool=tools/calendar-data/src/Saydin.CalendarData/Saydin.CalendarData.csproj
readonly data=tools/calendar-data/data

[[ "${SAYDIN_CALENDAR_TEST_REQUIRED:-}" == "true" ]] || {
    echo "SAYDIN_CALENDAR_TEST_REQUIRED=true zorunludur." >&2
    exit 1
}
[[ "${SAYDIN_INTEGRATION_RUN_ID:-}" =~ ^[0-9a-f]{32}$ ]] || {
    echo "SAYDIN_INTEGRATION_RUN_ID 32 lowercase hex karakter olmalıdır." >&2
    exit 1
}
[[ -r "${source_dir}/${project}" && -d "${source_dir}/${data}" ]] || {
    echo "Calendar-data test projesi veya snapshot bundle bulunamadı." >&2
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
dotnet restore "${project}" --force-evaluate
dotnet test "${project}" \
    --configuration Release \
    --no-restore \
    --results-directory "${results_dir}" \
    --logger "trx;LogFileName=calendar-data.trx" \
    --verbosity normal
dotnet run --project "${tool}" --configuration Release --no-restore -- \
    verify --data-root "${data}"
