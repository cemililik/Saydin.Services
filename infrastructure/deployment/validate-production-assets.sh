#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd "$script_dir/../.." && pwd -P)"
env_file="${1:-$script_dir/tests/production.validation.env}"

prometheus_image="prom/prometheus@sha256:565ee86501224ebbb98fc10b332fa54440b100469924003359edf49cbce374bd"
alertmanager_image="prom/alertmanager@sha256:690c7b525f4367aa91f73e2f91c632206d32e97c6384bdbf2fb7a861b420340d"
otel_image="otel/opentelemetry-collector-contrib@sha256:1f2c54a30e713fac6b3ae77a1ec84010c2007e29ced8ec666214fc2f6739c1cc"
tempo_image="grafana/tempo@sha256:65a5789759435f1ef696f1953258b9bbdb18eb571d5ce711ff812d2e128288a4"
loki_image="grafana/loki@sha256:cd6e176883a90c21755f0315688668991634143423f75bdedfef41441b0fdc3c"
caddy_validation_image="saydin-caddy-validation:asset-check-$$"
caddy_validation_image_created=false
if docker image inspect "$caddy_validation_image" >/dev/null 2>&1; then
  echo "production_assets_validation_failed:caddy_image_collision" >&2
  exit 73
fi

validation_tmp="$(mktemp -d /tmp/saydin-production-assets.XXXXXX)"
case "$validation_tmp" in
  /tmp/saydin-production-assets.*) ;;
  *) echo "production_assets_validation_failed:unsafe_temp" >&2; exit 64 ;;
esac
cleanup() {
  status=$?
  trap - EXIT
  if [[ -n "${validation_tmp:-}" && -d "$validation_tmp" ]]; then
    case "$validation_tmp" in
      /tmp/saydin-production-assets.*) rm -rf -- "$validation_tmp" ;;
      *) echo "production_assets_validation_failed:unsafe_cleanup" >&2; status=64 ;;
    esac
  fi
  if [[ "$caddy_validation_image_created" == true ]]; then
    case "$caddy_validation_image" in
      saydin-caddy-validation:asset-check-[0-9]*)
        docker image rm "$caddy_validation_image" >/dev/null || {
          echo "production_assets_validation_failed:caddy_image_cleanup" >&2
          [[ "$status" -ne 0 ]] || status=70
        }
        ;;
      *)
        echo "production_assets_validation_failed:unsafe_caddy_image_cleanup" >&2
        [[ "$status" -ne 0 ]] || status=64
        ;;
    esac
  fi
  exit "$status"
}
trap cleanup EXIT
install -d -m 0700 "$validation_tmp/prometheus/rules" "$validation_tmp/prometheus/targets"
cp "$repo_root/infrastructure/prometheus/prometheus.production.yml" \
  "$validation_tmp/prometheus/prometheus.yml"
cp "$repo_root/infrastructure/prometheus/rules/"*.yml "$validation_tmp/prometheus/rules/"
cp "$repo_root/infrastructure/deployment/tests/prometheus-targets/blackbox.json" \
  "$validation_tmp/prometheus/targets/blackbox.json"

"$script_dir/validate-production.sh" "$env_file"
python3 "$script_dir/validate-observability.py"
python3 "$script_dir/observability-self-test.py"
python3 "$script_dir/private-material-self-test.py"
python3 "$script_dir/monitoring-runtime-self-test.py"
python3 "$script_dir/volume-contract-self-test.py"

docker run --rm --read-only --cap-drop ALL --security-opt no-new-privileges \
  --user 65534:65534 --entrypoint promtool \
  -v "$validation_tmp/prometheus:/etc/prometheus:ro" \
  "$prometheus_image" check config /etc/prometheus/prometheus.yml
docker run --rm --read-only --cap-drop ALL --security-opt no-new-privileges \
  --user 65534:65534 --tmpfs /tmp:uid=65534,gid=65534,mode=0700 --entrypoint promtool \
  -v "$repo_root/infrastructure/prometheus:/etc/prometheus:ro" \
  "$prometheus_image" test rules \
    /etc/prometheus/tests/rules.test.yml /etc/prometheus/tests/inventory.test.yml
docker run --rm --read-only --cap-drop ALL --security-opt no-new-privileges \
  --user 65534:65534 --entrypoint amtool \
  -v "$repo_root/infrastructure/alertmanager/alertmanager.template.yml:/etc/alertmanager/alertmanager.yml:ro" \
  "$alertmanager_image" check-config /etc/alertmanager/alertmanager.yml

docker run --rm --read-only --cap-drop ALL --security-opt no-new-privileges \
  --user 10001:10001 --tmpfs /var/lib/otelcol/queue:uid=10001,gid=10001,mode=0700 \
  -e SAYDIN_RELEASE_VERSION=validation -e SAYDIN_DEPLOYMENT_ID=validation \
  -e SAYDIN_GIT_SHA=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa \
  -v "$repo_root/infrastructure/otel/otel-collector.production.yml:/etc/otelcol/config.yml:ro" \
  "$otel_image" validate --config=/etc/otelcol/config.yml
docker run --rm --read-only --cap-drop ALL --security-opt no-new-privileges \
  --user 10001:10001 -e SAYDIN_TEMPO_RETENTION=720h \
  -v "$repo_root/infrastructure/otel/tempo.production.yml:/etc/tempo/config.yml:ro" \
  "$tempo_image" -config.file=/etc/tempo/config.yml -config.expand-env=true -config.verify=true
docker run --rm --read-only --cap-drop ALL --security-opt no-new-privileges \
  --user 10001:10001 -e SAYDIN_LOKI_RETENTION=720h \
  -v "$repo_root/infrastructure/otel/loki.production.yml:/etc/loki/config.yml:ro" \
  "$loki_image" -config.file=/etc/loki/config.yml -config.expand-env=true -verify-config

docker build --pull=false --tag "$caddy_validation_image" \
  --file "$script_dir/Dockerfile.caddy" "$script_dir"
caddy_validation_image_created=true
docker run --rm --read-only --cap-drop ALL --security-opt no-new-privileges \
  --user 1000:1000 --entrypoint caddy \
  -e SAYDIN_PUBLIC_HOST=api.validation.test -e SAYDIN_ACME_EMAIL=ops@validation.test \
  -v "$script_dir/Caddyfile:/etc/caddy/Caddyfile:ro" \
  "$caddy_validation_image" validate --config /etc/caddy/Caddyfile --adapter caddyfile

echo "production_assets_validation_passed"
