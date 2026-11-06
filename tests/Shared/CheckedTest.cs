using Xunit;

namespace Larva.Testing;

/// <summary>
/// A test made of many named checks over one setup (a module compiled once, then looked at in many ways): each check that
/// fails is recorded, the rest still run, and the test fails at its end with every failed check listed.
/// xUnit makes a new instance of the class for each test and disposes it afterwards.
/// </summary>
public abstract class CheckedTest : IDisposable
{
    private readonly List<string> failures = [];
    private int count;

    /// <summary>How many checks ran in this test.</summary>
    protected int CheckCount => count;

    /// <summary>One check: <paramref name="name"/> says what must hold, <paramref name="detail"/> what was found instead.</summary>
    protected void Check(string name, bool pass, string? detail = null)
    {
        count++;
        if (!pass)
            failures.Add(detail is null ? name : $"{name}  - {detail}");
    }

    /// <summary>Fails the test now with the checks that failed so far (when a later step needs the earlier ones).</summary>
    protected void Require()
    {
        if (failures.Count > 0)
            Assert.Fail(Report());
    }

    private string Report() => $"{failures.Count} of {count} check(s) failed:\n  " + string.Join("\n  ", failures);

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
        if (failures.Count > 0)
            Assert.Fail(Report());
    }
}
