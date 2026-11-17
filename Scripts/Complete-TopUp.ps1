# Completes a fiat top-up: Super Admin signs the TopUp SignPayload; the API adds Central's signature,
# submits the network TopUp to blockers, then credits the wallet ledger.
#
# Run it on the Super Admin's own machine, never on the server. It needs the .NET 8 SDK (to sign).
#
#   ./Complete-TopUp.ps1                       # lists pending orders and asks which one
#   ./Complete-TopUp.ps1 -OrderId abc123
#   ./Complete-TopUp.ps1 -ApiBase https://testnet-api.deltanium.com   # testnet
#
# The network (mainnet / testnet) is read from $ApiBase/api/version and shown before you confirm; the
# signature is scoped to that network, so it is worthless on the other one.
#
# Admin calls are authorised by a Super Admin signature over each request (no shared token: Store and
# Blocker operators hold the node token, so it no longer opens admin endpoints).
# Secrets are asked for with hidden input when not already set:
#   Super Admin private key  from $env:SUPER_ADMIN_PRIVATE_KEY, else -SuperAdminKeysPath / ~/.deltanium/super_admin_keys.json
#                            (testnet: super_admin_keys.<network>.json) if that file exists, else prompt
# The private key is handed to the signer over stdin (never on a command line) and is never printed.

param(
  [string]$OrderId,
  [string]$ApiBase = "https://api.deltanium.com",
  [string]$SuperAdminKeysPath = "",
  [switch]$Yes  # skip the confirmation prompt
)

$ErrorActionPreference = "Stop"
$ApiBase = $ApiBase.TrimEnd("/")

function Read-Secret([string]$Prompt) {
  $secure = Read-Host -Prompt $Prompt -AsSecureString
  $plain = [System.Net.NetworkCredential]::new("", $secure).Password
  if ([string]::IsNullOrWhiteSpace($plain)) { throw "$Prompt is required" }
  return $plain.Trim()
}

function Get-SuperAdminPriv {
  if ($env:SUPER_ADMIN_PRIVATE_KEY) { return $env:SUPER_ADMIN_PRIVATE_KEY.Trim() }
  $file = if ($NetworkId) { "super_admin_keys.$NetworkId.json" } else { "super_admin_keys.json" }
  $path = if ($SuperAdminKeysPath) { $SuperAdminKeysPath } else { Join-Path (Join-Path $HOME ".deltanium") $file }
  if (Test-Path $path) {
    Write-Host "Using Super Admin key from $path"
    return ((Get-Content -Raw $path | ConvertFrom-Json).PrivateKey).Trim()
  }
  return Read-Secret "Super Admin private key for $NetworkName (hex)"
}

# Signer: reads the private key (hex) from stdin, message from args[0], network id from args[1] ("" = mainnet;
# same scoping as NetworkScope.Scope on the servers); prints "<pubkeyHex> <base64 DER sig>".
function New-Signer {
  $dir = Join-Path ([System.IO.Path]::GetTempPath()) "deltanium-sign-$([guid]::NewGuid().ToString('N'))"
  New-Item -ItemType Directory -Path $dir | Out-Null
  @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NBitcoin" Version="7.0.37" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $dir "sign.csproj")
  @"
using System.Security.Cryptography;
using System.Text;
using NBitcoin;
var priv = (Console.In.ReadLine() ?? "").Trim();
var network = args.Length > 1 ? args[1] : "";
var message = network.Length == 0 ? args[0] : network + "\n" + args[0];
var key = new Key(Convert.FromHexString(priv));
var hash = SHA256.HashData(Encoding.UTF8.GetBytes(message));
var sig = key.Sign(new uint256(hash));
Console.Write(key.PubKey.ToHex() + " " + Convert.ToBase64String(sig.ToDER()));
"@ | Set-Content (Join-Path $dir "Program.cs")
  Push-Location $dir
  try {
    & dotnet build -c Release -nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not build the signer (is the .NET 8 SDK installed?)" }
  } finally { Pop-Location }
  return $dir
}

function Invoke-Sign([string]$Dir, [string]$PrivateHex, [string]$Message) {
  Push-Location $Dir
  try { $out = ($PrivateHex | & dotnet run -c Release --no-build -- $Message $NetworkId) }
  finally { Pop-Location }
  if ($LASTEXITCODE -ne 0 -or -not $out) { throw "Signing failed (is the private key 64 hex characters?)" }
  $parts = ([string]$out).Trim().Split(" ")
  return @{ PublicKey = $parts[0]; Signature = $parts[1] }
}

$version = Invoke-RestMethod -Method Get -Uri "$ApiBase/api/version"
$NetworkName = if ($version.network) { [string]$version.network } else { "mainnet" }
$NetworkId = if ($NetworkName -eq "mainnet") { "" } else { $NetworkName }
Write-Host "Network    $NetworkName ($ApiBase, Central API $($version.version))"

$signer = New-Signer
$saPriv = Get-SuperAdminPriv
try {
$listPath = "/api/wallet/admin/topups?status=pending&limit=200"
$ts = [string][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
# Same message format as SignatureAuthHelper.BuildSignedMessage (GET: no body hash).
$listSig = Invoke-Sign $signer $saPriv ("GET" + $listPath + $ts)
$list = Invoke-RestMethod -Method Get -Uri "$ApiBase$listPath" -Headers @{
  "X-Timestamp" = $ts
  "X-SuperAdmin-Request-Signature" = $listSig.Signature
}
$pending = @($list.orders)
if ($pending.Count -eq 0) { Write-Host "No pending top-up orders."; return }

if (-not $OrderId) {
  $pending | Select-Object id, buyerPublicKey, fiatAmountVnd, dCredited, createdAt | Format-Table -AutoSize | Out-Host
  $OrderId = (Read-Host "OrderId to approve").Trim()
}
$order = $pending | Where-Object { $_.id -eq $OrderId } | Select-Object -First 1
if (-not $order) { throw "Pending order $OrderId not found" }
if (-not $order.signPayload) { throw "API did not return signPayload" }

Write-Host ""
Write-Host "Network    $NetworkName"
Write-Host "Order      $($order.id)"
Write-Host "Buyer      $($order.buyerPublicKey)"
Write-Host "Paid       $($order.fiatAmountVnd) VND (fee $($order.feePercent)%)"
Write-Host "Credit     $($order.dCredited) D"
Write-Host "SignPayload $($order.signPayload)"
Write-Host ""
if (-not $Yes) {
  $answer = Read-Host "Confirm you received the money and want to credit this order? (yes/no)"
  if ($answer -ne "yes") { Write-Host "Cancelled."; return }
}

$signed = Invoke-Sign $signer $saPriv $order.signPayload

if ($list.superAdminPublicKey -and ($signed.PublicKey -ne $list.superAdminPublicKey)) {
  throw "This private key belongs to $($signed.PublicKey), but the API expects Super Admin $($list.superAdminPublicKey). Nothing was sent."
}

$res = Invoke-RestMethod -Method Post -Uri "$ApiBase/api/wallet/admin/topup/approve" `
  -Headers @{
    "X-SuperAdmin-Signature" = $signed.Signature
  } `
  -ContentType "application/json" -Body (@{ orderId = $OrderId } | ConvertTo-Json -Compress)

$res | ConvertTo-Json -Depth 8
} finally {
  $saPriv = $null
  Remove-Item -Recurse -Force $signer -ErrorAction SilentlyContinue
}
