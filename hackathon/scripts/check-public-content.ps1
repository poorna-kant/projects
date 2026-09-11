$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$violations = [Collections.Generic.List[string]]::new()
$allowedHosts = @(
    'localhost', '127.0.0.1', 'example.com', 'example.org',
    'github.com', 'dotnet.microsoft.com', 'docs.github.com',
    'learn.microsoft.com', 'pkgs.dev.azure.com', 'mcr.microsoft.com',
    'img.shields.io', 'www.nuget.org'
)
$extensions = @('.cs', '.csproj', '.html', '.json', '.yaml', '.yml', '.md', '.config', '.Config')
$files = Get-ChildItem $root -File -Recurse | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj|data|\.git)[\\/]' -and
    ($_.Extension -in $extensions -or $_.Name -eq 'Dockerfile')
}
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($root, $file.FullName)
    $content = Get-Content -LiteralPath $file.FullName -Raw
    if (!$content) { continue }
    foreach ($match in [regex]::Matches($content, 'https?://[A-Za-z0-9.-]+[^\s"''<>`)]*')) {
        $uri = $null
        if (![Uri]::TryCreate($match.Value, [UriKind]::Absolute, [ref]$uri)) { continue }
        if ($uri.Host -notin $allowedHosts) { $violations.Add("$relative contains an unapproved external host.") }
        if ($uri.Host -eq 'github.com' -and $uri.AbsolutePath -notmatch '^/(poorna-kant/projects(?:\.git)?|advisories)(/|$)') {
            $violations.Add("$relative links to a different repository or private source.")
        }
        if ($uri.Host -eq 'pkgs.dev.azure.com' -and !$uri.AbsolutePath.StartsWith('/dnceng/public/')) {
            $violations.Add("$relative links outside the public package feed.")
        }
    }
    if ($content -match '(?i)[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}') {
        $violations.Add("$relative contains an email address; use fictional role labels.")
    }
    if ($content -match '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{30,}|AccountKey=[A-Za-z0-9+/]{20,}') {
        $violations.Add("$relative contains a possible credential.")
    }
}
$forbiddenFiles = Get-ChildItem $root -File -Recurse | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj|data|\.git)[\\/]' -and
    ($_.Name -match '^\.env($|\.)' -or $_.Extension -in @('.db', '.parquet', '.mp4', '.pptx', '.docx', '.pfx', '.pem'))
}
foreach ($file in $forbiddenFiles) { $violations.Add("Unexpected data or credential artifact: $([IO.Path]::GetRelativePath($root, $file.FullName))") }
if ($violations.Count) { throw ($violations -join [Environment]::NewLine) }
Write-Output 'Public-content policy passed. This is a heuristic check, not a provenance, history, or legal-release certification.'
