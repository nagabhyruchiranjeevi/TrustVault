# TrustVault — Zero-Trust Secure Document Vault

TrustVault is an enterprise zero-trust secure document management and vaulting platform designed with strict access governance, granular resource-based authorization, unguessable isolated storage, and a tamper-evident cryptographic audit hash chain.

---

## 🏛 System Architecture

```
                    ┌──────────────────────────────────────────────┐
                    │      React 19 + TypeScript (Vite SPA)        │
                    │  - Real backend HTTP calls (No mock delays)  │
                    │  - Permission management & Revocation UI     │
                    │  - Zero-leakage search & Audit verification  │
                    └──────────────────────┬───────────────────────┘
                                           │ HTTPS (Bearer JWT)
                                           ▼
                    ┌──────────────────────────────────────────────┐
                    │          TrustVault.Api (ASP.NET Core 8)     │
                    │  - TokenValidation: Issuer, Audience, Key    │
                    │  - Resource Authorization Handlers           │
                    │  - FluentValidation & Path Traversal Guards  │
                    │  - Deterministic SHA-256 Hash Chain Service  │
                    └──────────────┬───────────────────────────────┘
                                   │
              ┌────────────────────┴────────────────────┐
              ▼                                         ▼
┌──────────────────────────────┐          ┌──────────────────────────────┐
│       Database Layer         │          │     Document Storage         │
│ - PostgreSQL 16+ (Production)│          │ - Isolated Local Vault       │
│ - SQLite (Dev / Test)        │          │   (Outside webroot)          │
│ - Unique Index on SeqNumber  │          │ - AWS S3 Private Bucket      │
│ - Optimistic Concurrency     │          │   (Server-Side AES256)       │
└──────────────────────────────┘          └──────────────────────────────┘
```

---

## 🛠 Technology Stack

