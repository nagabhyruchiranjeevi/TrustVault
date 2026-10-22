# TrustVault — Comprehensive Threat Model & Security Posture

This document details the threat model, trust boundaries, vulnerability mitigations, audit integrity assumptions, and residual risks for the TrustVault zero-trust platform.

---

## 1. Primary Assets

1. **User Identity & Cryptographic Credentials:**
   - Salted PBKDF2 password hashes managed via ASP.NET Core Identity.
   - HMAC-SHA256 JWT signing keys (minimum 256 bits).
   - User claims (`sub`, `email`, `role`).
2. **Confidential Stored Documents:**
   - User files vaulted in private isolated storage (Local filesystem outside webroot or private S3 buckets).
   - Document metadata: titles, filenames, content types, byte sizes, owner mappings.
3. **Access Control Lists (ACLs):**
   - Granular `DocumentPermission` records (`Read`, `Edit`) granting least-privilege resource access.
4. **Audit Hash Chain:**
   - Chronological, tamper-evident record of all logins, uploads, permission grants, access revocations, downloads, and forbidden access attempts.

---

## 2. Trust Boundaries

```
[ External Untrusted Clients / Web Browsers ]
                     │
                     ▼ (Boundary 1: TLS 1.3 / HTTPS Gateway)
[ TrustVault Web API (Authentication & Model Validation Pipeline) ]
  - JWT Signature, Issuer, Audience, Lifetime Verification
  - FluentValidation: File size, path traversal sequences, prohibited extensions
                     │
                     ▼ (Boundary 2: Resource-Based Authorization Handler)
[ Document Governance Subsystem (DocumentAuthorizationHandler) ]
  - Checks Document.OwnerId == CurrentUserId
  - Checks explicit DocumentPermission (Read / Edit)
  - Zero-Trust Admin Restriction: Role != Document Access
                     │
                     ▼ (Boundary 3: Storage & Persistence Layer)
[ PostgreSQL / SQLite & Vault Binary Storage ]
  - Storage keys decoupled from untrusted filenames (<guid>.vault)
  - Soft-deletion flag immediately halts streaming
  - Cryptographic Hash Chain with DB unique constraint on SequenceNumber
```

