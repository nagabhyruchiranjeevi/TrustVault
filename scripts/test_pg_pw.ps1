$psql = "C:\Program Files\PostgreSQL\18\bin\psql.exe"
$passwords = @("postgres", "root", "admin", "password", "123456", "chiru", "chiru2005", "nagabhyru")

foreach ($p in $passwords) {
    $env:PGPASSWORD = $p
    $null = & $psql -U postgres -h localhost -p 5432 -c "SELECT 1;" 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "FOUND_PASSWORD:$p"
        exit 0
    }
}
Write-Host "PASSWORD_NOT_FOUND"
