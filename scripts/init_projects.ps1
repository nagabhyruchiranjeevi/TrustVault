$dotnet = "C:\Users\HP\AppData\Local\Microsoft\dotnet\dotnet.exe"
$git = "C:\Users\HP\AppData\Local\Programs\Git\cmd\git.exe"

Write-Host "Creating Solution and .NET Projects..."
Set-Location "e:\TrustVault"

& $dotnet new sln -n TrustVault
& $dotnet new webapi -n TrustVault.Api -o backend/TrustVault.Api --framework net8.0
& $dotnet new xunit -n TrustVault.Tests -o tests/TrustVault.Tests --framework net8.0

& $dotnet sln add backend/TrustVault.Api/TrustVault.Api.csproj
& $dotnet sln add tests/TrustVault.Tests/TrustVault.Tests.csproj

& $dotnet add tests/TrustVault.Tests/TrustVault.Tests.csproj reference backend/TrustVault.Api/TrustVault.Api.csproj

Write-Host "Adding NuGet packages to TrustVault.Api..."
& $dotnet add backend/TrustVault.Api/TrustVault.Api.csproj package Npgsql.EntityFrameworkCore.PostgreSQL --version 8.0.4
& $dotnet add backend/TrustVault.Api/TrustVault.Api.csproj package Microsoft.EntityFrameworkCore.Design --version 8.0.8
& $dotnet add backend/TrustVault.Api/TrustVault.Api.csproj package Microsoft.AspNetCore.Identity.EntityFrameworkCore --version 8.0.8
& $dotnet add backend/TrustVault.Api/TrustVault.Api.csproj package Microsoft.AspNetCore.Authentication.JwtBearer --version 8.0.8
& $dotnet add backend/TrustVault.Api/TrustVault.Api.csproj package FluentValidation.AspNetCore --version 11.3.0
& $dotnet add backend/TrustVault.Api/TrustVault.Api.csproj package Swashbuckle.AspNetCore --version 6.5.0
& $dotnet add backend/TrustVault.Api/TrustVault.Api.csproj package Microsoft.AspNetCore.Diagnostics.EntityFrameworkCore --version 8.0.8

Write-Host "Adding NuGet packages to TrustVault.Tests..."
& $dotnet add tests/TrustVault.Tests/TrustVault.Tests.csproj package Microsoft.AspNetCore.Mvc.Testing --version 8.0.8
& $dotnet add tests/TrustVault.Tests/TrustVault.Tests.csproj package Microsoft.EntityFrameworkCore.InMemory --version 8.0.8
& $dotnet add tests/TrustVault.Tests/TrustVault.Tests.csproj package FluentAssertions --version 6.12.0
& $dotnet add tests/TrustVault.Tests/TrustVault.Tests.csproj package Moq --version 4.20.70

Write-Host "Project initialization complete."
