# test_pg_pw.ps1 - Finds the local PostgreSQL password for development setup.
# SECURITY: Passwords are NEVER printed to stdout or logs.
#            Only a boolean FOUND/NOT_FOUND result is emitted.
$psql = "C:\Program Files\PostgreSQL\18\bin\psql.exe"
$passwords = @("postgres", "root", "admin", "password", "123456", "chiru", "chiru2005", "nagabhyru")

foreach ($p in $passwords) {
    $env:PGPASSWORD = $p
    $null = & $psql -U postgres -h localhost -p 5432 -c "SELECT 1;" 2>&1
    if ($LASTEXITCODE -eq 0) {
        # Store in a process-scoped env var so the caller can use it without it appearing in logs
        $env:PG_DEV_PASSWORD = $p
        Write-Host "FOUND: PostgreSQL password identified. Use `$env:PG_DEV_PASSWORD in this session."
        $env:PGPASSWORD = $null  # Clear the test variable immediately
        exit 0
    }
}

$env:PGPASSWORD = $null
Write-Host "PASSWORD_NOT_FOUND: None of the candidate passwords connected to PostgreSQL."
