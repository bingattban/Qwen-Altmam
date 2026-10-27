# Phase 12.06 — Offline atomic database-switch core (partial)

This is native infrastructure, NOT an enabled end-user restore workflow.

The OfflineRestoreSwitch component verifies the selected SQLCipher stage and the existing rescue, then creates a fresh WAL-consistent encrypted emergency snapshot of the latest live data. Only with all connections closed and no WAL/SHM/journal sidecars can it atomically replace the encrypted database file (same-volume File.Replace), retaining an independently inspectable old-database rollback image.

Before switching it flushes a durable JSON intent containing paths and SHA-256 digests, never keys or passwords. On an interrupted startup, ReconcileAtStartupAsync verifies the old/new database image with the original SQLCipher key and checksum. If neither matches, it restores only an exact cryptographically validated rollback image; otherwise it fails closed, without inventing keys or deleting the rescue. Synthetic exception checkpoints before and after replacement are tested on Windows.

**Critical gates still unfulfilled:** general-administration reauthentication and native confirmation; connecting startup reconciliation under the single-instance mutex BEFORE migrations; guaranteed lifetime closure of all running background services; user-facing staged restore and recovery-key selection; interrupted power-loss/disk-full tests on real Windows; cross-PC recovery acceptance; authenticated rollback UX. No web bridge action was enabled in this batch.

Do not declare the whole application or Phase 12 production-ready until these gates and phases 13–15 acceptance pass.