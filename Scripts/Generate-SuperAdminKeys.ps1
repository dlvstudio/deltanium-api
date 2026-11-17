# Generate Super Admin keypair on your laptop (not on the API host).
# Writes JSON + PDF (PDF contains PublicKey and PrivateKey for offline backup).
#
#   ./Generate-SuperAdminKeys.ps1
#   ./Generate-SuperAdminKeys.ps1 -Out "$HOME/.deltanium/super_admin_keys.json"
#   ./Generate-SuperAdminKeys.ps1 -Overwrite
#
# Then set SUPER_ADMIN_PUBLIC_KEY on API + blockers; keep PrivateKey / PDF offline.

param(
  [string]$Out = "",
  [switch]$Overwrite
)

$ErrorActionPreference = "Stop"
$apiDir = Split-Path $PSScriptRoot -Parent
$apiProject = Join-Path $apiDir "deltanium-api.csproj"
if (-not (Test-Path $apiProject)) {
  throw "Cannot find deltanium-api.csproj at $apiProject"
}

$dotnetArgs = @("--project", $apiProject, "--no-launch-profile", "--", "--generate-super-admin-keys")
if ($Out) { $dotnetArgs += @("--out", $Out) }
if ($Overwrite) { $dotnetArgs += "--overwrite" }

dotnet run @dotnetArgs
