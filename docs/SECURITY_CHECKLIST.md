# TrustVault — Security Checklist

> Version: 1.0.0
> Last Updated: 2026-10-22
> Applicable To: All deployment environments

---

## How to Use This Checklist

Review each item before deploying to staging or production. Items marked **[CRITICAL]** must be resolved before any public deployment. Items marked **[RECOMMENDED]** should be addressed for production hardening.

---

## 1. Authentication & Identity

### [CRITICAL] Password Security

- [ ] ASP.NET Core Identity PBKDF2 password hashing is active
- [ ] Minimum password length ≥ 8 characters enforced
- [ ] Uppercase, lowercase, digit, and special character requirements enforced
- [ ] Account lockout enabled (5 attempts, 15-minute lockout span)
- [ ] Admin self-registration via `/api/Auth/register` is rejected (`RegisterRequestValidator`)

### [CRITICAL] JWT Token Security

- [ ] `Jwt__SecretKey` is at least 32 characters (256 bits for HMAC-SHA256)
- [ ] `Jwt__SecretKey` is NOT the development default — replace with a cryptographically random string
- [ ] `Jwt__Issuer` and `Jwt__Audience` are validated on every request
- [ ] Token expiration (`Jwt__ExpiryMinutes`) is set to a reasonable value (default: 120 min)
- [ ] Clock skew tolerance is limited (max 30 seconds)
- [ ] Tokens are transmitted only via `Authorization: Bearer` headers (no URL query parameters)

### [RECOMMENDED] Session Management

- [ ] Frontend clears JWT from `localStorage` on logout
- [ ] No long-lived refresh tokens are issued (current design: single JWT per session)
- [ ] Consider implementing token rotation for extended sessions

---

## 2. Authorization & Access Control

### [CRITICAL] Resource-Based Authorization

- [ ] `DocumentAuthorizationHandler` enforces per-document access for all read/edit/delete/share operations
- [ ] Document owner identity is verified server-side (not trusted from client)
- [ ] Admin role does NOT automatically grant document read/edit access
- [ ] Self-grant permission (`owner grants to self`) is rejected with `400 Bad Request`

### [CRITICAL] Zero-Leakage Data Access

- [ ] Document list (`GET /api/Documents`) filters by ownership and active permissions at the database query level
- [ ] Search (`GET /api/Documents/search`) never returns titles or metadata for unauthorized documents
- [ ] Soft-deleted documents return `404 Not Found` immediately
- [ ] Revoked permissions take effect on the next API request (no cached access)

### [CRITICAL] Permission Management

- [ ] Only document owners can grant or revoke permissions
- [ ] Permission grants require valid target user email (validated server-side)
- [ ] Permission revocation is immediate and verified by automated tests
- [ ] Unauthorized permission modification attempts return `403 Forbidden`

---

## 3. File Upload & Storage Security

### [CRITICAL] Upload Validation

- [ ] Path traversal sequences (`../`, `..\\`, `/`, `\\`) are rejected by FluentValidation
- [ ] File extension allowlist enforced: `.pdf`, `.png`, `.jpg`, `.txt`, `.docx`, `.xlsx`, `.csv`, `.json`, `.xml`
- [ ] Dangerous extensions blocked: `.exe`, `.dll`, `.sh`, `.bat`, `.cmd`, `.ps1`, `.php`, `.jsp`
- [ ] File size limits are enforced (configurable, default: reasonable limit)
- [ ] Uploaded content type is validated

### [CRITICAL] Storage Isolation

- [ ] Files are stored with unguessable GUID storage keys (`<guid>.vault`)
- [ ] Storage directory is outside the web root (`wwwroot`)
- [ ] No direct URL access to stored files (all access mediated by authorized API endpoints)
- [ ] Soft-deleted documents cannot be retrieved or downloaded

### [RECOMMENDED] Production Storage

- [ ] AWS S3 bucket has `BlockPublicAccess` enabled (all four settings: `true`)
- [ ] Server-side encryption (SSE-S3 or SSE-KMS) is enabled
- [ ] S3 bucket policy restricts access to the ECS task execution role only
- [ ] No pre-signed URLs with long expiration times

---

## 4. Cryptographic Audit Chain

### [CRITICAL] Hash Chain Integrity

- [ ] SHA-256 hash algorithm is used for all audit event hashes
- [ ] Forward-linked chain: each event's `CurrentHash` incorporates `PreviousHash`
- [ ] Genesis event links to `0000000000000000000000000000000000000000000000000000000000000000`
- [ ] Deterministic canonical serialization produces consistent hashes
- [ ] Database unique constraint on `SequenceNumber` prevents duplicates

### [CRITICAL] Concurrency Safety

- [ ] `SemaphoreSlim` serializes single-instance audit writes
- [ ] Optimistic retry loop (5 attempts) handles multi-instance `DbUpdateException`
- [ ] Chain verification endpoint (`GET /api/Audit/verify`) validates full chain integrity

### [RECOMMENDED] External Anchoring

- [ ] For high-assurance environments, periodically anchor head hash to external WORM storage
- [ ] Consider: AWS S3 Object Lock (Compliance Mode), AWS CloudTrail, or QLDB
- [ ] Document the anchoring frequency and verification procedure

---

## 5. API Security

### [CRITICAL] Input Validation

- [ ] All API endpoints use FluentValidation for request model validation
- [ ] Error responses do not leak internal implementation details
- [ ] Stack traces are suppressed in Production environment

### [CRITICAL] CORS Configuration

- [ ] Development: `http://localhost:5173` only
- [ ] Production: Restrict to exact production domain origin
- [ ] Wildcard (`*`) CORS origins are NOT used in production

