# Builds Larva's installer for Windows into artifacts/installer:
#   LarvaSetup-<version>-win-x64.exe   the installer (larva, its language packs, the compiled System.Core and the project
#                                      templates inside; --help lists its options)
#   Larva-<version>-win-x64.zip        the same files to unpack anywhere (no PATH, no Settings > Apps entry)
#
#   tools/build-installer.ps1            the runtime packs from NuGet
#   tools/build-installer.ps1 -Offline   only what the local NuGet cache has
#   -Languages <folders>                 where the native language packs are (default ../Antril/artifacts/languages)
param([switch]$Offline, [string[]]$Languages)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content "$root/Larva.Cli/Larva.Cli.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = "$root/artifacts/installer"
$stage = "$out/stage"

# 1. larva, native, with its language packs (native and signed: Languages/<Name>/).
& "$PSScriptRoot/publish-larva.ps1" -Offline:$Offline -Languages $Languages
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 2. What gets installed: larva, its LLVM libraries, the language packs, System.Core and System.Bindings compiled into
#    libraries (System.Core.dll, System.Bindings.dll, by this same larva: no sources ship), the templates of `larva new` as Larva HIR (template.lva:
#    each project's language writes them as its source), a note.
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null
foreach ($file in @("larva.exe", "LLVM-C.dll", "libLLVMSharp.dll")) {
    Copy-Item "$root/artifacts/larva/$file" "$stage/$file"
}
Copy-Item -Recurse "$root/artifacts/larva/Languages" "$stage/Languages"
# The icons Explorer shows larva's files with (the setup registers them: --no-types skips it).
New-Item -ItemType Directory -Force "$stage/icons" | Out-Null
Copy-Item "$root/brand/file-*.ico" "$stage/icons/"
$sources = (Get-ChildItem "$root/Library/System.Core/*.ant").FullName
& "$stage/larva.exe" lib -o "$stage/System.Core.dll" --borrowcheck enforce @sources  # System.Core borrows (`&mut`), whatever this machine defaults to
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$sources = (Get-ChildItem "$root/Library/System.Bindings/*.ant").FullName
& "$stage/larva.exe" lib -o "$stage/System.Bindings.dll" @sources  # what binding profiles derive from (larva bind --profile)
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
foreach ($template in Get-ChildItem -Directory "$root/Templates") {
    & "$stage/larva.exe" new --pack $template.FullName -o "$stage/Templates"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
@"
Larva $version

larva.exe          the toolchain: larva new, larva build, larva lsp (the editor's language server), larva bind, larva hive
                   (packages), larva types
System.Core.dll    the standard library programs are built with (compiled)
System.Bindings.dll  what binding profiles are written against (larva bind --profile Profile.<ext> [library] [headers])
Languages\         the languages larva compiles: one folder each, with its frontend and its manifest
                   (language.json: author, version, the SHA-256 of its files, its signature); larva languages lists them
icons\             the icons of larva's files (.lva, .lvo, .lvm, .bind, .queen, .hive)
Templates\         the project templates of larva new (larva new lists them; larva new <template> <Name> --lang <language>)
LLVM-C.dll, libLLVMSharp.dll   the code generator larva uses

Native executables need the MSVC build tools (Visual Studio Build Tools, C++ workload).
larva bind preprocesses C headers with clang when LLVM is installed.
"@ | Set-Content -Encoding utf8 "$stage/README.txt"

$zip = "$out/Larva-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path "$stage/*" -DestinationPath $zip

# 3. The installer, with the zip inside.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;" + $env:PATH
$arguments = @("publish", "$root/Larva.Setup/Larva.Setup.csproj", "-c", "Release", "-r", "win-x64", "-p:PublishAot=true",
    "-p:Payload=$zip", "-p:Version=$version", "-o", "$out/setup")
if ($Offline) {
    $arguments += @("-p:RuntimeFrameworkVersion=10.0.12", "--source", "$env:USERPROFILE/.nuget/packages")
}
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item "$out/setup/LarvaSetup.exe" "$out/LarvaSetup-$version-win-x64.exe" -Force

# 4. The file types the setup registers, tried on a registry file of its own (--types-test: never the user's registry).
$types = & "$out/setup/LarvaSetup.exe" --types-test $stage | Out-String
Remove-Item "$stage/types.dat*" -Force -ErrorAction SilentlyContinue
$registered, $removed = $types -split "removed:"
foreach ($class in @("Larva.Library", "Larva.Object", "Larva.Project", "Larva.Package")) {
    if ($registered -notmatch [regex]::Escape($class)) { throw "the setup does not register $class`n$types" }
}
if ($removed -match "Larva\.") { throw "the uninstaller leaves file types behind`n$types" }
Write-Host "installer: $out/LarvaSetup-$version-win-x64.exe"
Write-Host "zip:       $zip"
