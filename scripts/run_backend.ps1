$env:DOTNET_ROOT = "$env:LocalAppData\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$HOME\.dotnet\tools;$env:PATH"

Write-Host "Starting Backend..."
& "$env:DOTNET_ROOT\dotnet.exe" run --project backend/TrustVault.Api/TrustVault.Api.csproj --urls "http://localhost:5115"
