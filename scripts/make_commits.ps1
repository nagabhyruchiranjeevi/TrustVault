$git = "C:\Users\HP\AppData\Local\Programs\Git\cmd\git.exe"

Set-Location "e:\TrustVault"

Write-Host "=== 1. Commit: Repository Foundation & Config ==="
& $git add .gitignore .env.example docker-compose.yml database/.gitkeep infrastructure/.gitkeep docs/.gitkeep scripts/ TrustVault.sln
& $git diff --cached --check
& $git commit -m "chore: initialize TrustVault repository structure and configuration"

Write-Host "`n=== 2. Commit: Backend API, Models & Data Layer ==="
& $git add backend/TrustVault.Api/Models/ backend/TrustVault.Api/Data/ backend/TrustVault.Api/Migrations/ backend/TrustVault.Api/TrustVault.Api.csproj backend/TrustVault.Api/Dockerfile
& $git diff --cached --check
& $git commit -m "feat: configure PostgreSQL and EF Core initial data model and migrations"

Write-Host "`n=== 3. Commit: Auth, Services, Controllers & Pipeline ==="
& $git add backend/TrustVault.Api/Auth/ backend/TrustVault.Api/DTOs/ backend/TrustVault.Api/Validators/ backend/TrustVault.Api/Services/ backend/TrustVault.Api/Middleware/ backend/TrustVault.Api/Controllers/ backend/TrustVault.Api/Program.cs backend/TrustVault.Api/appsettings.json backend/TrustVault.Api/appsettings.Development.json backend/TrustVault.Api/TrustVault.Api.http backend/TrustVault.Api/Properties/
& $git diff --cached --check
& $git commit -m "feat: implement authentication, resource authorization, and document controllers"

Write-Host "`n=== 4. Commit: Automated Test Suite ==="
& $git add tests/
& $git diff --cached --check
& $git commit -m "test: add automated test suite for authentication and resource authorization"

Write-Host "`n=== 5. Commit: Frontend React TypeScript Foundation ==="
& $git add frontend/
& $git diff --cached --check
& $git commit -m "feat: add React TypeScript frontend layout and secure api client"

Write-Host "`n=== 6. Commit: Project Documentation ==="
& $git add README.md THREATS.md PROMPTS.md
& $git diff --cached --check
& $git commit -m "docs: add project overview, threat model, and prompt records"

Write-Host "`n=== Git Log Summary ==="
& $git log --oneline -n 10
