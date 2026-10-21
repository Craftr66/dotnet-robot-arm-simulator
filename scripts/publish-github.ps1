param([string]$Owner = 'Craftr66', [string]$Repository = 'dotnet-robot-arm-simulator')
$ErrorActionPreference = 'Stop'
if ($Owner -notmatch '^[A-Za-z0-9-]+$' -or $Repository -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Invalid GitHub repository identity.' }
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $gitOptions = @('-c', ('safe.directory=' + $PWD.Path.Replace('\','/')))
    # Reuse Git Credential Manager in memory; never print or persist the credential.
    $credentialInput = "protocol=https`nhost=github.com`nusername=$Owner`n`n"
    $credentialLines = $credentialInput | & git @gitOptions credential fill
    if ($LASTEXITCODE -ne 0) { throw 'GitHub credential lookup failed. Sign in using Git Credential Manager first.' }
    $credentialFields = @{}
    foreach ($line in $credentialLines) { $pair = $line -split '=', 2; if ($pair.Length -eq 2) { $credentialFields[$pair[0]] = $pair[1] } }
    if (!$credentialFields['password']) { throw 'No GitHub credential is available.' }
    $headers = @{ Authorization = 'Bearer ' + $credentialFields['password']; Accept = 'application/vnd.github+json'; 'User-Agent' = 'AxisLab-source-publish'; 'X-GitHub-Api-Version' = '2022-11-28' }
    $account = Invoke-RestMethod -Uri 'https://api.github.com/user' -Headers $headers
    if ($account.login -ne $Owner) { throw 'Authenticated GitHub account differs from the requested repository owner.' }
    $repositoryUri = "https://api.github.com/repos/$Owner/$Repository"
    $existing = $null
    try { $existing = Invoke-RestMethod -Uri $repositoryUri -Headers $headers }
    catch { if (!$_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw 'Could not inspect the target GitHub repository.' } }
    if (!$existing) {
        $body = @{ name = $Repository; description = '基于.net开发的仿真机械臂平台：AxisLab、VisionMaster 与 WPF 标定实训'; private = $false; auto_init = $false } | ConvertTo-Json
        $existing = Invoke-RestMethod -Uri 'https://api.github.com/user/repos' -Headers $headers -Method Post -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json; charset=utf-8'
    }
    if ($existing.private) { throw 'The target repository is private; this script will not change existing repository visibility.' }
    $remote = "https://github.com/$Owner/$Repository.git"
    $remoteNames = @(& git @gitOptions remote)
    if ($remoteNames -notcontains 'origin') { & git @gitOptions remote add origin $remote }
    elseif ((& git @gitOptions remote get-url origin) -ne $remote) { throw 'Existing origin differs from the requested target.' }
    & git @gitOptions push -u origin main
    if ($LASTEXITCODE -ne 0) { throw 'Git push failed; no force push was attempted.' }
    $verified = Invoke-RestMethod -Uri ($repositoryUri + '/commits/main') -Headers $headers
    $localCommit = & git @gitOptions rev-parse HEAD
    if ($verified.sha -ne $localCommit) { throw 'Remote main does not match the local commit.' }
    Write-Host "Published and verified: https://github.com/$Owner/$Repository"
} finally {
    $credentialLines = $null; $credentialFields = $null; $headers = $null
    Pop-Location
}

