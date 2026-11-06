#!/bin/sh
# Larva's installer for Linux and macOS, for the current user (no root):
#
#   ./install.sh [--prefix <folder>] [--no-path] [--no-hive] [--no-types]
#   ./install.sh --uninstall [--prefix <folder>]
#
# Copies larva into <folder> (Linux: ~/.local/share/larva, macOS: ~/Library/Application Support/Larva), puts a `larva`
# command in ~/.local/bin (and that folder on the PATH of new shells unless --no-path), makes
# https://www.antril.org/hive-api larva's default package registry (not with --no-hive), and shows larva's files
# (.lva, .lvo, .lvm, .bind, .queen, .hive and each language's source files) with their icons (not with --no-types).
# The copy in <folder> (install.sh --uninstall) takes all of it back, and nothing else.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
system=$(uname -s)
data=${XDG_DATA_HOME:-$HOME/.local/share}
case "$system" in
    Darwin) prefix="$HOME/Library/Application Support/Larva" ;;
    *) prefix="$data/larva" ;;
esac
bin="$HOME/.local/bin"
mark="# added by Larva install.sh"
registry="https://www.antril.org/hive-api"
uninstall=no path=yes hive=yes types=yes

while [ $# -gt 0 ]; do
    case "$1" in
        --prefix) prefix=$2; shift ;;
        --no-path) path=no ;;
        --no-hive) hive=no ;;
        --no-types) types=no ;;
        --uninstall) uninstall=yes ;;
        -h|--help) sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "install.sh: unknown option \`$1\` (--help lists them)" >&2; exit 2 ;;
    esac
    shift
done

# ── File types ─────────────────────────────────────

# Each kind of file larva reads: extension, type name, what it is, icon (without its extension) in <folder>/icons or a
# language pack; one per line, tab-separated. Language packs add their source files (their manifest's extensions).
file_types() {
    printf '%s\t%s\t%s\t%s\n' \
        lva library "Larva library" "$prefix/icons/file-lva" \
        lvo object "Larva object file" "$prefix/icons/file-lvo" \
        lvm module "Larva module" "$prefix/icons/file-lvm" \
        bind binding "Larva binding description" "$prefix/icons/file-bind" \
        queen project "Larva project" "$prefix/icons/file-queen" \
        hive package "Hive package" "$prefix/icons/file-hive"
    for manifest in "$prefix"/Languages/*/language.json; do
        [ -f "$manifest" ] || continue
        pack=$(dirname "$manifest")
        id=$(sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$manifest" | head -n 1)
        name=$(sed -n 's/.*"name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$manifest" | head -n 1)
        icon=$(sed -n 's/.*"icon"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$manifest" | head -n 1)
        icon=${icon:+$pack/${icon%.*}}
        for extension in $(sed -n 's/.*"extensions"[[:space:]]*:[[:space:]]*\[\([^]]*\)\].*/\1/p' "$manifest" | tr -d '" ' | tr ',' ' '); do
            printf '%s\t%s\t%s\t%s\n' "${extension#.}" "source-$id" "$name source file" "${icon:-$prefix/icons/file-lva}"
        done
    done
}

tab=$(printf '\t')

# Linux: MIME types (shared-mime-info) with icons from the user's hicolor theme.
linux_types() {
    mkdir -p "$data/mime/packages" "$data/icons/hicolor/scalable/mimetypes"
    {
        echo '<?xml version="1.0" encoding="UTF-8"?>'
        echo '<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">'
        file_types | while IFS="$tab" read -r extension type description icon; do
            echo "  <mime-type type=\"application/x-larva-$type\">"
            echo "    <comment>$description</comment>"
            echo "    <icon name=\"application-x-larva-$type\"/>"
            echo "    <glob pattern=\"*.$extension\"/>"
            echo "  </mime-type>"
            if [ -f "$icon.svg" ]; then cp "$icon.svg" "$data/icons/hicolor/scalable/mimetypes/application-x-larva-$type.svg"; fi
        done
        echo '</mime-info>'
    } > "$data/mime/packages/larva.xml"
    refresh_linux
}

