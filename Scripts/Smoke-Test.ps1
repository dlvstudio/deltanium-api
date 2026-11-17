<#
.SYNOPSIS
  Post-deploy smoke tests for Central API. Read-only: never creates data, never uses a key or token.
  Runs after every deploy (testnet, then mainnet); any failure fails the pipeline stage.

.EXAMPLE
  ./Scripts/Smoke-Test.ps1 -Site testnet-api.deltanium.com
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Site,
    [string]$ExpectCommit = ''
)
$ErrorActionPreference = 'Stop'
$Site = @($Site | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
Add-Type -AssemblyName System.Net.Http
$script:http = [System.Net.Http.HttpClient]::new()
$script:http.Timeout = [TimeSpan]::FromSeconds(30)
$script:failures = 0

function Invoke-Http([string]$Method, [string]$Url, [hashtable]$Headers = @{}, [string]$Body = $null) {
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Url)
    foreach ($k in $Headers.Keys) { [void]$req.Headers.TryAddWithoutValidation($k, [string]$Headers[$k]) }
    if ($Method -ne 'GET') {
        $req.Content = [System.Net.Http.StringContent]::new($(if ($Body) { $Body } else { '' }), [Text.Encoding]::UTF8, 'application/json')
    }
    $resp = $script:http.SendAsync($req).GetAwaiter().GetResult()
    [pscustomobject]@{ Status = [int]$resp.StatusCode; Body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() }
}

function Check([string]$Name, [scriptblock]$Test) {
    try {
        $detail = & $Test
        Write-Host "PASS $Name $detail"
    } catch {
        $script:failures++
        Write-Host "FAIL $Name : $($_.Exception.Message)"
        Write-Host "##vso[task.logissue type=error]Smoke FAIL $Name : $($_.Exception.Message)"
    }
}

function Expect-Status($r, [int[]]$Want) {
    if ($Want -notcontains $r.Status) { throw "HTTP $($r.Status), want $($Want -join '/'): $($r.Body.Substring(0, [Math]::Min(160, $r.Body.Length)))" }
    "($($r.Status))"
}

function Expect-NoPrivateFields([string]$Json, [string[]]$Fields) {
    foreach ($f in $Fields) {
        if ($Json -match ('"' + $f + '"\s*:')) { throw "response exposes '$f'" }
    }
}

foreach ($s in $Site) {
    $b = "https://$s"
    Write-Host "=== Central API smoke: $b"

    Check 'version' {
        $v = (Invoke-Http GET "$b/api/version").Body | ConvertFrom-Json
        if (-not $v.commit) { throw 'no commit' }
        if ($ExpectCommit -and $v.commit -ne $ExpectCommit) { throw "serves $($v.commit), want $ExpectCommit" }
        "$($v.commitShort) network=$($v.network)"
    }
    Check 'fees are enforced' {
        $e = (Invoke-Http GET "$b/api/wallet/economics").Body | ConvertFrom-Json
        if ($e.enforceFees -ne $true) { throw "enforceFees=$($e.enforceFees)" }
    }
    Check 'store directory lists nodes' {
        $n = @((Invoke-Http GET "$b/api/storenode/list").Body | ConvertFrom-Json)
        if ($n.Count -lt 1) { throw 'no store nodes' }
        "($($n.Count) nodes)"
    }
    Check 'decision key published' {
        $k = (Invoke-Http GET "$b/api/blockcreatornode/decision-key").Body | ConvertFrom-Json
        if ($k.publicKey -notmatch '^0[23][0-9a-f]{64}$') { throw "bad key '$($k.publicKey)'" }
    }

    # --- auth: every state-changing endpoint refuses unsigned calls and the old shared token ---
    Check 'tx pull refuses unsigned + node token' {
        Expect-Status (Invoke-Http POST "$b/api/tx/pull?blockerPubKey=x" @{ 'X-Internal-Token' = 'smoke-test' }) 401,403
    }
    Check 'admin endpoints refuse node token' {
        foreach ($p in 'admin/payee', 'admin/slash', 'admin/run/settle') {
            Expect-Status (Invoke-Http POST "$b/api/wallet/$p" @{ 'X-Internal-Token' = 'smoke-test' } '{}') 401 | Out-Null
        }
        Expect-Status (Invoke-Http GET "$b/api/wallet/admin/topups" @{ 'X-Internal-Token' = 'smoke-test' }) 401
    }
    Check 'store rekey needs a Super Admin signature' { Expect-Status (Invoke-Http POST "$b/api/storenode/admin/rekey" @{ 'X-Internal-Token' = 'smoke-test' } '{"nodeId":"x","publicKey":"x"}') 401 }
    Check 'PRE validate-token refuses unsigned + node token' { Expect-Status (Invoke-Http POST "$b/api/policy/validate-token" @{ 'X-Internal-Token' = 'smoke-test' } '{"token":"x"}') 401 }
    Check 'cancel-escrow refuses unsigned' { Expect-Status (Invoke-Http POST "$b/api/storedfile/cancel-escrow" @{} '{"fileId":"x"}') 401 }
    Check 'email send-code refuses unsigned' { Expect-Status (Invoke-Http POST "$b/api/email/send-code" @{} '{"email":"a@example.com"}') 401 }
    Check 'wallet balance refuses unsigned' { Expect-Status (Invoke-Http GET "$b/api/wallet/balance") 401 }

    # --- privacy: public endpoints never return email, date of birth or registration signature ---
    $sigs = Invoke-Http GET "$b/api/user/signatures"
    Check 'user/signatures hides email and DOB' {
        Expect-Status $sigs 200 | Out-Null
        Expect-NoPrivateFields $sigs.Body @('email', 'dateOfBirth')
    }
    Check 'public profile hides private fields' {
        $first = @($sigs.Body | ConvertFrom-Json) | Select-Object -First 1
        if (-not $first) { return 'skipped (no users)' }
        $r = Invoke-Http GET "$b/api/user/profile/$($first.publicKey)"
        Expect-Status $r 200 | Out-Null
        Expect-NoPrivateFields $r.Body @('email', 'dateOfBirth', 'signature')
    }
}

$script:http.Dispose()
if ($script:failures -gt 0) { throw "$($script:failures) smoke check(s) failed" }
Write-Host 'All Central API smoke checks passed'
