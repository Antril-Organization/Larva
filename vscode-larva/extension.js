// Larva for VS Code: starts the language server (`larva lsp`) for .queen projects and for every language whose VS Code
// extension declares itself a Larva language, and adds commands that call `larva` (new, run, build, bind) in a terminal.
//
// A language extension declares its languages in its package.json, by the VS Code language ids it contributes, which
// are also the ids of its Larva language packs (`larva languages` lists them):
//
//   "larva": { "languages": ["mylang"] },
//   "extensionDependencies": ["larva.larva"]
const vscode = require('vscode');
const path = require('path');
const fs = require('fs');
const os = require('os');
const { execFile } = require('child_process');
const { LanguageClient, TransportKind } = require('vscode-languageclient/node');

/** The oldest larva this extension works with (`larva --version`). */
const MINIMUM_LARVA = '0.8.0';

let client;

// ── Languages ──────────────────────────────────────

/**
 * The languages installed extensions declare for Larva (`"larva": { "languages": [...] }`): each with the file
 * extensions VS Code gives it (from the same extension's `contributes.languages`).
 */
function larvaLanguages() {
  const found = new Map();
  for (const extension of vscode.extensions.all) {
    const manifest = extension.packageJSON || {};
    const declared = manifest.larva && manifest.larva.languages;
    if (!Array.isArray(declared)) {
      continue;
    }
    const contributed = (manifest.contributes && manifest.contributes.languages) || [];
    for (const id of declared) {
      if (typeof id !== 'string' || found.has(id)) {
        continue;
      }
      const language = contributed.find((l) => l.id === id);
      const name = language && language.aliases && language.aliases.length > 0 ? language.aliases[0] : id;
      found.set(id, { id, name, extensions: ((language && language.extensions) || []).map((e) => e.toLowerCase()) });
    }
  }
  return [...found.values()];
}

/** Whether a file is a source file of a Larva language or a .queen project. */
function isLarvaFile(file, languages) {
  const lower = file.toLowerCase();
  return lower.endsWith('.queen') || languages.some((l) => l.extensions.some((e) => lower.endsWith(e)));
}

// ── Finding larva ──────────────────────────────────

/** The command that starts an executable or a framework-dependent .dll (`dotnet x.dll`). */
function launch(file) {
  return file.toLowerCase().endsWith('.dll') ? { command: 'dotnet', args: [file] } : { command: file, args: [] };
}

function firstExisting(candidates) {
  return candidates.find((c) => c && fs.existsSync(c));
}

function onPath(exe) {
  for (const folder of (process.env.PATH || '').split(path.delimiter)) {
    if (folder && fs.existsSync(path.join(folder, exe))) {
      return path.join(folder, exe);
    }
  }
  return undefined;
}

/** Where the Larva installer puts larva on this system. */
function installedLarva(exe) {
  switch (process.platform) {
    case 'win32':
      return path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'Programs', 'Larva', exe);
    case 'darwin':
      return path.join(os.homedir(), 'Library', 'Application Support', 'Larva', exe);
    default:
      return path.join(process.env.XDG_DATA_HOME || path.join(os.homedir(), '.local', 'share'), 'larva', exe);
  }
}

/**
 * The Larva toolchain (`larva`), installed on its own (not with the extension): the `larva.path` setting, LARVA_HOME,
 * where the installer puts it, the PATH, or (working from the repository) its builds. Undefined when there is none.
 */
function larvaCommand(context) {
  const configured = vscode.workspace.getConfiguration('larva').get('path');
  const exe = process.platform === 'win32' ? 'larva.exe' : 'larva';
  const found = firstExisting([
    configured,
    process.env.LARVA_HOME ? path.join(process.env.LARVA_HOME, exe) : undefined,
    installedLarva(exe),
    onPath(exe),
    // Working from the repository: the published native build, then the debug build.
    context.asAbsolutePath(path.join('..', 'artifacts', 'larva', exe)),
    context.asAbsolutePath(path.join('..', 'Larva.Cli', 'bin', 'Debug', 'net10.0', 'larva.dll')),
  ]);
  return found ? launch(found) : undefined;
}