refresh_linux() {
    if command -v update-mime-database > /dev/null 2>&1; then update-mime-database "$data/mime" > /dev/null 2>&1 || true; fi
    if command -v gtk-update-icon-cache > /dev/null 2>&1; then gtk-update-icon-cache -f -t "$data/icons/hicolor" > /dev/null 2>&1 || true; fi
}

remove_linux_types() {
    rm -f "$data/mime/packages/larva.xml" "$data"/icons/hicolor/scalable/mimetypes/application-x-larva-*.svg
    refresh_linux
}

app="$HOME/Applications/Larva Files.app"
lsregister=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister

# macOS: an application bundle that declares the types (Uniform Type Identifiers) and their icons, registered with
# Launch Services; it opens nothing (its handler rank is None), it only names and draws larva's files.
mac_types() {
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    cp "$prefix/icons/larva-app.icns" "$app/Contents/Resources/Larva.icns"
    printf '#!/bin/sh\nexit 0\n' > "$app/Contents/MacOS/larva-files"
    chmod +x "$app/Contents/MacOS/larva-files"
    types="" documents=""
    while IFS="$tab" read -r extension type description icon; do
        if [ -f "$icon.icns" ]; then cp "$icon.icns" "$app/Contents/Resources/$type.icns"; fi
        conforms="public.data"
        case "$type" in source-*|binding|project) conforms="public.plain-text" ;; esac
        types="$types
    <dict>
      <key>UTTypeIdentifier</key><string>org.larva.$type</string>
      <key>UTTypeDescription</key><string>$description</string>
      <key>UTTypeConformsTo</key><array><string>$conforms</string></array>
      <key>UTTypeIconFile</key><string>$type.icns</string>
      <key>UTTypeTagSpecification</key><dict><key>public.filename-extension</key><array><string>$extension</string></array></dict>
    </dict>"
        documents="$documents
    <dict>
      <key>CFBundleTypeName</key><string>$description</string>
      <key>CFBundleTypeRole</key><string>None</string>
      <key>LSHandlerRank</key><string>None</string>
      <key>CFBundleTypeIconFile</key><string>$type.icns</string>
      <key>LSItemContentTypes</key><array><string>org.larva.$type</string></array>
    </dict>"
    done <<EOF
$(file_types)
EOF
    cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>org.larva.files</string>
  <key>CFBundleName</key><string>Larva Files</string>
  <key>CFBundleExecutable</key><string>larva-files</string>
  <key>CFBundleIconFile</key><string>Larva.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$("$prefix/larva" --version 2>/dev/null | sed 's/[^0-9.].*//' | head -n 1)</string>
  <key>LSUIElement</key><true/>
  <key>UTExportedTypeDeclarations</key>
  <array>$types
  </array>
  <key>CFBundleDocumentTypes</key>
  <array>$documents
  </array>
</dict>
</plist>
EOF
    if [ -x "$lsregister" ]; then "$lsregister" -f "$app" || true; fi
}

remove_mac_types() {
    if [ -d "$app" ]; then
        if [ -x "$lsregister" ]; then "$lsregister" -u "$app" > /dev/null 2>&1 || true; fi
        rm -rf "$app"
    fi
}

# ── The PATH of new shells ─────────────────────────

profile() {
    case "$system" in
        Darwin) echo "$HOME/.zprofile" ;;
        *) echo "$HOME/.profile" ;;
    esac
}

remove_path() {
    file=$(profile)
    if [ -f "$file" ] && grep -q "$mark" "$file"; then
        { grep -v "$mark" "$file" || true; } > "$file.larva"
        mv "$file.larva" "$file"
    fi
}

# ── Uninstall ──────────────────────────────────────

