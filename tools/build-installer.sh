#!/bin/sh
# Builds Larva's installer for Linux or macOS into artifacts/installer (what tools/build-installer.ps1 does on Windows):
#   Larva-<version>-linux-x64.tar.gz    larva, its language packs, the compiled System.Core and System.Bindings, the
#   Larva-<version>-osx-arm64.tar.gz    project templates, the icons of larva's files, and install.sh, which installs it
#                                       all for the current user (./install.sh --help lists its options)
#
#   tools/build-installer.sh
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
version=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$root/Larva.Cli/Larva.Cli.csproj" | head -n 1)
out="$root/artifacts/installer"

# 1. larva, native, with its language packs.
sh "$root/tools/publish-larva.sh"
case "$(uname -s)" in
    Linux) rid=linux-x64 ;;
    Darwin) rid=osx-arm64 ;;
esac

# 2. What gets installed: larva and LLVM's libraries, the language packs, System.Core and System.Bindings compiled into
#    libraries (no sources ship), the templates of `larva new` as Larva HIR, the icons, a note and the installer.
stage="$out/Larva-$version-$rid"
rm -rf "$stage"
mkdir -p "$stage/icons"
cp "$root/artifacts/larva/larva" "$stage/"
for file in "$root"/artifacts/larva/*.so* "$root"/artifacts/larva/*.dylib; do
    if [ -f "$file" ]; then cp "$file" "$stage/"; fi
done
cp -R "$root/artifacts/larva/Languages" "$stage/Languages"
"$stage/larva" lib -o "$stage/System.Core.dll" --borrowcheck enforce "$root"/Library/System.Core/*.ant
"$stage/larva" lib -o "$stage/System.Bindings.dll" "$root"/Library/System.Bindings/*.ant
for template in "$root"/Templates/*/; do
    "$stage/larva" new --pack "${template%/}" -o "$stage/Templates"
done
for name in lva lvo lvm bind queen hive; do
    cp "$root/brand/file-$name.svg" "$root/brand/file-$name.icns" "$stage/icons/"
done
cp "$root/brand/larva-app.svg" "$root/brand/larva-app.icns" "$stage/icons/"
cp "$root/tools/installer/install.sh" "$stage/install.sh"
chmod +x "$stage/install.sh" "$stage/larva"
cat > "$stage/README.txt" <<EOF
Larva $version

./install.sh       installs larva for the current user (./install.sh --help lists its options; --uninstall removes it)
larva              the toolchain: larva new, larva build, larva lsp (the editor's language server), larva bind, larva hive
                   (packages), larva types
System.Core.dll    the standard library programs are built with (compiled)
System.Bindings.dll  what binding profiles are written against (larva bind --profile Profile.<ext> [library] [headers])
Languages/         the languages larva compiles: one folder each, with its frontend and its manifest
                   (language.json: author, version, the SHA-256 of its files, its signature); larva languages lists them
Templates/         the project templates of larva new (larva new lists them; larva new <template> <Name> --lang <language>)
icons/             the icons of larva's files (.lva, .lvo, .lvm, .bind, .queen, .hive)

Native executables are linked by the system's C compiler (cc: clang or gcc; on macOS, the Xcode command line tools).
larva bind preprocesses C headers with clang when it is installed.
EOF

archive="$out/Larva-$version-$rid.tar.gz"
rm -f "$archive"
tar -czf "$archive" -C "$out" "Larva-$version-$rid"

# 3. The installer, tried in a home of its own (never the user's): installed, larva answering, file types registered,
#    and all of it removed again.
check=$(mktemp -d)
HOME="$check/home" XDG_DATA_HOME="$check/home/.local/share" LARVA_CONFIG="$check/defaults.json" \
    sh "$stage/install.sh" --prefix "$check/larva" --no-path
"$check/home/.local/bin/larva" --version
HOME="$check/home" XDG_DATA_HOME="$check/home/.local/share" LARVA_CONFIG="$check/defaults.json" \
    sh "$check/larva/install.sh" --uninstall --prefix "$check/larva"
if [ -e "$check/larva/larva" ] || [ -e "$check/home/.local/bin/larva" ]; then
    echo "build-installer.sh: the uninstaller left larva behind" >&2
    exit 1
fi
rm -rf "$check"
echo "installer: $archive"
