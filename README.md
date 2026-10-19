# TrustVault — Day 1 Core Implementation

TrustVault is a zero-trust secure document management and vaulting platform designed for strict access governance, granular resource-based authorization, and tamper-evident audit logging.

---

## 🛠 Technology Stack

- **Backend:** ASP.NET Core Web API 8.0 (C#)
- **Database & ORM:** PostgreSQL 16+ with Entity Framework Core 8.0 (`Npgsql.EntityFrameworkCore.PostgreSQL`)
- **Identity & Security:** ASP.NET Core Identity with PBKDF2 password hashing & HMAC-SHA256 JWT Bearer authentication
- **Authorization:** Reusable Role policies (`Admin`, `Owner`, `Viewer`) and Resource-Based Authorization handlers enforcing zero-trust document privacy
- **Validation:** FluentValidation with strict sanitization and path traversal defenses
- **API Documentation:** Swagger / OpenAPI with interactive Bearer token authentication
- **Automated Testing:** xUnit, FluentAssertions, Moq, EF Core InMemory Test Harness
- **Frontend:** React 19 + TypeScript + Vite with native CSS design system
- **Containerization:** Docker & Docker Compose

---

## 📋 Prerequisites

Before running the project locally, ensure you have:
1. **.NET 8 SDK** (v8.0.426 or later)
2. **Node.js** (v20+ or v24+) and **npm**
3. **PostgreSQL** (running locally on port `5432` or via Docker)
4. *(Optional)* **Docker & Docker Compose** for containerized database execution

---

## ⚙️ Configuration & Environment Separation

TrustVault uses configuration separation to prevent sensitive keys or production credentials from leaking into Git repositories:

- Development default values reside in `backend/TrustVault.Api/appsettings.json` and `appsettings.Development.json`.
- Production and secret overrides must be supplied via environment variables or a `.env` file (see template in [`.env.example`](./.env.example)).

### Key Configuration Variables

| Variable | Description | Default (Dev) |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string | `Host=localhost;Port=5432;Database=trustvault_db;Username=postgres;Password=postgres;` |
| `Jwt__Issuer` | JWT Token Issuer claim | `TrustVault` |
| `Jwt__Audience` | JWT Token Audience claim | `TrustVaultUsers` |
| `Jwt__SecretKey` | HMAC-SHA256 Secret (Min 32 chars) | Configured in `appsettings.json` |
| `Storage__LocalVaultPath` | Non-web directory for vault storage | `SecureVaultStorage` |
| `SeedData__AdminEmail` | Initial Admin user email | `admin@trustvault.local` |
| `SeedData__AdminPassword` | Initial Admin strong password | `Admin#TrustVault2026!` |

---

## 🚀 Running Locally

### 1. Database Setup
Ensure PostgreSQL is running locally on port 5432. Alternatively, start the containerized database:
```bash
docker compose up db -d
```

### 2. Apply Migrations & Seed Data
Migrations and initial role seeding execute automatically upon backend startup via `DbInitializer`. To apply migrations manually via CLI:
```bash
dotnet ef database update --project backend/TrustVault.Api/TrustVault.Api.csproj
```

### 3. Run the Backend API
```bash
cd backend/TrustVault.Api
dotnet run
```
- API Endpoint: `http://localhost:5115`
- Swagger UI Documentation: `http://localhost:5115/swagger`
- Health Check: `http://localhost:5115/api/health`

### 4. Run the Automated Tests
```bash
dotnet test tests/TrustVault.Tests/TrustVault.Tests.csproj --logger "console;verbosity=normal"
```

### 5. Run the Frontend (React + TypeScript)
```bash
cd frontend
npm install
npm run dev
```
- Frontend dev server starts at `http://localhost:5173`

---

## 🔐 Core Security Principles Enforced on Day 1

1. **Zero-Trust Admin Model:** An Admin **does not** automatically receive permission to read or modify arbitrary user documents. Admin privileges are restricted to audit logs, user management, and health checks.
2. **Resource-Based Authorization:** Document read and edit capabilities require document ownership or an explicit `DocumentPermission` record.
3. **Non-Public Storage Isolation:** Uploaded document binaries are decoupled from their original filenames, assigned cryptographically random GUID storage keys, and stored outside the web root (`SecureVaultStorage/`), never accessible via direct static URL.
4. **Input Sanitization & Path Traversal Guards:** File names are validated and prohibited from containing directory navigation patterns (`..`, `/`, `\`).
5. **No Secret Leaks:** Centralized exception handling presents RFC 7807 `ProblemDetails` to users without leaking stack traces or internal server details in non-development modes.