- **Backend:** ASP.NET Core Web API 8.0 (C# 12)
- **Database & ORM:** PostgreSQL 16+ via `Npgsql.EntityFrameworkCore.PostgreSQL` 8.0.4; SQLite development provider via `Microsoft.EntityFrameworkCore.Sqlite` 8.0.8
- **Identity & Security:** ASP.NET Core Identity with PBKDF2 password hashing & HMAC-SHA256 JWT Bearer authentication
- **Authorization:** Granular resource-based authorization (`IAuthorizationHandler<OperationAuthorizationRequirement, Document>`) enforcing zero-trust document privacy
- **Audit Integrity:** SHA-256 forward-linked cryptographic hash chain with deterministic canonical serialization and multi-instance concurrency retry
- **Validation:** FluentValidation with path traversal defenses, size limits, and file extension allowlists
- **API Documentation:** Swagger / OpenAPI with interactive Bearer token authentication
- **Automated Testing:** xUnit, FluentAssertions, Moq, EF Core InMemory Test Harness (31 passing automated tests)
- **Frontend:** React 19 + TypeScript + Vite with native CSS design system

---

## 📋 Prerequisites

1. **.NET 8 SDK** (v8.0.426 or later)
2. **Node.js** (v20+ or v24+) and **npm**
3. **PostgreSQL** (running locally on port `5432` or via Docker) or SQLite for local development
4. **AWS CLI v2** (optional, for staging/production cloud deployment)

---

## ⚙️ Configuration & Environment Separation

TrustVault decouples configuration from code:

- **Local Development Defaults:** `backend/TrustVault.Api/appsettings.json`
- **Environment Overrides:** Supply via environment variables or a `.env` file (template: [`.env.example`](./.env.example)).

### Key Configuration Variables

| Variable | Description | Default (Dev) |
|---|---|---|
| `DatabaseProvider` | Provider selector (`Sqlite` or `PostgreSQL`) | `Sqlite` |
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string | `Host=localhost;Port=5432;Database=trustvault_db;Username=postgres;Password=postgres;` |
| `ConnectionStrings__SqliteConnection` | SQLite connection string | `Data Source=trustvault.db` |
| `Jwt__Issuer` | JWT Token Issuer claim | `TrustVault` |
| `Jwt__Audience` | JWT Token Audience claim | `TrustVaultUsers` |
| `Jwt__SecretKey` | HMAC-SHA256 Secret (Min 32 chars) | Managed via secure config / env var |
| `Jwt__ExpiryMinutes` | Token expiration duration | `120` |
| `Storage__Provider` | Storage provider selector (`Local` or `S3`) | `Local` |
| `Storage__LocalVaultPath` | Directory for vault storage outside web root | `SecureVaultStorage` |
| `Storage__S3BucketName` | Private AWS S3 bucket name | `trustvault-private-vault` |
| `SeedData__AdminEmail` | Initial Admin user email | `admin@trustvault.local` |
| `SeedData__AdminPassword` | Initial Admin strong password | `Admin#TrustVault2026!` |

---

## 🚀 Local Setup & Running Instructions

### 1. Database Setup

#### Option A: PostgreSQL Setup (Production-Identical)
If PostgreSQL is running locally on port 5432:
```bash
# Set PostgreSQL environment variables in your shell:
$env:DatabaseProvider = "PostgreSQL"
$env:ConnectionStrings__DefaultConnection = "Host=localhost;Port=5432;Database=trustvault_db;Username=postgres;Password=YOUR_ACTUAL_PASSWORD;"

# Apply EF Core migrations to PostgreSQL:
dotnet ef database update --project backend/TrustVault.Api/TrustVault.Api.csproj
```

#### Option B: SQLite Development Storage (Zero-Configuration)
```bash
# Default mode: requires no external database server
$env:DatabaseProvider = "Sqlite"
```

### 2. Run the Backend API
```bash
cd backend/TrustVault.Api
dotnet run
```
- API Base URL: `http://localhost:5115`
- Swagger UI Documentation: `http://localhost:5115/swagger`
- Health Check: `http://localhost:5115/api/health`

### 3. Run Automated Tests
```bash
dotnet test tests/TrustVault.Tests/TrustVault.Tests.csproj --logger "console;verbosity=normal"
```

### 4. Run the Frontend (React 19 + TypeScript)
```bash
cd frontend
npm install
npm run dev
```
- Frontend dev server starts at `http://localhost:5173`

---

## 📡 Complete API Endpoint Reference

| Method | Endpoint | Authorization | Description |
|---|---|---|---|
| `POST` | `/api/Auth/register` | Anonymous | Registers a new user with strong password enforcement. Rejects self-escalation to `Admin`. |
| `POST` | `/api/Auth/login` | Anonymous | Authenticates credentials and issues signed HMAC-SHA256 JWT bearer token. |
| `GET` | `/api/Auth/me` | Authenticated | Retrieves the authenticated user's profile and assigned roles. |
| `GET` | `/api/Documents` | Authenticated | Lists only documents owned by or explicitly shared with caller. |
| `GET` | `/api/Documents/search` | Authenticated | Searches titles/filenames. Zero-leakage: never returns unauthorized metadata. |
| `POST` | `/api/Documents/upload` | Authenticated | Multipart file upload with size validation, extension allowlists, and unguessable GUID storage keys. |
| `POST` | `/api/Documents` | Authenticated | Base64 JSON upload for programmatic API clients. |
| `GET` | `/api/Documents/{id}` | Read Permitted | Returns document metadata if caller is Owner or has active Read/Edit permission. |
| `GET` | `/api/Documents/{id}/download` | Read Permitted | Streams document binary directly after real-time authorization check. |
| `PATCH`| `/api/Documents/{id}/metadata`| Edit Permitted | Updates document metadata (title). Viewers are denied. |
| `DELETE`| `/api/Documents/{id}` | Owner Only | Soft-deletes document. Immediately denies all subsequent reads/downloads. |
| `GET` | `/api/Documents/{id}/permissions` | Owner Only | Lists active user permissions for a document. |
| `POST`| `/api/Documents/{id}/permissions` | Owner Only | Grants Read or Edit permission to a target user email. Rejects self-grants. |
| `DELETE`| `/api/Documents/{id}/permissions/{targetUserId}`| Owner Only | Revokes permission immediately. Subsequent downloads are denied on the next request. |
| `GET` | `/api/Audit` | Admin Only | Lists system audit trail with cryptographic hash fingerprints. |
| `GET` | `/api/Audit/verify` | Admin Only | Verifies the cryptographic integrity of the SHA-256 audit hash chain. |
| `GET` | `/api/Audit/me` | Authenticated | Returns audit events for the authenticated caller only (hashes withheld). |
| `GET` | `/api/Health` | Anonymous | Health check reporting service status, environment, and database connectivity. |

---

## 🛡️ End-to-End Security Verification Workflow (9 Steps)

1. **Login as Owner:** Sign in with an Owner account (or register `owner@trustvault.local`).
2. **Upload Sample Document:** Deposit a confidential PDF or text file into the vault.
3. **Grant Read Permission:** Open "Manage Access", enter `viewer@trustvault.local`, and grant `Read` access.
4. **Login as Viewer:** Sign in with `viewer@trustvault.local`.
5. **Download Permitted Document:** Verify the document appears in the vault list and downloads successfully.
6. **Verify Unrelated User Denied:** Log in as an unrelated user (`stranger@trustvault.local`) and verify:
   - Search returns 0 results (no title/filename leakage).
   - Direct download URL returns `403 Forbidden`.
   - Admin user attempting download without explicit permission is denied `403 Forbidden`.
7. **Revoke Viewer Permission:** Log back in as Owner, open "Manage Access", and click "Revoke" for `viewer@trustvault.local`.
8. **Verify Next Download Denied:** Switch back to `viewer@trustvault.local` and attempt download. The server rejects the request immediately with `HTTP 403 Forbidden`.
9. **Show Audit Events:** Log in as `admin@trustvault.local`, open "System Audit Trail", click "Verify Hash Chain Integrity", and observe that all events (Upload, GrantPermission, Download, RevokePermission, DownloadDenied) are cryptographically chained and intact.

---

## ☁️ AWS Deployment Preparation & Architecture

### Architecture Overview
- **Storage:** Private S3 Bucket with `BlockPublicAccess=true`, SSE-S3 AES-256 encryption, and IAM bucket policy restricting access exclusively to backend execution roles.
- **Compute:** AWS ECS Fargate container running `TrustVault.Api` inside a private subnet behind an Application Load Balancer (ALB).
- **Database:** Amazon RDS PostgreSQL (db.t4g.micro for cost-efficiency) inside private database subnets with encryption at rest.
- **Secrets Management:** AWS Secrets Manager or Systems Manager Parameter Store for database passwords and JWT signing keys.
- **Logging:** Amazon CloudWatch Logs for audit trail and container logs.

### AWS Resource & Cost Estimates
- **Private S3 Bucket:** ~$0.023/GB-month (minimal cost for document storage).
- **ECS Fargate (0.25 vCPU, 0.5 GB RAM):** ~$9–$15/month if continuously running.
- **RDS PostgreSQL (db.t4g.micro):** ~$15–$18/month (or AWS Free Tier eligible for eligible accounts).
- **Policy:** **No AWS resources are provisioned or billed without explicit user approval.**

---

## 🧹 AWS Cleanup Instructions

If cloud resources are provisioned during deployment:
```bash
# 1. Empty and delete private S3 bucket
aws s3 rm s3://trustvault-private-vault --recursive
aws s3api delete-bucket --bucket trustvault-private-vault

# 2. Delete ECS Fargate service and task definition
aws ecs update-service --cluster trustvault-cluster --service trustvault-api-service --desired-count 0
aws ecs delete-service --cluster trustvault-cluster --service trustvault-api-service
aws ecr delete-repository --repository-name trustvault-api --force

# 3. Delete RDS database instance
aws rds delete-db-instance --db-instance-identifier trustvault-db --skip-final-snapshot
```

---

## ⚠️ Known Limitations & Assumptions

1. **Hash Chain Scope vs Privileged DBA:**
   The SHA-256 audit hash chain guarantees tamper evidence against application-level modifications or external attackers tampering with rows. However, a hash chain stored in the same relational database cannot prevent a privileged database administrator with direct SQL access from rewriting historical records and recalculating hashes forward. For absolute non-repudiation, audit event hashes should be periodically anchored to an external append-only ledger (e.g., AWS QLDB, Amazon S3 Object Lock in Compliance Mode, or public blockchain timestamps).
2. **PostgreSQL Local Password:**
   PostgreSQL 18 is running as a local Windows service, but requires setting the authorized password in `ConnectionStrings__DefaultConnection` or supplying credentials via environment variable to activate PostgreSQL mode over SQLite.