async function larvaMissing() {
  const choice = await vscode.window.showErrorMessage(
    'Larva is not installed. Run the Larva installer (it installs larva for your user and puts it on the PATH), or set `larva.path` to the larva program.',
    'Set Path',
    'Retry'
  );
  if (choice === 'Set Path') {
    vscode.commands.executeCommand('workbench.action.openSettings', 'larva.path');
  } else if (choice === 'Retry') {
    vscode.commands.executeCommand('larva.restartServer');
  }
}

function requireLarva(context) {
  const larva = larvaCommand(context);
  if (!larva) {
    larvaMissing();
  }
  return larva;
}

function older(version, than) {
  const a = version.split('.').map((n) => parseInt(n, 10) || 0);
  const b = than.split('.').map((n) => parseInt(n, 10) || 0);
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    if ((a[i] || 0) !== (b[i] || 0)) {
      return (a[i] || 0) < (b[i] || 0);
    }
  }
  return false;
}

function checkVersion(larva) {
  execFile(larva.command, [...larva.args, '--version'], { timeout: 15000 }, (error, stdout) => {
    const match = /larva\s+(\d+(\.\d+)*)/.exec(stdout || '');
    if (!match || older(match[1], MINIMUM_LARVA)) {
      vscode.window.showWarningMessage(
        `Larva: the larva found (${larva.command}) is ${match ? match[1] : 'an older one'}; this extension needs Larva ${MINIMUM_LARVA} or newer. Install the new Larva.`
      );
    }
  });
}

// ── The language server ────────────────────────────

function startClient(context) {
  const larva = larvaCommand(context);
  if (!larva) {
    larvaMissing();
    return;
  }
  checkVersion(larva);
  const languages = larvaLanguages();
  vscode.commands.executeCommand('setContext', 'larva.languages', languages.map((l) => l.id));
  const executable = { command: larva.command, args: [...larva.args, 'lsp'], transport: TransportKind.stdio };
  const selector = [{ scheme: 'file', language: 'queen' }];
  for (const language of languages) {
    selector.push(
      { scheme: 'file', language: language.id },
      { scheme: 'untitled', language: language.id },
      // Library types decompiled by the server (read-only): colored and navigable from what the decompiler wrote.
      { scheme: 'larva', language: language.id }
    );
  }
  const watched = ['queen', ...languages.flatMap((l) => l.extensions.map((e) => e.replace(/^\./, '')))];
  client = new LanguageClient(
    'larva',
    'Larva',
    { run: executable, debug: executable },
    {
      documentSelector: selector,
      synchronize: {
        configurationSection: 'larva',
        fileEvents: watched.map((e) => vscode.workspace.createFileSystemWatcher(`**/*.${e}`)),
      },
    }
  );
  client.start();
}

async function restartClient(context) {
  if (client) {
    const old = client;
    client = undefined;
    try {
      await old.stop();
    } catch (_) {
      // A server too busy to answer the shutdown (a large project being analyzed): the client ends it anyway.
    }
  }
  startClient(context);
}

// ── Commands ───────────────────────────────────────

/**
 * What a command works on: the nearest .queen project (here or above), or the file itself. `name` names what is built.
 */
function target() {
  const editor = vscode.window.activeTextEditor;
  if (!editor || !isLarvaFile(editor.document.uri.fsPath, larvaLanguages())) {
    vscode.window.showWarningMessage('Larva: open a source file or a .queen project first.');
    return undefined;
  }
  const file = editor.document.uri.fsPath;
  for (let folder = path.dirname(file); ; folder = path.dirname(folder)) {
    let entries;
    try {
      entries = fs.readdirSync(folder);
    } catch (_) {
      break;
    }
    const queen = entries.find((f) => f.toLowerCase().endsWith('.queen'));
    if (queen) {
      // The project file itself: `larva build` reads its sources, dependencies, `assembly:` libraries and BUILD settings.
      return { cwd: folder, name: path.basename(queen, path.extname(queen)), files: [path.join(folder, queen)], queen: true };
    }
    if (path.dirname(folder) === folder) {
      break;
    }
  }
  return { cwd: path.dirname(file), name: path.basename(file, path.extname(file)), files: [file] };
}

