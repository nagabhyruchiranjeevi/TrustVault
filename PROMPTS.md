# TrustVault — AI Interaction & Prompt Log

This document records the genuine user prompt, real dates, actual technical challenges encountered during development, and the engineering decisions taken to resolve them.

---

## Session 1: Day 1 Foundation & Architecture

**Date:** October 19, 2026  
**Environment:** Windows 11 Enterprise (AMD64), PowerShell, Node.js v24.11.1, PostgreSQL 18 Local Service  
**Model:** Gemini 3.8 Flash

### Prompt Received

```markdown
# TRUSTVAULT — DAY 1 DEVELOPMENT PROMPT

Act as my senior software engineer and technical mentor.

Today is Day 1 of my three-day TrustVault project. Focus on building a reliable foundation, implementing authentication, and publishing the actual completed work to GitHub.

I am still learning development and AWS CloudOps. Explain important commands and errors in simple language.

1. Inspect the environment
2. Initialize the project
3. Implement the backend (ASP.NET Core Web API, C#, PostgreSQL, EF Core, Identity/JWT)
4. Create the initial data model (ApplicationUser, Document, DocumentPermission, AuditEvent)
5. Implement authentication and roles (Admin, Owner, Viewer; Admin must not automatically receive permission to read every document)
6. Establish the authorization foundation (Resource-based authorization, unit/integration tests)
7. Frontend foundation (React + TypeScript, login screen, navigation, API client)
8. Documentation (README.md, THREATS.md, PROMPTS.md)
9. Verify the implementation (Build backend, apply local migrations, check health, run automated tests, build frontend)
10. GitHub commit and push
11. End-of-day report
```

---

### Real Issues Encountered & Engineering Resolutions

1. **Missing .NET SDK and Git in Environment Path:**
   - *Issue:* Initial inspection showed `git` and `dotnet` were not found on the default system PATH. An initial `winget install Git.Git` failed because standard installers request UAC elevation (exit code 1), and the shell was running in non-elevated user mode.
   - *Resolution:* Deployed Microsoft's official user-level installer script (`dotnet-install.ps1`), which downloaded and configured .NET 8 SDK (v8.0.426) directly into `$env:LocalAppData\Microsoft\dotnet`. For Git, downloaded and extracted official portable MinGit (v2.44.0) into `$env:LocalAppData\Programs\Git`. Updated the user PATH environment variable so all subsequent operations could locate `dotnet` and `git` without requiring administrator access.

2. **PowerShell Quote & Statement Escaping in CLI Strings:**
   - *Issue:* Executing complex inline PowerShell commands via `powershell -Command "..."` caused semicolon and backslash syntax errors due to Windows quote-stripping.
   - *Resolution:* Transitioned multi-step automation tasks to standalone, well-structured `.ps1` script files (`scripts/init_projects.ps1`, `scripts/add_migration.ps1`) executed via `powershell -ExecutionPolicy Bypass -File <script>`.

3. **`dotnet-ef` Host Resolution:**
   - *Issue:* When running `dotnet-ef.exe migrations add InitialCreate`, the global tool executable initially looked for `.NET` in default program files instead of the user directory and failed with `Failed to resolve hostfxr.dll`.
   - *Resolution:* Set `$env:DOTNET_ROOT = "$env:LocalAppData\Microsoft\dotnet"` before executing `dotnet-ef`, allowing the migration tool to discover the runtime and generate the initial EF migration snapshot without issue.

4. **TypeScript `verbatimModuleSyntax` Compilation Errors:**
   - *Issue:* Frontend build (`npm run build`) initially threw TypeScript errors TS1484 (`error TS1484: 'User' is a type and must be imported using a type-only import`).
   - *Resolution:* Refactored imports in `src/App.tsx` and `src/api/client.ts` to use `import type { ... } from './types'`, satisfying strict TypeScript module conventions.

5. **Resource-Based Zero-Trust Authorization Requirement:**
   - *Issue:* Standard role-based access control (RBAC) often allows `Admin` to access everything. The prompt strictly mandated: *"An Admin must not automatically receive permission to read every document."*
   - *Resolution:* Designed and implemented `DocumentAuthorizationHandler` utilizing ASP.NET Core's resource-based authorization system (`IAuthorizationHandler<OperationAuthorizationRequirement, Document>`). The handler verifies explicit document ownership (`OwnerId == currentUserId`) or an active `DocumentPermission` record (`Read`/`Edit`). If an Admin attempts to read a document without ownership or permission, the check fails (emitting a 403 Forbidden and logging an audit warning). Built 19 automated tests (including test `Admin_Without_Explicit_Permission_Must_NOT_Automatically_Receive_Read_Access`) to mathematically prove this invariant.

6. **GitHub Remote Publishing:**
   - *Remote Configured:* `https://github.com/nagabhyruchiranjeevi/TrustVault.git`
   - *Action:* Remote added and `main` branch pushed successfully to GitHub with all Day 1 commits.

