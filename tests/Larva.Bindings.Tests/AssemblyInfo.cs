// The tests run larva and clang as processes and read big headers: one test at a time, as the old runner did.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
