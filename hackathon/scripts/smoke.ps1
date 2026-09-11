param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root 'kb-app'
$assembly = Join-Path $app "bin/$Configuration/net10.0/KbApp.dll"
if (!(Test-Path -LiteralPath $assembly)) { throw 'Build the application before running smoke.ps1.' }
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('scope-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$state = Join-Path $temporary 'state'
$seed = Join-Path $root 'knowledge-base'
$beforeSeed = (Get-FileHash (Join-Path $seed 'plan/terms/index.yaml')).Hash
$process = $null
$base = $null
function Assert($condition, [string]$message) {
    if (!$condition) { throw $message }
}
function Stop-Demo {
    if ($script:process -and !$script:process.HasExited) {
        $script:process.Kill($true)
        $script:process.WaitForExit()
    }
}
function Start-Demo([bool]$readOnly) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.WorkingDirectory = $app
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add($assembly)
    $start.ArgumentList.Add('--urls')
    $start.ArgumentList.Add('http://127.0.0.1:0')
    $start.Environment['SCOPE_READ_ONLY'] = $readOnly.ToString().ToLowerInvariant()
    $start.Environment['SCOPE_STATE_PATH'] = $state
    $start.Environment['SCOPE_SEED_PATH'] = $seed
    $script:process = [Diagnostics.Process]::Start($start)
    $script:stderrTask = $script:process.StandardError.ReadToEndAsync()
    $limit = [DateTime]::UtcNow.AddSeconds(35)
    while ([DateTime]::UtcNow -lt $limit) {
        $lineTask = $script:process.StandardOutput.ReadLineAsync()
        if (!$lineTask.Wait([TimeSpan]::FromSeconds(10))) { throw 'Application startup timed out.' }
        $line = $lineTask.Result
        if ($null -eq $line) { throw "Application exited: $($script:stderrTask.Result)" }
        if ($line -match 'Now listening on: (http://127\.0\.0\.1:\d+)') {
            $script:base = $Matches[1]
            $script:stdoutTask = $script:process.StandardOutput.ReadToEndAsync()
            return
        }
    }
    throw 'No application listening address was reported.'
}
function Read-Api([string]$path) {
    Invoke-RestMethod -Uri "$script:base$path"
}
function Write-Api([string]$path, $body, [int]$expected = 200, [bool]$header = $true) {
    $headers = @{}
    if ($header) { $headers['X-SCOPE-Local'] = '1' }
    $result = Invoke-WebRequest -Uri "$script:base$path" -Method Post -Headers $headers `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 12) -SkipHttpErrorCheck
    Assert ($result.StatusCode -eq $expected) "$path returned $($result.StatusCode), expected $expected."
    if ($result.Content) { return $result.Content | ConvertFrom-Json }
}
try {
    Start-Demo $true
    Assert ((Read-Api '/healthz').status -eq 'ok') 'Health check failed.'
    Assert ((Read-Api '/api/demo/config').readOnly -eq $true) 'Hosted demo must default to read-only.'
    Write-Api '/api/publish' @{} 403 | Out-Null
    $initial = Read-Api '/api/agent/answer?q=Demand%20Plan'
    Assert $initial.answered 'Published demo term not found.'
    Assert ((Read-Api '/api/measures').Count -eq 2) 'Expected two synthetic measures.'
    Assert ((Read-Api '/api/catalog/plan/metrics').count -eq 2) 'Expected two synthetic metrics.'
    Stop-Demo

    Start-Demo $false
    Write-Api '/api/publish' @{} 403 $false | Out-Null
    $badOrigin = Invoke-WebRequest "$base/api/publish" -Method Post -Headers @{
        'X-SCOPE-Local' = '1'; Origin = 'https://example.com'
    } -ContentType 'application/json' -Body '{}' -SkipHttpErrorCheck
    Assert ($badOrigin.StatusCode -eq 403) 'Cross-origin write must be denied.'
    $updated = 'Synthetic revised demand definition for the smoke demonstration.'
    $proposal = Write-Api '/api/reviews' @{
        Domain = 'plan'; Type = 'terms'; ItemId = 'plan/terms/demand-plan'; Title = 'Demand Plan'
        Fields = @{ Id = 'plan/terms/demand-plan'; Term = 'Demand Plan'; Category = 'Planning'
            Definition = $updated; Aliases = 'demand forecast, plan'; SeeAlso = 'Supply Plan, Plan Version' }
        By = 'Demo reviewer'
    }
    Assert ((Read-Api '/api/agent/answer?q=Demand%20Plan').definition -eq $initial.definition) 'Proposal changed published definition.'
    Write-Api ("/api/reviews/" + $proposal.id + '/approve') @{} | Out-Null
    Assert ((Read-Api '/api/agent/answer?q=Demand%20Plan').definition -eq $initial.definition) 'Approval must not implicitly publish.'
    $published = Write-Api '/api/publish' @{}
    Assert ($published.version -gt 1) 'Publication did not advance version.'
    Assert ((Read-Api '/api/agent/answer?q=Demand%20Plan').definition -eq $updated) 'Publication missed approved YAML change.'
    Assert ((Read-Api '/api/published/tree').records.Count -gt 0) 'Typed knowledge snapshot missing.'
    $contextFile = Join-Path $state 'knowledge-base/plan/metrics/planning-demand.yaml'
    $contextHash = (Get-FileHash $contextFile).Hash
    $generated = Write-Api '/api/domains/plan/metrics/generate' @{}
    Assert ($generated.count -eq 2) 'Synthetic registry generation failed.'
    Assert ((Get-FileHash $contextFile).Hash -eq $contextHash) 'Generation overwrote authored context.'
    $metrics = (Read-Api '/api/catalog/plan/metrics').metrics
    Assert (@($metrics | Where-Object { $_.code -eq 'DEMO-001' -and $_.unit -eq 'units' }).Count -eq 1) 'Demand identity/unit changed.'
    Assert (@($metrics | Where-Object { $_.code -eq 'DEMO-002' -and $_.unit -eq 'percent' }).Count -eq 1) 'Coverage identity/unit changed.'
    Assert ((Read-Api '/api/catalog/plan/validation').status -eq 'pass') 'Synthetic structural validation failed.'
    $refresh = Write-Api '/api/refresh/demo_demand/verify' @{}
    Assert ($refresh.outcome -eq 'reference' -and !$refresh.synced) 'Demo freshness must not claim a live synchronization.'
    Stop-Demo

    Start-Demo $true
    Assert ((Read-Api '/api/agent/answer?q=Demand%20Plan').definition -eq $updated) 'Published data was not preserved after restart.'
    Assert ((Get-FileHash (Join-Path $seed 'plan/terms/index.yaml')).Hash -eq $beforeSeed) 'Smoke modified the source seed.'
    Write-Output 'SCOPE smoke scenarios passed: hosted read-only, local review/publication, generation, and persistence.'
}
finally {
    Stop-Demo
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
