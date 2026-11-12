// The programs the Larva commands run (larva build, larva run, larva bind, the program just built): each a program
// and its arguments as separate values, never a command line. No shell reads them, so a folder or a name with `;`,
// `&`, `|`, `$` or quotes in it is only ever a folder or a name. This module needs no VS Code (node --test runs its tests).
'use strict';
const path = require('path');

/** `larva build <options> <files>`. */
function larvaBuild(larva, where, options) {
  return { command: larva.command, args: [...larva.args, 'build', ...options, ...where.files] };
}

/**
 * What a build command runs, in order, each only when the one before succeeded: `run` runs a project (`larva run`), or
 * builds a lone file into `<tmpdir>/larva-run` and runs it; `check` type-checks; `exe` builds a native executable (a
 * project where its BUILD `output` says, a lone file next to itself); `wasm` builds NAME.js, NAME.wasm and NAME.html.
 */
function buildSteps(kind, larva, where, environment) {
  const exe = environment.platform === 'win32' ? '.exe' : '';
  switch (kind) {
    case 'run': {
      if (where.queen) {
        return [{ command: larva.command, args: [...larva.args, 'run', where.files[0]] }];
      }
      const output = path.join(environment.tmpdir, 'larva-run', where.name + exe);
      return [larvaBuild(larva, where, ['-o', output]), { command: output, args: [] }];
    }
    case 'check':
      return [larvaBuild(larva, where, ['--check'])];
    case 'exe':
      return [larvaBuild(larva, where, where.queen ? [] : ['-o', path.join(where.cwd, where.name + exe)])];
    case 'wasm':
      return [larvaBuild(larva, where, ['--target', 'wasm-llvm', '-o', path.join(where.cwd, where.name + '.js')])];
    default:
      throw new Error(`unknown build kind ${kind}`);
  }
}

/**
 * The files `larva bind` writes for the native library `library` (a name or a path) as the bindings `name`, in `folder`:
 * the Larva library `Name.dll` (a library with the native one's file name would be loaded in its place, so then it is
 * `Name.Bindings.dll`), the source in the language's first extension when there is one, and the description derived
 * from a C header.
 */
function bindOptions(folder, name, library, description, language) {
  const nativeFile = path.extname(library) ? path.basename(library) : path.basename(library) + '.dll';
  const libraryFile = (name + '.dll').toLowerCase() === nativeFile.toLowerCase() ? name + '.Bindings.dll' : name + '.dll';
  const options = ['-o', path.join(folder, libraryFile)];
  if (language && language.extensions.length > 0) {
    options.push('--source', path.join(folder, name + language.extensions[0]));
  }
  if (description.toLowerCase().endsWith('.h')) {
    options.push('--describe', path.join(folder, name + '.bind'));
  }
  return options;
}

/** `larva bind <library> <description> <options>`, or `larva bind --list <library>`. */
function bindStep(larva, library, description, options) {
  return description === undefined
    ? { command: larva.command, args: [...larva.args, 'bind', '--list', library] }
    : { command: larva.command, args: [...larva.args, 'bind', library, description, ...options] };
}

/** `larva bind --profile <file>`. */
function bindProfileStep(larva, file) {
  return { command: larva.command, args: [...larva.args, 'bind', '--profile', file] };
}

module.exports = { larvaBuild, buildSteps, bindOptions, bindStep, bindProfileStep };
