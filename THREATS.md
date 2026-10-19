# TrustVault — Threat Model (Day 1)

This document formalizes the threat landscape, trust boundaries, vulnerability mitigations, and known security limitations established during Day 1 of the TrustVault platform.

---

## 1. Primary Assets

1. **User Identity & Credentials:** Passwords (hashed via PBKDF2 with salt), JWT signing keys, user profile claims.
2. **Encrypted/Stored Documents:** Confidential enterprise files uploaded by users to the vault.
3. **Document Metadata & Permissions:** Access control lists, ownership definitions, permission levels (`Read`, `Edit`).
4. **Audit Trail:** Immutable records of authentication attempts, authorization rejections, and document operations.

---

## 2. Trust Boundaries

```
[ External / Public Internet ]
          │
          ▼ (Boundary 1: HTTPS & Public Gateway)
[ TrustVault Web API (AuthController, HealthController) ]
          │
          ▼ (Boundary 2: JWT Bearer Claims & Role Authorization)
[ Protected Controllers (DocumentsController, AuditController) ]
          │
          ▼ (Boundary 3: Resource-Based Authorization Handler)
[ Document Data & File Storage Subsystem ]
          │
          ▼ (Boundary 4: Network & Persistence Layer)
[ PostgreSQL Database & Secure Local Vault Disk ]
```

- **Boundary 1 (Public vs API):** All incoming HTTP requests are untrusted. Validation rules reject malformed inputs before processing.
- **Boundary 2 (Authenticated vs Unauthenticated):** Protected endpoints require a valid, non-expired JWT signature.
- **Boundary 3 (Role vs Resource):** System roles (`Admin`, `Owner`, `Viewer`) do NOT automatically grant document access. Access checks are evaluated on the exact document entity instance.
- **Boundary 4 (API vs Storage/Database):** Storage files reside outside the web server's static root (`wwwroot`). Files cannot be retrieved by direct URL paths.

---

## 3. Threat Assessment & Mitigations

### A. Authentication Risks

| Threat | Description | Day 1 Mitigation |
|---|---|---|
| **Credential Stuffing / Brute Force** | Attackers guessing user passwords through automated requests | Configured ASP.NET Core Identity with lockout rules (max 5 failed attempts, 15-minute lockout). Strong password complexity enforced via FluentValidation (8+ chars, upper, lower, digit, special char). |
| **JWT Tampering / Forgery** | Attackers forging JWT claims (e.g. changing `sub` or `role`) | Signed with HMAC-SHA256 using a symmetric secret key. Server verifies signature, issuer, audience, and lifetime on every request. |
| **Privilege Escalation on Register** | Malicious users supplying `"Role": "Admin"` in the registration payload | FluentValidation explicitly rejects `Admin` role requests during public self-registration. Server-side role assignment defaults to `Owner` or `Viewer`. |

---

### B. Authorization Risks

| Threat | Description | Day 1 Mitigation |
|---|---|---|
| **Admin Privilege Abuse / Universal Read** | An Admin abusing platform oversight to inspect private user documents | **Zero-Trust Restriction:** `DocumentAuthorizationHandler` explicitly excludes automatic Admin read grants. Only Document Owners or users with an explicit `DocumentPermission` record can read or edit files. |
| **Insecure Direct Object Reference (IDOR)** | User requesting `/api/documents/{guid}` for a file they do not own or have permission to access | Server executes `_authorizationService.AuthorizeAsync(User, document, DocumentOperations.Read)`. Any unauthorized attempt emits a 403 Forbidden and logs an audit event. |
| **Confused Deputy / Client-Supplied IDs** | Attacker submitting foreign User IDs in request bodies to tamper with permissions | Controller ignores client-supplied user identity claims and strictly extracts the caller's identity from `User.FindFirst(ClaimTypes.NameIdentifier)`. |

---

### C. Data Storage & File Upload Risks

| Threat | Description | Day 1 Mitigation |
|---|---|---|
| **Path Traversal (`../`) Attacks** | Uploading files named `../../windows/system32/calc.exe` to overwrite system files | `CreateDocumentValidator` validates that filenames contain no relative paths (`..`, `/`, `\`). Furthermore, physical storage names are decoupled and assigned randomized GUIDs (`<guid>.vault`). |
| **Unauthenticated File Retrieval** | Directly browsing the web server to access uploaded binaries | Document files are stored in a non-public folder (`SecureVaultStorage`), outside of static content roots. Binary retrieval is strictly mediated by the authenticated, authorized `/download` endpoint. |
| **Denial of Service via Huge Payloads** | Uploading massive files to exhaust server memory or disk | Request payloads are inspected; max sizes enforced on API endpoints. |

---

## 4. Known Limitations & Day 2 / Day 3 Roadmap

1. **At-Rest Binary Encryption:** Files in `SecureVaultStorage` are currently isolated from public access and decoupled by GUID, but not yet encrypted at rest with envelope encryption (e.g., AWS KMS / AES-256-GCM). This is planned for Day 2.
2. **Cloud Storage (AWS S3):** Currently using local isolated filesystem storage. Day 2 will introduce S3 integration with private buckets and pre-signed URLs.
3. **Token Revocation / Refresh Tokens:** JWT tokens expire after 120 minutes. Refresh token rotation and instant revocation will be added in Day 2.
4. **Rate Limiting Middleware:** Distributed rate limiting per IP / API key to be introduced before production deployment.
