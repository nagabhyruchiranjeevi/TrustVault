$env:DOTNET_ROOT = "$env:LocalAppData\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$HOME\.dotnet\tools;$env:PATH"

Write-Host "=== Starting TrustVault.Api on http://localhost:5115 ==="
$process = Start-Process -FilePath "$env:DOTNET_ROOT\dotnet.exe" -ArgumentList "run --project backend/TrustVault.Api/TrustVault.Api.csproj --urls http://localhost:5115" -PassThru

try {
    Write-Host "Waiting 6 seconds for API startup..."
    Start-Sleep -Seconds 6

    Write-Host "Calling Health Check: http://localhost:5115/api/health"
    try {
        $healthRes = Invoke-RestMethod -Uri "http://localhost:5115/api/health" -Method Get -TimeoutSec 5
        Write-Host "Health Check (HTTP 200):" ($healthRes | ConvertTo-Json -Depth 4)
    }
    catch {
        $stream = $_.Exception.Response.GetResponseStream()
        $reader = New-Object System.IO.StreamReader($stream)
        $body = $reader.ReadToEnd()
        Write-Host "Health Check Response (HTTP $($_.Exception.Response.StatusCode.value__)):`n$body"
    }

    Write-Host "`nCalling Swagger JSON: http://localhost:5115/swagger/v1/swagger.json"
    $swaggerRes = Invoke-RestMethod -Uri "http://localhost:5115/swagger/v1/swagger.json" -Method Get -TimeoutSec 5
    Write-Host "Swagger Doc Title:" $swaggerRes.info.title "Version:" $swaggerRes.info.version
    Write-Host "Total Endpoints Documented:" $swaggerRes.paths.psobject.properties.Count
    foreach ($path in $swaggerRes.paths.psobject.properties.Name) {
        Write-Host "  -> $path"
    }
}
finally {
    Write-Host "`nStopping backend test process (PID: $($process.Id))..."
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
}
