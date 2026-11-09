# R15 — Preserve orphaned encrypted key-preflight evidence (2026-11-09)

Baseline: R14 merged in `89458280c0ee3eb06884806b545f16205aca86da`.

## Verified implementation gap
The R14 recovery-key verifier copies the encrypted customer database to `.altamam-key-preflight-<32hex>/candidate.dbbackup` in the data directory before reading it with SQLCipher. A power interruption between copy and cleanup can leave that *directory* (sometimes with candidate and WAL/SHM files) as the only surviving encrypted recovery evidence. R13's missing-DPAPI-key guard scans only files in that root and files under `backups/`, not the GUID-named scratch directory. When the original key and live DB are also absent, `LoadOrCreate` could silently issue a different random key. The protected-key writer itself can also leave `db.key.protected.<guid>.tmp` or `.pending` after a crash, another unrecognized key-bearing artifact.

## Test-first change
RED commit `b212904771ea418556f92acbf8b13524989174a2` adds five real Windows DPAPI startup cases: empty R14 scratch directory, scratch directory with encrypted candidate, scratch with WAL sidecar, orphaned initial protected-key `.tmp`, and orphaned original-key recovery `.pending`. Each has no live DB, key, WAL, SHM or backup directory; must throw the existing `MissingEncryptionKey` diagnostic and preserve the evidence without producing a new protected key. Positive coverage proves an unrelated directory and a noncanonical scratch-name are not treated as interrupted recovery.

The minimum production change extends the existing fail-closed `ContainsRecoveryArtifacts` detector with canonical 32-character hex preflight directories and exact GUID-bearing protected-key temporary-file names. It scans the actual key directory as well as the live database directory, which can be different. It does not read, delete, repair, decrypt or move those files. A valid existing DPAPI key is still read normally, and a truly fresh install still generates exactly one key.

## Intentional boundaries
Automated Windows tests are synthetic crash-state simulations, not a physical disk-full, power-loss, clean-machine, independent-account or cross-PC validation. Stale artifacts must be reviewed by an authorized operator; do **not** automatically delete them or silently rotate the key. Secure pre-login recovery UI and owner verification policy are still unresolved. No production GO or manual acceptance claim.

## Delivery gate
Windows Core and Windows Release must both succeed on the **same final SHA**. Release runs full tests before building x86/x64 Portable/Installer and performs independent SHA-256 verification across all four customer packages. Keep this PR draft until each mandatory green check and all four artifacts are present. Never merge with failures.
