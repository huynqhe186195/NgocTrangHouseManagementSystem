# D04-B protected bank-proof archive — isolated engineering gate (2026-11-08)

## Proven starting point

Owner-confirmed local full isolated D04 suite PASS for source `7249c9f20b88332f33fcd2dc91858654c71d6975`, log folder `C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261108-170132`. No production database, live payOS payment, bank transfer or official D04 posting. The **D04-B BUSINESS HOLD remains** for native ABP/OpenIddict login, secure durable independent bank evidence, forced process-kill recovery, and least-privilege PostgreSQL roles.

This is a **separate experimental branch** from the green PR #7 baseline. No production service, Host DI registrations, financial writer, ABP Identity schema, or test script changes have been made here.

## Exact gap in current service

`PayOsD04ManualEvidenceService.RecordIndependentBankReviewAsync` currently hashes the uploaded bytes server-side, validates a format magic prefix, and stores the hash together with an **unverified client-submitted** `ProtectedArchiveReference`. It does not persist or retrieve the uploaded file. The HTTP controller currently reads at most 5 MiB into memory and forwards both the bytes and caller-supplied reference. This is not proof of a protected, durable, independently audited bank statement. Passing HTTP, ledger, and ACK tests does not close this gap.

## First isolated crypto rehearsal (source-only, NOT executed yet)

`test/PayOs.Staging.Persistence.Tests/D04BDisposableProtectedEvidenceVault_Tests.cs` contains an **internal, test-only**, file-backed vault. It uses a caller-supplied 32-byte key (no key in repository), AES-256-GCM with random nonce/auth tag, immutable content-addressed-by-keyed-HMAC filenames, server-computed SHA-256, and additional authenticated data binding the encrypted statement to the **exact intent ID + reviewer ID + document hash**. New files are written to random temporary files in the same isolated scratch directory, flushed with WriteThrough, then renamed without overwrite. Duplicate submissions must decrypt and match the exact bytes. Readback checks the authenticated tag and digest. The tests exercise ciphertext-at-rest, exact readback, idempotent write, other-reviewer/other-intent denial, path-traversal reference refusal, tamper detection, wrong-key refusal, and identical-key readback across a new vault instance.

**IMPORTANT: This is an isolated crypto/immutable-storage prototype, not yet an operational archive.** It has no Host DI registration, no upload controller integration, no physical bank evidence or external bank verification. The local temp directory is not a dedicated protected storage volume; the throwaway key is not KMS-managed; there is no explicit Windows/Linux ACL assurance, backup/restore, antivirus/malware scanning, retention/expiry/legal hold, disaster recovery, audit trail, or cross-transaction archive-to-financial-journal reconciliation. The code itself is not evidence that any of those controls exist.

## Acceptance design for the real feature

1. **Server-issued immutable archive reference.** Do not trust a reference string from HTTP clients. The server must write/retrieve the file under a protected service identity before accepting a reviewer attestation. The immutable review row must persist the server-issued reference and SHA-256; if the file cannot be durably reread and hash-verified, fail closed and record **no bank review**.
2. **Independent human approval.** The reviewer must inspect a genuine bank statement sourced independently of payOS, match bank transaction and amount, and attest through native ABP authentication and live permissions. A different Owner must approve after verifying the exact immutable digest/intent/transaction evidence. No signed test cookies or fake PDF files in operational acceptance.
3. **Durability and rollback.** Store first, verify readback, then append the SQL bank review under the current SERIALIZABLE scope. Any crash after file storage and before SQL may leave a harmless orphan that must be journaled and safely reconciled; a missing file must never produce a posted Payment. Never commit a money write merely because an archive reference exists.
4. **Key management and authorization.** Bind keys to an external, managed secrets service; use rotation with old-version readback; never print raw bank proof, plaintext, access token or key in logs. Restrict storage filesystem/object ACLs, service IAM permissions, and read access to authorized bank reviewers/auditors. Prohibit unauthenticated public URLs.
5. **Storage operational security.** Verify antivirus/scanning, PDF/PNG/JPEG safety (magic bytes alone are insufficient), maximum size, quarantine, backups and disaster recovery, retention period and deletion/legal hold with a written policy. Protect from directory junction/symlink attacks and malicious volume mounts in production.
6. **Gates before Business Accepted.** Native ABP/OpenIddict login with distinct accounts; real protected bank-proof upload and readback through HTTP; revoked role/membership race; forced process crash/restart and ACK idempotence; least-privilege database role; redacted logs; independent bank-statement authenticity and human approval. All must run on dedicated nonproduction test data before any operational approval.

## Test/disposition

This new isolated vault rehearsal has **NOT** been compiled or executed on the owner's .NET/PostgreSQL runner. It does not change the PASS status of PR #7 at its previously verified source. No new operational capability is claimed. D04-B BUSINESS ACCEPTANCE **HOLD**; no production or live bank activity. The new branch should only be merged into the staging proposal after code review and a targeted sandbox test of `FullyQualifiedName~D04BDisposableProtectedEvidenceVault_Tests` followed by a full isolated regression if it gets integrated into the D04-B workflow.
