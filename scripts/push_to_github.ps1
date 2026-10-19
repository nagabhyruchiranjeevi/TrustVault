$git = "C:\Users\HP\AppData\Local\Programs\Git\cmd\git.exe"

Set-Location "e:\TrustVault"

$existing = & $git remote
if ($existing -contains "origin") {
    & $git remote set-url origin https://github.com/nagabhyruchiranjeevi/TrustVault.git
} else {
    & $git remote add origin https://github.com/nagabhyruchiranjeevi/TrustVault.git
}

Write-Host "Configured remotes:"
& $git remote -v

Write-Host "`nAttempting git push to origin main..."
& $git push -u origin main
