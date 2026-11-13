// node --test: the commands the extension runs are programs with argument lists, never command lines.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('path');
const { larvaBuild, buildSteps, bindOptions, bindStep, bindProfileStep } = require('../commands');

const exe = { command: 'C:\\Programs\\Larva\\larva.exe', args: [] };
const dll = { command: 'dotnet', args: ['C:\\repo\\Larva.Cli\\bin\\Debug\\net10.0\\larva.dll'] };
const hostile = 'C:\\work\\a;calc;b & del *.* | $(x) `y` "z"';
const file = path.join(hostile, 'Program.ant');
const lone = { cwd: hostile, name: 'Program', files: [file] };
const project = { cwd: hostile, name: 'App', files: [path.join(hostile, 'App.queen')], queen: true };
const windows = { platform: 'win32', tmpdir: 'C:\\Temp' };
const posix = { platform: 'linux', tmpdir: '/tmp' };

/** Every argument reaches the process as it was given: whole, and not quoted or escaped for a shell. */
function unchanged(step, values) {
  for (const value of values) {
    assert.ok(step.args.includes(value) || step.command === value, `${value} is one argument of ${JSON.stringify(step)}`);
  }
  for (const argument of [step.command, ...step.args]) {
    assert.ok(!/^["'].*["']$/.test(argument), `${argument} is not quoted`);
    assert.ok(!argument.includes('\\"'), `${argument} has no escaped quotes`);
  }
}

test('a hostile folder name is one argument of larva build, not a command line', () => {
  const step = larvaBuild(exe, lone, ['--check']);
  assert.deepEqual(step, { command: exe.command, args: ['build', '--check', file] });
  unchanged(step, [file]);
});

test('a framework-dependent larva.dll runs with dotnet, the dll first', () => {
  const step = larvaBuild(dll, lone, []);
  assert.deepEqual(step.args, [dll.args[0], 'build', file]);
});

test('run of a lone file builds into the temporary folder, then runs the executable', () => {
  const steps = buildSteps('run', exe, lone, windows);
  const output = path.join('C:\\Temp', 'larva-run', 'Program.exe');
  assert.deepEqual(steps, [
    { command: exe.command, args: ['build', '-o', output, file] },
    { command: output, args: [] },
  ]);
  unchanged(steps[0], [file]);
  unchanged(steps[1], [output]);
  assert.equal(buildSteps('run', exe, lone, posix)[1].command, path.join('/tmp', 'larva-run', 'Program'));
});

test('run of a project is larva run with the .queen file', () => {
  assert.deepEqual(buildSteps('run', exe, project, windows), [{ command: exe.command, args: ['run', project.files[0]] }]);
});

test('exe and wasm name the output next to the source; a project builds where its .queen says', () => {
  assert.deepEqual(buildSteps('exe', exe, lone, windows)[0].args, ['build', '-o', path.join(hostile, 'Program.exe'), file]);
  assert.deepEqual(buildSteps('exe', exe, lone, posix)[0].args, ['build', '-o', path.join(hostile, 'Program'), file]);
  assert.deepEqual(buildSteps('exe', exe, project, windows)[0].args, ['build', project.files[0]]);
  assert.deepEqual(buildSteps('wasm', exe, lone, windows)[0].args, ['build', '--target', 'wasm-llvm', '-o', path.join(hostile, 'Program.js'), file]);
  assert.deepEqual(buildSteps('check', exe, lone, windows)[0].args, ['build', '--check', file]);
  assert.throws(() => buildSteps('other', exe, lone, windows), /unknown build kind/);
});

test('bind writes Name.dll, or Name.Bindings.dll when that is the native library’s own name', () => {
  const language = { id: 'lang', extensions: ['.src'] };
  assert.deepEqual(bindOptions(hostile, 'Kernel32', 'kernel32.dll', 'C:\\h\\windows.h', language), [
    '-o', path.join(hostile, 'Kernel32.Bindings.dll'),
    '--source', path.join(hostile, 'Kernel32.src'),
    '--describe', path.join(hostile, 'Kernel32.bind'),
  ]);
  assert.deepEqual(bindOptions(hostile, 'Sdl', 'SDL3', 'C:\\h\\sdl.bind', undefined), ['-o', path.join(hostile, 'Sdl.dll')]);
  assert.deepEqual(bindOptions(hostile, 'C', '/lib/libc.so.6', 'libc.bind', { id: 'l', extensions: [] }), ['-o', path.join(hostile, 'C.dll')]);
});

test('bind, bind --list and bind --profile pass the library and files as arguments', () => {
  const library = 'C:\\libs\\a;b.dll';
  assert.deepEqual(bindStep(dll, library), { command: 'dotnet', args: [dll.args[0], 'bind', '--list', library] });
  const step = bindStep(exe, library, 'x.h', ['-o', 'X.dll']);
  assert.deepEqual(step.args, ['bind', library, 'x.h', '-o', 'X.dll']);
  unchanged(step, [library]);
  assert.deepEqual(bindProfileStep(exe, file).args, ['bind', '--profile', file]);
});
