// The tests run larva processes over shared temporary folders and settings: one test at a time.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
