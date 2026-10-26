# Mandatory rules for all coding agents

All contributors and agents implementing phases or optimizations of the AlTamam project MUST follow [the three-gate acceptance contract](docs/PERFORMANCE_SAFETY_GATE.md): **measured performance improvement, exact output parity, and passing Windows/security/integrity regression tests**, all tied to the exact Git SHA. Keep changes on review branches and do not merge while required checks are missing or failing. The contract applies to every phase, including future work. The listed speculative optimizations remain deferred until the full program is completed.

Never delete historical, audit, movement, attendance, mutation-receipt or other durable evidence during maintenance. Preview tokens are eligible for deletion only more than 30 days after expiry, after a conservative check for ALL existing references. Preserve all user-approved functional behavior, UI workflows, SQLCipher encryption, role enforcement, transactional atomicity and idempotency.
