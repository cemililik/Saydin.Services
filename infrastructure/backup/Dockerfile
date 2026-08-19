# Restic encrypts every repository object client-side. The repository password is
# supplied only as an absolute, private file populated by the operator's KMS agent.
# tag: 0.18.1 (docker.io/restic/restic)
FROM restic/restic@sha256:39d9072fb5651c80d75c7a811612eb60b4c06b32ffe87c2e9f3c7222e1797e76 AS restic

# Match the database major and Timescale extension used by production and restore drills.
# tag: 2.16.1-pg16 (docker.io/timescale/timescaledb)
FROM timescale/timescaledb@sha256:3adf01543c37b5b88d3c4998338e0f7f21cb3cdd02bbddea08b09bf60e2289b7
COPY --from=restic /usr/bin/restic /usr/local/bin/restic
COPY backup-entrypoint.sh /usr/local/bin/saydin-backup
COPY prepare-recovery.sh /usr/local/bin/saydin-prepare-recovery
COPY select-base-snapshot.py /usr/local/bin/saydin-select-base-snapshot
COPY restore_target_guard.py /usr/local/bin/saydin-validate-restore-target
RUN adduser -D -H -u 1001 backup \
    && install -d -o backup -g backup -m 0700 /work /run/saydin-secrets \
    && chmod 0555 /usr/local/bin/saydin-backup /usr/local/bin/saydin-prepare-recovery \
      /usr/local/bin/saydin-select-base-snapshot /usr/local/bin/saydin-validate-restore-target \
      /usr/local/bin/restic
USER backup
WORKDIR /work
ENTRYPOINT ["/usr/local/bin/saydin-backup"]
CMD ["base-backup"]
