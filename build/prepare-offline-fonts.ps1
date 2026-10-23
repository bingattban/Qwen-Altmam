# Build-time only. The application never downloads fonts at runtime.
# Fonts are fetched from a pinned public OFL-licensed Google Fonts commit.
# SHA-1 below is the verified Git blob object ID; refuse any unexpected bytes.
$ErrorActionPreference = 'Stop'
$commit = 'e7db6ca603fd4862010383ff17ffd6cc4ffac1c0'
$base = "https://raw.githubusercontent.com/google/fonts/$commit/ofl"
$destination = Join-Path $PSScriptRoot '..\src\AlTamam.WebUI\assets\fonts'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$fonts = @(
  @{ file='Cairo-Variable.ttf'; uri="$base/cairo/Cairo%5Bslnt%2Cwght%5D.ttf"; blob='25641b1c828857698457bcd9f5a102616b4e8033' },
  @{ file='Tajawal-Regular.ttf'; uri="$base/tajawal/Tajawal-Regular.ttf"; blob='d23d25b8b68db86d5b727d181c2008fd874216eb' },
  @{ file='Tajawal-Bold.ttf'; uri="$base/tajawal/Tajawal-Bold.ttf"; blob='71e1488e90215abcc8aedc541d94a2bcbec574f3' }
)
foreach ($font in $fonts) {
  $target = Join-Path $destination $font.file
  if (-not (Test-Path -LiteralPath $target)) {
    Invoke-WebRequest -Uri $font.uri -OutFile $target -ErrorAction Stop
  }
  $actual = (& git hash-object -- $target).Trim()
  if ($LASTEXITCODE -ne 0 -or $actual -ne $font.blob) {
    Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
    throw "Arabic font integrity verification failed: $($font.file)"
  }
  Write-Host "FONT VERIFIED: $($font.file)"
}
foreach ($family in @('cairo','tajawal')) {
  $target = Join-Path $destination ("OFL-" + $family + ".txt")
  if (-not (Test-Path -LiteralPath $target)) {
    Invoke-WebRequest -Uri "$base/$family/OFL.txt" -OutFile $target -ErrorAction Stop
  }
}
Write-Host 'OFFLINE ARABIC FONTS READY'