- **Boundary 1 (Public Network vs API):** All incoming HTTP requests and payloads are untrusted. FluentValidation rejects invalid sizes, prohibited file extensions (`.exe`, `.sh`, `.bat`, etc.), and directory traversal patterns (`..`, `/`, `\`).
- **Boundary 2 (Role vs Resource):** System administrative roles (`Admin`) do **not** automatically grant permission to read or modify private documents. Document access is evaluated per document entity instance.
- **Boundary 3 (API vs Storage/Database):** Storage keys are cryptographically generated GUIDs (`<guid>.vault`). Files reside outside the static web root (`wwwroot`) and cannot be accessed via direct URL paths.

---

## 3. Threat Assessment & Implemented Mitigations

### A. Authentication & Session Risks

| Threat | Description | Implemented Mitigation |
|---|---|---|
| **Credential Stuffing / Brute Force** | Automated password guessing against login endpoints | ASP.NET Core Identity configured with 5-attempt lockout (15-minute lockout span). FluentValidation enforces uppercase, lowercase, numeric, and special character requirements. |
| **Token Forgery / Signature Bypass** | Attackers forging JWT claims or tampering with payloads | ASP.NET Core `JwtBearerHandler` validates HMAC-SHA256 signature, issuer (`TrustVault`), audience (`TrustVaultUsers`), expiration lifetime, and clock skew (30s max). |
| **Privilege Escalation on Self-Registration** | Malicious users submitting `"Role": "Admin"` during public sign-up | `RegisterRequestValidator` explicitly rejects the `Admin` role during self-registration. Role assignment is strictly bounded to `Owner` or `Viewer`. |

---

### B. Authorization & Access Control Risks

| Threat | Description | Implemented Mitigation |
|---|---|---|
| **Insecure Direct Object Reference (IDOR / BOLA)** | User requesting `/api/documents/{id}` for another user's document | Server executes `_authorizationService.AuthorizeAsync(User, document, DocumentOperations.Read)`. Any unauthorized attempt emits a `403 Forbidden` and logs a security audit event. |
| **Metadata & Existence Leakage via Search/List** | Unauthorized users discovering document titles or existence through search | Controller queries filter documents at the database query level (`Where(!d.IsDeleted && (d.OwnerId == currentUserId || d.Permissions.Any(...)))`). Unauthorized titles are never returned in search results. |
| **Admin Privilege Escalation / Snoop Attacks** | Administrative staff viewing private customer files | `DocumentAuthorizationHandler` explicitly excludes automatic Admin grants. Admins can view audit logs and health metrics, but cannot read or download document contents without explicit owner-granted permissions. |
| **Unauthorized Permission Grants / Revocations** | Non-owners attempting to share or revoke access to documents | `GrantPermission` and `RevokePermission` enforce `DocumentOperations.Share`, which strictly succeeds only if `CurrentUserId == document.OwnerId`. |
| **Self-Grant Access Bypass** | Users attempting to grant permissions to themselves | `GrantPermission` checks `targetUser.Id == CurrentUserId` and returns `400 Bad Request`. |
| **Permission Revocation Lag** | Revoked users continuing to download via cached tokens or presigned URLs | Authorization checks are evaluated server-side on **every request**. Downloads stream through the backend; no long-lived presigned URLs are issued for private documents. Revoking a permission causes the very next request to be denied (`403 Forbidden`). |

---

### C. File Upload & Binary Storage Risks

| Threat | Description | Implemented Mitigation |
|---|---|---|
| **Path Traversal Attacks (`../`)** | Uploading filenames like `../../etc/passwd` to overwrite system files | Input validation strips paths, and storage files are saved with randomized, unguessable names (`<guid>.vault`). |
| **Executable Script Uploads (Web Shells)** | Uploading `.exe`, `.dll`, `.sh`, `.bat`, or `.php` files | Extension blocklist and strict allowlist (`.pdf`, `.png`, `.jpg`, `.txt`, `.docx`, `.xlsx`, `.csv`, `.json`, `.xml`) reject dangerous file types. |
| **Direct URL Binary Retrieval** | Downloading vaulted files directly bypassing authorization | Files are stored in non-public storage directories outside `wwwroot` (or in private S3 buckets with `BlockPublicAccess=true`). Retrieval is strictly mediated by the authorized `/download` endpoint. |
| **Storage Retrieval Post-Deletion** | Downloading files after soft-deletion | Soft deletion marks `IsDeleted = true`. Document queries filter out deleted documents, immediately returning `404 Not Found` for downloads. Binary cleanup runs as a best-effort background task. |

---

## 4. Audit Hash Chain Integrity & Assumptions

### A. Implemented Cryptographic Mechanism
1. **Deterministic Canonical Serialization:** Every event serializes canonical fields in strict order:
   `PreviousHash|SequenceNumber|Id|TimestampUtc(o)|UserId|Action|EntityType|EntityId|Status|Details|CorrelationId`.
2. **SHA-256 Forward Chaining:** The genesis event links to `0000000000000000000000000000000000000000000000000000000000000000`. Each subsequent event's `CurrentHash` incorporates the preceding record's `CurrentHash`.
3. **Multi-Instance Concurrency Resilience:**
   - The database enforces a `UNIQUE` index on `SequenceNumber`.
   - In-process `SemaphoreSlim` serializes single-instance requests.
   - For multi-instance deployments, `AuditService.LogEventAsync` implements an **optimistic retry loop** (up to 5 attempts) catching `DbUpdateException`, re-reading the latest chain head, and re-computing hashes dynamically.

### B. Critical Security Assumptions & Limitations
> [!IMPORTANT]
> **DBA Historical Rewrite Limitation:**
> A cryptographic hash chain stored within a relational database guarantees tamper evidence against application-layer tampering, SQL injection updates, and external attackers.
> **However, a hash chain alone cannot prevent a privileged database administrator with direct SQL `UPDATE` / `DELETE` access from rewriting historical records and recalculating all subsequent hashes forward.**
>
> **Mitigation for Complete Non-Repudiation:**
> In high-assurance environments, the audit head hash must be periodically anchored to an external, write-once-read-many (WORM) storage mechanism, such as:
> 1. Amazon S3 Object Lock in Compliance Mode.
> 2. AWS CloudTrail / Amazon QLDB.
> 3. Public timestamping services or digital signatures using a hardware security module (HSM).

---

## 5. Residual Risks & Staging Roadmap

1. **At-Rest Storage Encryption:** Local development storage isolates binaries, but does not use envelope encryption. For AWS deployments, private S3 buckets must enforce SSE-KMS or SSE-S3 encryption.
2. **Rate Limiting:** Distributed rate limiting per client IP / User ID should be deployed at the API gateway or reverse proxy (e.g., AWS WAF / NGINX).
3. **Database Credentials:** Production environments must source database passwords and JWT secrets from AWS Secrets Manager rather than local files.
