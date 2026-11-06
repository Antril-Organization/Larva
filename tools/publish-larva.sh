#!/bin/sh
# Publishes the `larva` command line as a native (ahead-of-time compiled) program into artifacts/larva, on Linux or
# macOS, with the language packs next to it (Languages/<Name>/), their manifests completed and signed: what
# tools/publish-larva.ps1 does on Windows. The packs come from LARVA_PACKS (folders separated by `:`), by default
# ../Antril/artifacts/languages (what the Antril repository's tools/publish-pack.sh writes). NativeAOT compiles for the system it runs on only, so each
# platform's larva is published on that platform (.github/workflows/installers.yml does all three).
#
#   tools/publish-larva.sh
#
# The packs are signed with LARVA_SIGNING_KEY (a PEM file), or with artifacts/keys/languages.pem (made the first time).
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
out="$root/artifacts/larva"

case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) rid=linux-x64 ;;
    Darwin-arm64) rid=osx-arm64 ;;
    *) echo "publish-larva.sh: larva is published for Linux x64 and macOS arm64, not $(uname -s) $(uname -m)" >&2; exit 2 ;;
esac

dotnet publish "$root/Larva.Cli/Larva.Cli.csproj" -c Release -r "$rid" -p:PublishAot=true -o "$out"

key=${LARVA_SIGNING_KEY:-}
if [ -z "$key" ]; then
    key="$root/artifacts/keys/languages.pem"
    if [ ! -f "$key" ]; then
        mkdir -p "$(dirname "$key")"
        "$out/larva" languages key "$key"
    fi
fi

# The language packs, published natively by their own repositories: copied and signed.
rm -rf "$out/Languages"
found=0
old_ifs=$IFS
IFS=:
for folder in ${LARVA_PACKS:-$root/../Antril/artifacts/languages}; do
    IFS=$old_ifs
    [ -d "$folder" ] || { echo "publish-larva.sh: no language packs in $folder" >&2; continue; }
    for language in "$folder"/*/; do
        [ -f "$language/language.json" ] || continue
        pack="$out/Languages/$(basename "$language")"
        mkdir -p "$out/Languages"
        cp -R "${language%/}" "$pack"
        "$out/larva" languages pack "$pack" --key "$key"
        found=$((found + 1))
    done
done
IFS=$old_ifs
if [ "$found" -eq 0 ]; then
    echo "publish-larva.sh: no language packs: publish one first (in the Antril repository: tools/publish-pack.sh)" >&2
    exit 1
fi
echo "larva: $out/larva"
