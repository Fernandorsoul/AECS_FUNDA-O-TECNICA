# ADR-008: Authenticated Evidence Envelope

**Status:** Accepted

**Date:** 2026-08-30

## Context

The staged pipeline persisted complete execution evidence and rechecked the candidate diff hash before promotion. The JSON document itself remained mutable, however: an actor able to rewrite the diff, its hash, and the final decision together could create a self-consistent but untrusted record.

Promotion events also need to remain attributable after the initial execution. Rewriting the whole document atomically prevents torn writes, but atomicity alone does not provide authenticity or tamper evidence.

## Decision

Persist evidence in a versioned authenticated envelope shared by JSON and PostgreSQL:

- canonicalize the complete initial execution payload;
- hash it with SHA-256 and sign it with RSA-PSS/SHA-256;
- identify keys by the SHA-256 of their SubjectPublicKeyInfo representation;
- append promotion/export records as individually signed events linked to the previous signature;
- maintain a signed chain head over the event count and last signature;
- reject unsigned, unknown-schema, malformed, duplicate-property, untrusted-key, or invalid-signature evidence;
- keep private keys outside both the target repository and the evidence payload;
- retain historical public keys across signing-key rotation.

Each store verifies the envelope before returning `ExecutionEvidence`. Security-sensitive consumers therefore cannot accidentally opt out of verification while using `IExecutionEvidenceStore`.

## Consequences

- Coordinated edits to the diff, hash, decision, approval, or event order fail closed.
- Promotion and export stop before repository mutation when evidence validation fails.
- Existing unsigned JSON evidence is intentionally not eligible for promotion.
- Operators must protect and back up the keyring and retain old public keys.
- RSA-PSS signatures are intentionally nondeterministic; the canonical payload hash remains the stable content identifier.
- Replacing a complete JSON file, or restoring database and keyring together to a previous valid state, is not detectable without an independent monotonic anchor.

## Alternatives considered

### Trust only the candidate diff hash

Rejected because an attacker can replace both the diff and its stored hash.

### HMAC

Rejected as the default because every verifier would need the signing secret. Asymmetric signatures allow verification with public material while keeping signing authority separate.

### Sign the mutable JSON document after every update

Rejected because it erases the append history. Individually signed events retain ordering and provenance; the signed chain head additionally detects simple tail deletion.
