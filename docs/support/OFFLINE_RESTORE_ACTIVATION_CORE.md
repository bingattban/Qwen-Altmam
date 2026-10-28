# Phase 12.06 — Offline atomic database-switch core (partial)

This is native infrastructure, NOT an enabled end-user restore workflow.

The OfflineRestoreSwitch component verifies the selected SQLCipher stage and the existing rescue, then creates a fresh WAL-consistent encrypted emergency snapshot of the latest live data. Only with all connections closed and no WAL/SHM/journal sidecars can it atomically replace the encrypted database file (same-volume File.Replace), retaining an independently inspectable old-database rollback image.

Before switching it flushes a durable JSON intent containing paths and SHA-256 digests, never keys or passwords. On an interrupted startup, ReconcileAtStartupAsync verifies the old/new database image with the original SQLCipher key and checksum. If neither matches, it restores only an exact cryptographically validated rollback image; otherwise it fails closed, without inventing keys or deleting the rescue. Synthetic exception checkpoints before and after replacement are tested on Windows.

**Native startup reconciliation is now connected:** MainWindow.PrepareServicesAsync invokes ReconcileAtStartupAsync after the original DPAPI key is obtained, under the App.xaml.cs single-instance mutex, and BEFORE any SQLCipher DbContext is instantiated, migrations are applied, scheduler starts or WebView2 attaches. On ambiguous/corrupt states it blocks application opening and leaves encrypted rescue files intact.

**Critical gates still unfulfilled:** general-administration reauthentication and native restore confirmation; guaranteed shutdown of all services before intentional switching; user-facing staged restore and recovery-key selection; interrupted power-loss/disk-full tests on real Windows; cross-PC recovery acceptance; authenticated rollback UX. No web bridge restore action was enabled in this batch.

Do not declare the whole application or Phase 12 production-ready until these gates and phases 13–15 acceptance pass.

## 2026-10-28 follow-up — untrusted crash journal validation

Before startup recovery reads, trusts or clears a pending activation intent, the
native switch now requires all six fields to be present: live and staged paths,
rollback and rescue paths, and the two complete SHA-256 digests. It verifies
that both digests are exactly 64 hexadecimal characters and that the rollback
has the canonical `.altamam-restore-rollback-<32-hex>.dbbackup` basename in
the **same directory** as the live SQLCipher database. The rescue must be
`backups/rescue-at-activation-<same-32-hex>.dbbackup` under that live
database directory; the stage remains a canonical sibling file.

A corrupted or tampered intent fails **before** attempting to approve either
live database image or touch rollback files. The journal is retained for
manual forensic recovery. New synthetic SQLCipher crash tests cover
unauthorized rollback extensions, rescue directory/identity changes, stage
escape, and a malformed digest after replacement. The original valid
before/after-replacement crash reconciliation tests remain mandatory.

This improves the already-existing **startup reconciliation mechanism only**.
It does **not** activate `RestoreDatabaseAtomically`, permit UI restoration,
bypass fresh GeneralAdministration reauthentication, terminate active DB
connections, test actual power loss, or validate cross-device key recovery.
The previously documented no-go gates remain OPEN.