function quote(arg) {
  return /[\s"&()]/.test(arg) ? `"${arg.replace(/"/g, '\\"')}"` : arg;
}

/** Runs command lines one after another in the Larva terminal, each only when the one before succeeded. */
function inTerminal(where, lines) {
  let terminal = vscode.window.terminals.find((t) => t.name === 'Larva');
  if (!terminal) {
    terminal = vscode.window.createTerminal({ name: 'Larva', cwd: where.cwd });
  }
  terminal.show(true);
  const shell = (vscode.env.shell || '').toLowerCase();
  const powershell = process.platform === 'win32' && (shell === '' || shell.includes('powershell') || shell.includes('pwsh'));
  const text = lines.map((line) => {
    const words = line.map(quote).join(' ');
    return powershell && words.startsWith('"') ? `& ${words}` : words;
  });
  // PowerShell 5 has no `&&`.
  terminal.sendText(powershell ? text.join('; if ($?) { ') + ' }'.repeat(text.length - 1) : text.join(' && '));
}

function larvaBuild(larva, where, options) {
  return [larva.command, ...larva.args, 'build', ...options, ...where.files];
}

function folderOf() {
  const editor = vscode.window.activeTextEditor;
  if (editor && editor.document.uri.scheme === 'file') {
    return path.dirname(editor.document.uri.fsPath);
  }
  const folders = vscode.workspace.workspaceFolders;
  return folders && folders.length > 0 ? folders[0].uri.fsPath : os.homedir();
}

/** The language a command writes in: the one of the file open, the only one installed, or the one the user picks. */
async function pickLanguage(prompt) {
  const languages = larvaLanguages();
  const editor = vscode.window.activeTextEditor;
  const open = editor && languages.find((l) => l.id === editor.document.languageId);
  if (open) {
    return open;
  }
  if (languages.length <= 1) {
    return languages[0];
  }
  const picked = await vscode.window.showQuickPick(
    languages.map((l) => ({ label: l.name, description: l.extensions.join(' '), language: l })),
    { placeHolder: prompt }
  );
  return picked ? picked.language : null;
}

/**
 * `larva bind`: bindings for a native library from a C header or a `.bind` description, written as the Larva library
 * `Name.dll` plus the source (in the language picked) and the derived description. A library with the native one's file
 * name would be loaded in its place, so then it is `Name.Bindings.dll`.
 */
async function bind(context, listOnly) {
  const library = await vscode.window.showInputBox({
    prompt: listOnly ? 'Library whose exports to list' : 'Native library to bind (a name in the system folder, or a path)',
    value: process.platform === 'win32' ? 'kernel32.dll' : 'libc.so.6',
  });
  if (!library) {
    return;
  }
  const larva = requireLarva(context);
  if (!larva) {
    return;
  }
  const where = { cwd: folderOf() };
  if (listOnly) {
    inTerminal(where, [[larva.command, ...larva.args, 'bind', '--list', library]]);
    return;
  }
  const picked = await vscode.window.showOpenDialog({
    canSelectMany: false,
    openLabel: 'Bind',
    title: 'The C header or .bind description of the library',
    filters: { 'C headers and descriptions': ['h', 'bind'], 'All files': ['*'] },
  });
  if (!picked || picked.length === 0) {
    return;
  }
  const language = await pickLanguage('Language of the generated source');
  if (language === null) {
    return;
  }
  const description = picked[0].fsPath;
  const stem = path.basename(library, path.extname(library)).replace(/^lib/, '');
  const name = await vscode.window.showInputBox({
    prompt: 'Name of the bindings (the class, and the library and source files)',
    value: stem.charAt(0).toUpperCase() + stem.slice(1),
  });
  if (!name) {
    return;
  }
  const nativeFile = path.extname(library) ? path.basename(library) : path.basename(library) + '.dll';
  const libraryFile = (name + '.dll').toLowerCase() === nativeFile.toLowerCase() ? name + '.Bindings.dll' : name + '.dll';
  const options = ['-o', path.join(where.cwd, libraryFile)];
  if (language && language.extensions.length > 0) {
    options.push('--source', path.join(where.cwd, name + language.extensions[0]));
  }
  if (description.toLowerCase().endsWith('.h')) {
    options.push('--describe', path.join(where.cwd, name + '.bind'));
  }
  inTerminal(where, [[larva.command, ...larva.args, 'bind', library, description, ...options]]);
}

/** What `larva new` lists: the templates (identifier and description). */
function templates(larva) {
  return new Promise((resolve) => {
    execFile(larva.command, [...larva.args, 'new'], { timeout: 30000 }, (error, stdout) => {
      const found = [];
      let inTemplates = false;
      for (const line of (stdout || '').split(/\r?\n/)) {
        if (line.startsWith('Templates')) {
          inTemplates = true;
        } else if (inTemplates && line.trim().length === 0) {
          break;
        } else if (inTemplates) {
          const match = /^\s+(\S+)\s+(.*)$/.exec(line);
          if (match && match[1] !== 'none') {
            found.push({ label: match[1], detail: match[2] });
          }
        }
      }
      resolve(found);
    });
  });
}

/** `larva new`: a project from one of Larva's templates, written in the language picked, in a folder of its own. */
async function newProject(context) {
  const larva = requireLarva(context);
  if (!larva) {
    return;
  }
  const choices = await templates(larva);
  if (choices.length === 0) {
    vscode.window.showWarningMessage('Larva: larva has no templates (larva new lists them).');
    return;
  }
  const template = await vscode.window.showQuickPick(choices, { placeHolder: 'Template of the new project' });
  if (!template) {
    return;
  }
  const language = await pickLanguage('Language of the new project');
  if (language === null) {
    return;
  }
  const name = await vscode.window.showInputBox({
    prompt: 'Name of the project (its folder, its namespace and its executable or library)',
    value: 'MyApp',
    validateInput: (text) => (/^[A-Za-z_][A-Za-z0-9_]*$/.test(text) ? undefined : 'letters, digits and _, not starting with a digit'),
  });
  if (!name) {
    return;
  }
  const parent = await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, openLabel: 'Create here', title: 'Where the project folder goes' });
  if (!parent || parent.length === 0) {
    return;
  }
  const folder = parent[0].fsPath;
  const args = [...larva.args, 'new', template.label, name, ...(language ? ['--lang', language.id] : [])];
  execFile(larva.command, args, { cwd: folder, timeout: 60000 }, async (error, stdout, stderr) => {
    if (error) {
      vscode.window.showErrorMessage(`Larva: larva new failed: ${(stderr || stdout || error.message).trim()}`);
      return;
    }
    const project = path.join(folder, name);
    const open = await vscode.window.showInformationMessage(`Larva: ${name} created (${template.label}).`, 'Open', 'Open in New Window');
    if (open) {
      vscode.commands.executeCommand('vscode.openFolder', vscode.Uri.file(project), { forceNewWindow: open !== 'Open' });
    }
  });
}

async function build(context, kind) {
  const where = target();
  if (!where) {
    return;
  }
  const larva = requireLarva(context);
  if (!larva) {
    return;
  }
  await vscode.workspace.saveAll(false);
  const exe = process.platform === 'win32' ? '.exe' : '';
  switch (kind) {
    case 'run': {
      if (where.queen) {
        // A project: `larva run` builds it when something changed and runs it.
        inTerminal(where, [[larva.command, ...larva.args, 'run', where.files[0]]]);
        break;
      }
      // A lone file: built into a temporary folder, then run.
      const folder = path.join(os.tmpdir(), 'larva-run');
      fs.mkdirSync(folder, { recursive: true });
      const output = path.join(folder, where.name + exe);
      inTerminal(where, [larvaBuild(larva, where, ['-o', output]), [output]]);
      break;
    }
    case 'check':
      inTerminal(where, [larvaBuild(larva, where, ['--check'])]);
      break;
    case 'exe':
      // A project builds where its BUILD `output` says (bin/NAME.exe by default); a lone file next to itself.
      inTerminal(where, [larvaBuild(larva, where, where.queen ? [] : ['-o', path.join(where.cwd, where.name + exe)])]);
      break;
    case 'wasm': {
      // NAME.wasm, NAME.js (`node NAME.js` runs it) and NAME.html, optimized by LLVM.
      inTerminal(where, [larvaBuild(larva, where, ['--target', 'wasm-llvm', '-o', path.join(where.cwd, where.name + '.js')])]);
      break;
    }
  }
}

async function bindProfile(context) {
  // The binding profile open in the editor (a class deriving from System.Bindings.BindingProfile): `larva bind
  // --profile` runs it and writes the library (.dll) and the source its settings name.
  const editor = vscode.window.activeTextEditor;
  const languages = larvaLanguages();
  if (!editor || !languages.some((l) => l.id === editor.document.languageId)) {
    vscode.window.showWarningMessage('Larva: open a binding profile (a source file with a BindingProfile class) first.');
    return;
  }
  await editor.document.save();
  const larva = requireLarva(context);
  if (!larva) {
    return;
  }
  const file = editor.document.uri.fsPath;
  inTerminal({ cwd: path.dirname(file) }, [[larva.command, ...larva.args, 'bind', '--profile', file]]);
}

async function showType() {
  // A library type decompiled (System.List, StringBuilder…) into the language picked, opened read-only.
  if (!client) {
    return;
  }
  const language = await pickLanguage('Language to show the type in');
  if (!language) {
    return;
  }
  const name = await vscode.window.showInputBox({ prompt: 'Type to show (System.List, StringBuilder…)' });
  if (!name) {
    return;
  }
  const result = await client.sendRequest('larva/decompile', { type: name, language: language.id });
  if (!result) {
    vscode.window.showWarningMessage(`Larva: no type ${name}`);
    return;
  }
  const document = await vscode.workspace.openTextDocument(vscode.Uri.parse(result.uri));
  await vscode.languages.setTextDocumentLanguage(document, language.id);
  await vscode.window.showTextDocument(document, { preview: true });
}

function activate(context) {
  startClient(context);
  context.subscriptions.push(
    // A library type decompiled by the server (`larva:` + the file it wrote): read-only, outside every project.
    vscode.workspace.registerTextDocumentContentProvider('larva', {
      provideTextDocumentContent(uri) {
        return fs.readFileSync(uri.fsPath, 'utf8');
      },
    }),
    // A language extension installed or removed: the server serves the languages there are now.
    vscode.extensions.onDidChange(() => restartClient(context)),
    vscode.commands.registerCommand('larva.run', () => build(context, 'run')),
    vscode.commands.registerCommand('larva.check', () => build(context, 'check')),
    vscode.commands.registerCommand('larva.buildExe', () => build(context, 'exe')),
    vscode.commands.registerCommand('larva.buildWasm', () => build(context, 'wasm')),
    vscode.commands.registerCommand('larva.newProject', () => newProject(context)),
    vscode.commands.registerCommand('larva.bind', () => bind(context, false)),
    vscode.commands.registerCommand('larva.bindList', () => bind(context, true)),
    vscode.commands.registerCommand('larva.bindProfile', () => bindProfile(context)),
    vscode.commands.registerCommand('larva.showType', () => showType()),
    vscode.commands.registerCommand('larva.restartServer', () => restartClient(context)),
    { dispose: () => (client ? client.stop() : undefined) }
  );
  // What other extensions can ask (vscode.extensions.getExtension('larva.larva').exports).
  return { larvaLanguages };
}

function deactivate() {
  return client ? client.stop() : undefined;
}

module.exports = { activate, deactivate };
