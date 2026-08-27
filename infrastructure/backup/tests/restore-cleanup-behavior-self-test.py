#!/usr/bin/env python3
from __future__ import annotations

import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[3]
RESTORE = ROOT / "infrastructure/backup/restore-drill.sh"


def executable(path: Path, text: str) -> None:
    path.write_text(text, encoding="utf-8")
    path.chmod(0o755)


def main() -> int:
    script = RESTORE.read_text(encoding="utf-8")
    start = script.index("docker_reachable() {")
    admission = script.index('docker_reachable || die "restore_docker_unavailable"', start)
    removal = script.index("remove_owned_resource() {", admission)
    functions = script[start:admission] + script[removal:script.index("trap 'cleanup $?' EXIT")]
    with tempfile.TemporaryDirectory(prefix="saydin-restore-cleanup-") as raw:
        root = Path(raw)
        fake = root / "bin"
        fake.mkdir()
        executable(fake / "sleep", "#!/bin/sh\nexit 0\n")
        executable(fake / "docker", """#!/bin/sh
state=${SAYDIN_TEST_STATE:?}
[ "${SAYDIN_TEST_DAEMON-}" != down ] || exit 2
case "$1:$2" in
  info:) exit 0 ;;
  container:inspect|volume:inspect|network:inspect) [ -e "$state/$3" ] ;;
  rm:-f) rm -f "$state/$3" ;;
  volume:rm|network:rm)
    [ "${SAYDIN_TEST_KEEP-}" != "$3" ] || exit 1
    rm -f "$state/$3" ;;
  *) exit 64 ;;
esac
""")
        names = {
            "dqa_container": "run-dqa", "api_container": "run-api",
            "redis_container": "run-redis", "database_container": "run-db",
            "init_container": "run-init", "fetch_container": "run-fetch",
            "evidence_copy_container": "run-copy", "prepare_container": "run-prepare",
            "transaction_container": "run-transaction",
            "recovery_state_container": "run-recovery", "role_container": "run-role",
            "migrator_container": "run-migrator",
            "evidence_verify_container": "run-verify", "volume": "run-data",
            "network": "run-net", "egress_network": "run-egress",
        }
        assignments = "\n".join(f"{key}={value}" for key, value in names.items())
        harness = root / "harness.sh"
        executable(harness, f"""#!/bin/sh
set -eu
resources_admitted=true
{assignments}
{functions}
cleanup "$1"
""")
        environment = dict(os.environ)
        environment["PATH"] = f"{fake}:{environment['PATH']}"
        environment["SAYDIN_TEST_STATE"] = str(root / "state")

        def run(status: int, *, daemon: str = "up", keep: str = "") -> subprocess.CompletedProcess[str]:
            state = root / "state"
            state.mkdir(exist_ok=True)
            environment["SAYDIN_TEST_DAEMON"] = daemon
            environment["SAYDIN_TEST_KEEP"] = keep
            return subprocess.run(
                [str(harness), str(status)], env=environment,
                capture_output=True, text=True, check=False,
            )

        passed = run(0)
        assert passed.returncode == 0 and passed.stdout.strip() == "restore_drill_passed"
        signaled = run(143)
        assert signaled.returncode == 143 and "restore_drill_passed" not in signaled.stdout
        (root / "state" / names["volume"]).touch()
        residual = run(0, keep=names["volume"])
        assert residual.returncode == 70 and "restore_cleanup_residual" in residual.stderr, (
            residual.returncode, residual.stdout, residual.stderr
        )
        (root / "state" / names["volume"]).unlink()
        daemon_error = run(0, daemon="down")
        assert daemon_error.returncode == 70 and "restore_drill_passed" not in daemon_error.stdout
    print("restore_cleanup_behavior_self_test_passed:success,signal,residual,daemon-error")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
