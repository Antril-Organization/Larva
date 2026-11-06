// The old tests are full of statics, and the native runtime is process-global (NativeRuntime.Reset): one test at a time.
// (xUnit 4 makes CollectionBehavior(DisableTestParallelization = true) an error; this is its replacement.)
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
