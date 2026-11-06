// The native runtime is process-global (NativeRuntime.Reset), and the speed checks want the machine to themselves: one test at a time.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
