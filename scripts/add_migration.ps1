$env:DOTNET_ROOT = "$env:LocalAppData\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$HOME\.dotnet\tools;$env:PATH"

Write-Host "DOTNET_ROOT set to $env:DOTNET_ROOT"
& "$HOME\.dotnet\tools\dotnet-ef.exe" migrations add InitialCreate --project backend/TrustVault.Api/TrustVault.Api.csproj
