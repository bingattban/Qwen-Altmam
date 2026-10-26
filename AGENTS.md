# Mandatory rules for all coding agents

All contributors and agents implementing phases or optimizations of the AlTamam project MUST follow [the three-gate acceptance contract](docs/PERFORMANCE_SAFETY_GATE.md): **measured performance improvement, exact output parity, and passing Windows/security/integrity regression tests**, all tied to the exact Git SHA. Keep changes on review branches and do not merge while required checks are missing or failing. The contract applies to every phase, including future work. The listed speculative optimizations remain deferred until the full program is completed.

Never delete historical, audit, movement, attendance, mutation-receipt or other durable evidence during maintenance. Preview tokens are eligible for deletion only more than 30 days after expiry, after a conservative check for ALL existing references. Preserve all user-approved functional behavior, UI workflows, SQLCipher encryption, role enforcement, transactional atomicity and idempotency.

## Binding October 26 owner exceptions

Use [the owner's 63 dated exceptions](docs/requirements/OWNER_EXCEPTIONS_2026-10-26.md) when reconciling phase instructions with the current application. They supersede conflicting older ideas; treat source statements that describe an item as incomplete as evidence of outstanding work, not a claim of implementation. Also honor [the explicit founding-account and test-first owner decisions](docs/requirements/OWNER_DECISIONS_2026-10-24.md), including the founder's **optional** full name and prohibition on ever linking the GeneralAdmin to a roster PersonId.

**DO NOT** implement exception 26 (dismissed person's reused number (ع) and prior-history reset) without a new, explicit owner decision specifying how to preserve immutable PersonId-linked history, movement, attendance, audit hashes, reports and legal evidence. Never silently convert historical deletion into an assumed permission. Continue independent, non-conflicting phases and report any further owner-level conflict before applying it.
