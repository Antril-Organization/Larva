# Publishes the `larva` command line as a native (ahead-of-time compiled) program into artifacts/larva, with the language
# packs next to it (Languages/<Name>/), their manifests completed and signed.
# A native larva starts compiling at once: no JIT, so a build of a small program spends its time in LLVM and the linker
# (the frontend, HIR linking, lowering and IR checks take tens of milliseconds instead of a second).
#
#   tools/publish-larva.ps1 [-Offline] [-Languages <folders>]
#
# -Offline: only what the local NuGet cache has (pins the 10.0.12 runtime pack found there).
# -Languages: folders of native language packs, each pack a folder with its library and language.json (by default
# ../Antril/artifacts/languages, what the Antril repository's tools/publish-pack.ps1 writes). Each language is built in
# its own repository; larva only signs and ships its pack.
#
# The packs are signed with LARVA_SIGNING_KEY (a PEM file), or with artifacts/keys/languages.pem (made the first time).
param([switch]$Offline, [string[]]$Languages)

$root = Split-Path -Parent $PSScriptRoot
$out = "$root/artifacts/larva"
# The native linker is found through vswhere, which Visual Studio installs outside PATH.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;" + $env:PATH
$common = @("-c", "Release", "-r", "win-x64", "-p:PublishAot=true")
if ($Offline) {
    $common += @("-p:RuntimeFrameworkVersion=10.0.12", "--source", "$env:USERPROFILE/.nuget/packages")
}
& dotnet publish "$root/Larva.Cli/Larva.Cli.csproj" @common -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# The key the language packs are signed with.
$key = $env:LARVA_SIGNING_KEY
if (-not $key) {
    $key = "$root/artifacts/keys/languages.pem"
    if (-not (Test-Path $key)) {
        New-Item -ItemType Directory -Force (Split-Path $key) | Out-Null
        & "$out/larva.exe" languages key $key
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
if (-not $Languages) { $Languages = @("$root/../Antril/artifacts/languages") }
if (Test-Path "$out/Languages") { Remove-Item -Recurse -Force "$out/Languages" }
$found = 0
foreach ($folder in $Languages) {
    if (-not (Test-Path $folder)) { Write-Warning "no language packs in $folder"; continue }
    foreach ($language in Get-ChildItem -Directory $folder) {
        if (-not (Test-Path "$($language.FullName)/language.json")) { continue }
        $pack = "$out/Languages/$($language.Name)"
        Copy-Item -Recurse $language.FullName $pack
        & "$out/larva.exe" languages pack $pack --key $key
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $found++
    }
}
if ($found -eq 0) { Write-Error "no language packs: publish one first (in the Antril repository: tools/publish-pack.ps1)"; exit 1 }
Write-Host "larva: $out/larva.exe"
exit 0