### [RECOMMENDED] Rate Limiting

- [ ] Implement rate limiting on authentication endpoints (`/api/Auth/login`, `/api/Auth/register`)
- [ ] Implement rate limiting on file upload endpoints
- [ ] Consider using AWS WAF, Nginx rate limiting, or ASP.NET Core rate limiting middleware

### [RECOMMENDED] HTTP Security Headers

- [ ] `X-Content-Type-Options: nosniff`
- [ ] `X-Frame-Options: DENY`
- [ ] `Strict-Transport-Security: max-age=31536000; includeSubDomains`
- [ ] `Content-Security-Policy: default-src 'self'`
- [ ] `Referrer-Policy: strict-origin-when-cross-origin`

---

## 6. Infrastructure Security

### [CRITICAL] Secrets Management

- [ ] `Jwt__SecretKey` is NOT stored in `appsettings.json` for production
- [ ] Database passwords are NOT in docker-compose or source code for production
- [ ] Use AWS Secrets Manager or Systems Manager Parameter Store
- [ ] Rotate secrets on a defined schedule (minimum: annually)

### [CRITICAL] Network Security

- [ ] Production API server is in a private subnet (not directly internet-accessible)
- [ ] ALB terminates TLS 1.2+ with a valid certificate
- [ ] Database (RDS) is in a private subnet with no public access
- [ ] Security groups restrict access to minimum required ports

### [CRITICAL] Git Hygiene

- [ ] `.gitignore` excludes `*.db`, `**/*.db` (SQLite databases)
- [ ] `.gitignore` excludes `SecureVaultStorage/`, `**/SecureVaultStorage/`
- [ ] `.gitignore` excludes `.env` files (preserves `.env.example`)
- [ ] `.gitignore` excludes `node_modules/`, `bin/`, `obj/`
- [ ] No secrets or private keys are committed to the repository
- [ ] Git history does not contain previously committed secrets (audit with `git log -p`)

---

## 7. Database Security

### [CRITICAL] Access Control

- [ ] Database user has minimum required privileges (not superuser in production)
- [ ] Connection strings use SSL mode in production (`SslMode=Require`)
- [ ] Database is not exposed to the public internet

### [CRITICAL] Data Protection

- [ ] RDS encryption at rest is enabled
- [ ] Database backups are enabled with appropriate retention period
- [ ] Sensitive data columns are indexed appropriately for query performance

### [RECOMMENDED] Monitoring

- [ ] Enable PostgreSQL query logging for security audit
- [ ] Monitor failed login attempts to the database
- [ ] Set up alerts for unusual query patterns

---

## 8. Frontend Security

### [CRITICAL] Authentication Flow

- [ ] JWT is stored in `localStorage` (acceptable for SPA with XSS mitigations)
- [ ] JWT is cleared on logout
- [ ] Expired tokens are handled gracefully (redirect to login)
- [ ] API error responses do not expose sensitive information to the client

### [RECOMMENDED] Build Security

- [ ] Production build minifies and tree-shakes JavaScript
- [ ] No development debug information in production bundle
- [ ] Source maps are NOT deployed to production
- [ ] Dependencies are audited with `npm audit`

---

## 9. Automated Test Coverage

### [CRITICAL] Security Tests (Must Pass)

| Test | Description | Status |
|---|---|---|
| Path traversal blocking | 4 patterns tested | ✅ |
| Password strength rules | 6 variations tested | ✅ |
| Admin self-registration rejection | Direct validation | ✅ |
| JWT claim embedding | userId, email, roles | ✅ |
| Auth endpoint security | Valid/invalid claims | ✅ |
| Owner authorization | Read + edit | ✅ |
| Read-only permission | Read OK, edit denied | ✅ |
| Edit permission | Read + edit OK | ✅ |
| Revoked permission denial | Immediate effect | ✅ |
| Admin non-inheritance | No automatic document access | ✅ |
| Unrelated user denial | Zero access | ✅ |
| Zero-leakage search | No metadata exposure | ✅ |
| Immediate revocation | Next-request denial | ✅ |
| Soft-delete prevention | Immediate 404 | ✅ |
| Self-grant rejection | 400 Bad Request | ✅ |
| Storage key decoupling | GUID independence | ✅ |
| Unauthorized permission modification | 403 Forbidden | ✅ |
| Hash chain determinism | Consistent hashes | ✅ |
| Tamper detection (details) | Modified event detected | ✅ |
| Tamper detection (chain) | Broken link detected | ✅ |
| Concurrent write safety | Chain integrity under load | ✅ |
| Chain verification | Full chain valid | ✅ |

**Total: 31/31 tests passing**

---

## 10. Deployment Verification

### Pre-Deployment Checklist

- [ ] All 31 automated tests pass (`dotnet test`)
- [ ] Frontend builds without errors (`npm run build`)
- [ ] Backend builds with 0 warnings (`dotnet build`)
- [ ] Health endpoint responds correctly (`GET /api/health`)
- [ ] Admin can log in and access audit trail
- [ ] Owner can upload, share, and revoke documents
- [ ] Unauthorized access is denied (403/404)
- [ ] Hash chain verification returns `isValid: true`

### Post-Deployment Checklist

- [ ] HTTPS is enforced on all endpoints
- [ ] Health check is configured in ALB/ECS
- [ ] CloudWatch log group is receiving logs
- [ ] Initial admin password has been changed
- [ ] Database migrations have been applied
- [ ] S3 bucket encryption is verified
- [ ] Security group rules have been reviewed
- [ ] Cleanup instructions are documented and accessible

---

## Version History

| Version | Date | Changes |
|---|---|---|
| 1.0.0 | 2026-10-22 | Initial security checklist |
