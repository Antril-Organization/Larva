# Larva for Visual Studio Code

The Larva toolchain in VS Code: its language server (`larva lsp`) uses the real compiler for every language installed in Larva, and its commands create, run, check and build projects and bind native libraries.

Each language comes in an extension of its own (its syntax, snippets and icons), which installs this one with it. This extension serves every language such an extension declares, and `.queen` projects.

## Features

- **Errors as you type**: the compiler's own diagnostics for the whole project, a moment after each edit.
- **Completion, hover, go to definition, signature help, outline, workspace symbols and highlights**, from the language server.
- **`.queen` projects**: errors (QN…) as you type, completion of chunk names, of the keys of the chunk you are in and of the values a key takes, and hover on keys. A project's dependencies, its `assembly:` libraries (such as bindings) and the GC domains of its `gc:`-different dependencies are what its files are analyzed against.
- **Icons** for `.queen` (a crown) and `.bind` files.
- **Commands** (in a terminal named Larva):
  - *Larva: New Project from a Template*: `larva new` with one of Larva's templates (`Console-App`, `Library`…), written in the language you pick, in a folder of its own with its `.queen`; then opened.
  - *Larva: Run* (Ctrl+F5, or the play button): in a project, `larva run` (built only when something changed, then run); a lone file is built into a temporary folder and run.
  - *Larva: Check*: the errors only (`larva build --check`).
  - *Larva: Build Native Executable*: the project's BUILD `output`, or `NAME.exe` next to a lone file.
  - *Larva: Build WebAssembly*: `NAME.wasm`, `NAME.js` and `NAME.html` (LLVM, optimized).
  - *Larva: Bind a Native Library (C header or .bind)*: `larva bind` on a `.dll`/`.so` with a C header (preprocessed by clang when it is installed) or a `.bind` description: the Larva library `Name.dll` (`Name.Bindings.dll` when `Name.dll` is the native library's own name), its source in the language you pick, and `Name.bind`. *Larva: List a Native Library's Exports* prints what a library exports.
  - *Larva: Generate Bindings from the Profile Open*: `larva bind --profile` on the binding profile open in the editor (a class deriving from `System.Bindings.BindingProfile`).
  - *Larva: Show Type (decompiled)* and *Larva: Restart Language Server*.

A file belongs to the nearest `.queen` project above it: the commands build that project. Otherwise the file is built alone.

## Settings

| Setting | What it does |
| --- | --- |
| `larva.path` | The `larva` command. Empty: `LARVA_HOME`, then where the Larva installer puts it, then `larva` on the PATH. |
| `larva.trace.server` | Logs the LSP messages in the Output panel. |

## Requirements

- **Larva 0.8.0 or newer**, installed on its own (the extension does not bring it): on Windows `LarvaSetup-<version>-win-x64.exe`, on Linux and macOS the `install.sh` of `Larva-<version>-<platform>.tar.gz`. The extension finds it where they put it, through `LARVA_HOME`, on the PATH, or at `larva.path`, and says so when it is missing or too old.
- The language packs of the languages you write (`larva languages` lists them).
- For native executables: the MSVC build tools on Windows, a C compiler elsewhere. For WebAssembly: LLVM's `wasm-ld`, and Node or a browser to run it.

## For language extensions

A language becomes a Larva language in VS Code by declaring it in its extension's `package.json`. Its VS Code language id is the id of its Larva language pack:

```json
"larva": { "languages": ["mylang"] },
"extensionDependencies": ["larva.larva"],
"activationEvents": ["onLanguage:mylang"]
```

The language server then serves its files (and the library types it decompiles, under the `larva:` scheme). The commands work on its files, and *New Project* and *Bind* offer it.

## Building the extension

```
npm install
npm run package          # makes larva-VERSION.vsix (without larva itself)
code --install-extension larva-0.8.0.vsix
```

While working on the toolchain, open this folder in VS Code and press F5. The extension uses `../artifacts/larva/larva.exe` when no larva is installed, then `../Larva.Cli/bin/Debug/net10.0/larva.dll`.
