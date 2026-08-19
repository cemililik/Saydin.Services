#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
ENV_FILE="${1:-}"
if [[ -z "$ENV_FILE" || ! -f "$ENV_FILE" ]]; then
  echo "production_validation_failed:env_file_required" >&2
  exit 64
fi

TMP_DIR="$(mktemp -d)"
cleanup() {
  rm -f -- "${TMP_DIR:?}/a.json" "${TMP_DIR:?}/b.json"
  rmdir -- "${TMP_DIR:?}"
}
trap cleanup EXIT INT TERM

for project in saydin_prod_validation_a saydin_prod_validation_b; do
  output="$TMP_DIR/${project##*_}.json"
  docker compose --project-name "$project" --env-file "$ENV_FILE" \
    --file "$SCRIPT_DIR/compose.production.yml" --profile "*" config --format json > "$output"
  "$SCRIPT_DIR/validate-production.py" "$output"
done

"$SCRIPT_DIR/validation-self-test.py" "$TMP_DIR/a.json"

python3 - "$TMP_DIR/a.json" "$TMP_DIR/b.json" <<'PY'
import json, sys
first = json.load(open(sys.argv[1], encoding="utf-8"))
second = json.load(open(sys.argv[2], encoding="utf-8"))
if first.get("name") == second.get("name"):
    raise SystemExit("production_validation_failed:project_name_not_isolated")
print("production_two_project_config_passed")
PY