if [ "$uninstall" = yes ]; then
    if [ ! -f "$prefix/larva" ]; then
        echo "install.sh: no larva in $prefix" >&2
        exit 1
    fi
    remove_linux_types_or_mac() { if [ "$system" = Darwin ]; then remove_mac_types; else remove_linux_types; fi; }
    remove_linux_types_or_mac
    remove_path
    if [ -f "$bin/larva" ] && grep -q "$mark" "$bin/larva"; then rm -f "$bin/larva"; fi
    # Only the files the installation put there (its list); anything else in the folder stays, and the folder then too.
    if [ -f "$prefix/uninstall.files" ]; then
        while IFS= read -r file; do
            [ -n "$file" ] && rm -f "$prefix/$file"
        done < "$prefix/uninstall.files"
        rm -f "$prefix/uninstall.files"
        find "$prefix" -depth -type d -empty -exec rmdir {} \; 2> /dev/null || true
    fi
    echo "Larva was removed."
    exit 0
fi

# ── Install ────────────────────────────────────────

if [ ! -f "$here/larva" ]; then
    echo "install.sh: run the install.sh that comes with larva (next to it)" >&2
    exit 1
fi
version=$("$here/larva" --version 2>/dev/null || echo "?")
echo "Larva $version into $prefix"
mkdir -p "$prefix" "$bin"
# Every file of the archive (this script too: in <folder> it is the uninstaller), and the list of them.
(cd "$here" && find . -type f ! -name uninstall.files | sed 's|^\./||') > "$prefix/uninstall.files.new"
if [ "$here" != "$(cd "$prefix" && pwd)" ]; then
    while IFS= read -r file; do
        mkdir -p "$(dirname "$prefix/$file")"
        cp -p "$here/$file" "$prefix/$file"
    done < "$prefix/uninstall.files.new"
fi
mv "$prefix/uninstall.files.new" "$prefix/uninstall.files"
chmod +x "$prefix/larva" "$prefix/install.sh"
if [ "$system" = Darwin ]; then
    # Downloaded files are quarantined: larva's own (its folder only) are let run.
    xattr -dr com.apple.quarantine "$prefix" 2> /dev/null || true
fi

# The `larva` command: a launcher (larva finds its libraries next to itself, not next to a link).
if [ -f "$bin/larva" ] && ! grep -q "$mark" "$bin/larva"; then
    echo "install.sh: $bin/larva is not Larva's: left as it is (larva is $prefix/larva)" >&2
else
    printf '#!/bin/sh\n%s launcher (install.sh --uninstall removes it)\nexec "%s/larva" "$@"\n' "$mark" "$prefix" > "$bin/larva"
    chmod +x "$bin/larva"
fi

if [ "$path" = yes ]; then
    case ":$PATH:" in
        *":$bin:"*) ;;
        *)
            file=$(profile)
            if ! { [ -f "$file" ] && grep -q "$mark" "$file"; }; then
                printf 'export PATH="$HOME/.local/bin:$PATH" %s\n' "$mark" >> "$file"
            fi
            echo "$bin is on the PATH of new shells ($file)."
            ;;
    esac
fi

if [ "$hive" = yes ]; then
    current=$("$prefix/larva" config hive.provider 2>/dev/null || echo "-")
    case "$current" in
        *www.antril.org/hive-api*) ;;
        -|"") "$prefix/larva" config hive.provider "$registry" > /dev/null ;;
        *) "$prefix/larva" config hive.provider "$registry,$current" > /dev/null ;;
    esac
fi

if [ "$types" = yes ]; then
    if [ "$system" = Darwin ]; then mac_types; else linux_types; fi
fi

if command -v cc > /dev/null 2>&1; then
    echo "  ok   C compiler (native executables)"
else
    echo "  --   no C compiler (cc): \`larva build\` of native executables needs one (clang or gcc; on macOS: xcode-select --install)"
fi
if command -v clang > /dev/null 2>&1; then
    echo "  ok   clang (C header preprocessing in larva bind)"
else
    echo "  --   clang not found: larva bind reads headers without preprocessing"
fi
echo "Installed: $("$bin/larva" --version 2>/dev/null || echo "$version")"
