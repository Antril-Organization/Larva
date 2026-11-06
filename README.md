# Larva

Larva is a compiler toolchain: a high-level IR (HIR) that language frontends produce, its lowering to Larva IR, an IL and a VM, native back ends (LLVM, a direct x86-64 one, WebAssembly), a native runtime with pluggable garbage collectors, project files (`.queen`), a package manager (Hive) and native-library bindings, all behind one command: `larva`.

Languages are not part of Larva: each is a **language pack** built in its own repository and loaded by larva (`larva languages` lists them).

## Layout

| Folder | What it is |
| --- | --- |
| `Larva.IL`, `Larva.IR`, `Larva.Runtime` | Larva IR (types, borrow checker, encoding), the IL it lowers to and the VM that runs it. |
| `Larva.Hir`, `Larva.Hir.Lowering`, `Larva.Hir.Services` | HIR (what frontends produce), its lowering to IR, and the services editors use (catalog, language views). |
| `Larva.Native`, `Larva.Llvm`, `Larva.X64`, `Larva.Wasm`, `Larva.Driver` | The native runtime, the back ends and the builders of executables and libraries. |
| `Larva.Build`, `Hive.Core`, `Larva.Bindings` | Project files, the Hive package manager, and bindings to native libraries. |
| `Larva.Cli` | `larva`: build, run, lsp, bind, hive, languages, new, config. |
| `Larva.Setup` | The Windows installer (wizard and silent). `tools/installer/install.sh` installs on Linux and macOS. |
| `Library` | The standard library (`System.Core`, `System.Bindings`), compiled into `.dll` libraries by the installer build. |
| `Templates` | The project templates of `larva new`. |
| `tests` | xUnit v3 tests (`dotnet test`). |
| `vscode-larva` | The VS Code extension: the language server client, `.queen` files and the commands. |
| `brand`, `tools` | Logos and file icons, and the build scripts. |

## Building and testing

```
dotnet build Larva.slnx
dotnet test --solution Larva.slnx
```

A working copy of larva loads language packs from the folders in `LARVA_LANGUAGES`. A language repository's build writes its pack as .NET code there; for Antril, the sibling repository `..\Antril` writes it to `..\Antril\artifacts\pack`.

```
set LARVA_LANGUAGES=..\Antril\artifacts\pack
dotnet Larva.Cli\bin\Debug\net10.0\larva.dll languages
```

## Installers

- `tools/build-installer.ps1` (Windows) and `tools/build-installer.sh` (Linux, macOS) publish larva natively, sign the native language packs (by default from `..\Antril\artifacts\languages`; `-Languages` or `LARVA_PACKS` say otherwise), compile the standard library and package everything.
- `.github/workflows/installers.yml` builds all three on their own systems.
