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
