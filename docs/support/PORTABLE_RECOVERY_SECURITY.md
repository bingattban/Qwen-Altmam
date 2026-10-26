# Phase 12.07 — Portable encrypted recovery material

The project uses a random 256-bit SQLCipher database key protected for ordinary
local use by Windows user-scoped DPAPI. DPAPI key files **do not work on a
different Windows user profile or computer**.

The infrastructure layer now provides `PortableRecoveryKeyPackage` (v1) and
`WindowsDatabaseKeyStore.ExportPortableRecoveryKey` /
`RecoverExistingDatabaseKey`. The v1 package is a separate
`.altrecovery` file containing a 256-bit key encrypted with AES-256-GCM,
a random 256-bit salt, a random 96-bit nonce, authenticated format metadata
and PBKDF2-HMAC-SHA256 with 600,000 iterations. It does not contain the
recovery password or any plain database key.

The recovery file must be held **separately** from the encrypted SQLCipher
backup, together with its separately safeguarded passphrase. Losing both the
DPAPI file and the recovery password/material makes that database impossible
to decrypt using the available information; the program must not silently
create a replacement key.

The recovery import API fails closed when a destination DPAPI key already exists,
requires an existing encrypted database, verifies actual SQLCipher integrity
*before* writing any new protected key, and never edits, moves or deletes the
database. It protects the validated original key under the destination
Windows user's own DPAPI scope using an atomic new-file move.

**Integration status:** Cryptographic core and Windows tests only. The native
GeneralAdmin-only passphrase/file dialogs, explicit owner confirmation,
staged database restoration and crash-recoverable atomic activation remain
mandatory and are **not implemented** in this batch. No recovery secret is
passed to JavaScript or an action response. This code is intentionally not
exposed to end users until native secure dialogs and authentication gates pass.
