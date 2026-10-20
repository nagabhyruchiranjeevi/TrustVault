$git = "C:\Users\HP\AppData\Local\Programs\Git\cmd\git.exe"
$dotnet = "C:\Users\HP\AppData\Local\Microsoft\dotnet\dotnet.exe"

Set-Location "e:\TrustVault"

Write-Host "=== Current Branch & Status ==="
& $git branch --show-current
& $git status -s

Write-Host "`n=== Latest 5 Commits ==="
& $git log --oneline -n 5

Write-Host "`n=== Backend Build Check ==="
& $dotnet build backend/TrustVault.Api/TrustVault.Api.csproj

Write-Host "`n=== Existing Test Suite Run ==="
& $dotnet test tests/TrustVault.Tests/TrustVault.Tests.csproj