---

## Session 2: Day 2 Security Review & Feature Enhancements

**Date:** October 20, 2026
**Environment:** Windows 11 Enterprise (AMD64), PowerShell, .NET 8 SDK (v8.0.426), PostgreSQL 18 Local Service
**Model:** Gemini 3.8 Flash

### Real Issues Encountered & Engineering Resolutions

1. **Audit Scope & DTO Decoupling:**
   - Separated sensitive cryptographic hash properties from user self-audit endpoints (`/api/Audit/me`), restricting raw hash inspection to `/api/Audit` (Admin) and integrity checks to `/api/Audit/verify`.
2. **Revocation Immediate Denial Test:**
   - Added automated test proving that a revoked `DocumentPermission` immediately rejects the very next protected download request without relying on cached permissions or client tokens.
3. **Storage Cleanup on Delete:**
   - Implemented non-blocking background binary deletion upon document soft-deletion to ensure immediate API rejection while cleaning storage.
4. **Git Commit & Push:**
   - Commits `bd4f764` and `d9c551c` committed and pushed to `origin/main`.

---

## Session 3: Day 3 Finalization, Verification & Delivery

**Date:** October 21, 2026
**Environment:** Windows 11 Enterprise (AMD64), PowerShell, Node.js v24.11.1, .NET 8 SDK (v8.0.426), AWS CLI v2.36.10
**Model:** Gemini 3.8 Flash

### Prompt Received

```markdown
# TRUSTVAULT — DAY 3 FINALIZATION

Continue the existing TrustVault project. Do not create a new repository, replace working code, or rewrite Git history.
- Run complete test suite and verify current state.
- Final security verification (Authorization, Revocation, Audit Integrity, Storage/Deletion, Authentication, Upload validation).
- PostgreSQL readiness verification.
- Frontend and end-to-end workflow (Login Owner, Upload, Grant Read, Login Viewer, Download, Verify Denial, Revoke, Verify Denied 403, Audit Trail).
- Documentation updates (README.md, THREATS.md, PROMPTS.md).
- AWS deployment preparation (Explain architecture, costs, require explicit approval before provisioning).
- Final tests and GitHub review.
- Final report.
```

### Real Issues Encountered & Engineering Resolutions

1. **Multi-Instance Audit Write Ordering & Concurrency:**
   - *Issue:* In a multi-instance deployment (e.g., multiple ECS Fargate replicas or horizontal nodes), an in-process `SemaphoreSlim` cannot serialize global writes. Concurrent writes calculating the next sequence number simultaneously would violate the database unique constraint on `SequenceNumber`.
   - *Resolution:* Implemented an optimistic retry loop in `AuditService.LogEventAsync` catching `DbUpdateException` (up to 5 attempts with backoff), detaching the conflicted entity, re-querying the database chain head, and recalculating the deterministic SHA-256 hash. Added unit test `HashChain_Should_Handle_Concurrent_Writes_Safely_And_Preserve_Chain_Integrity` executing 10 parallel tasks and verifying that sequence numbers remain strictly contiguous (1 to 10) and the hash chain validates 100%.
2. **EF Core In-Memory Navigation Joins in Unit Testing:**
   - *Issue:* In `SecurityRequirementsTests.cs`, tests performing queries with `.Include(d => d.Owner)` and `.Include(p => p.User)` returned empty sets when the foreign key user IDs were not explicitly present in the in-memory `Users` DbSet.
   - *Resolution:* Seeded `ApplicationUser` principals directly into `_dbContext.Users` in test arrangements, allowing EF Core's navigation join tracking to resolve relations faithfully.
3. **Frontend Permission Management & Zero-Leak Workflow:**
   - *Issue:* The existing frontend had document upload and download, but lacked UI controls for document owners to grant permissions, view active permissions, or revoke them, which prevented completing the required 9-step demo in the browser.
   - *Resolution:* Enhanced `frontend/src/App.tsx` and `api/client.ts` with:
     - "Manage Access" modal allowing owners to enter a user email, select permission level (`Read`/`Edit`), grant access, and view/revoke existing permissions with real-time feedback.
     - Live search bar querying `/api/documents/search?query=...` demonstrating zero-leakage isolation.
     - Prominent "Verify Hash Chain Integrity" button calling `/api/Audit/verify` and displaying real-time cryptographic integrity status.
     - Full handling of `HTTP 403 Forbidden` responses when revoked users attempt unauthorized downloads.
4. **Test Suite Expansion:**
   - Total automated tests expanded from 23 to 31 tests across 6 test classes. All 31 tests pass cleanly in 844ms.
5. **PostgreSQL Service Analysis:**
   - Verified that PostgreSQL 18 (`postgresql-x64-18`) is running on port 5432, but candidate passwords did not connect. In compliance with strict guidelines, SQLite is not treated as equivalent; exact configuration steps are documented, and no billable cloud database was provisioned without authorization.
