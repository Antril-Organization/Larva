namespace Larva.Runtime
{
    /// <summary>Misuse of the reflection API: trimmed metadata, wrong argument types or counts, unknown names.</summary>
    public class ReflectionException(string message) : Exception(message);

    /// <summary>A dynamic borrow that conflicts with one still alive (the run-time counterpart of the borrow checker).</summary>
    public sealed class BorrowConflictException(string message) : ReflectionException(message);

    /// <summary>A type argument that does not satisfy the generic parameter's constraint.</summary>
    public sealed class UnsatisfiedConstraintException(string message) : ReflectionException(message);

    /// <summary>
    /// A generic instantiation the AOT image cannot provide: the method needs specialization and this instance was not
    /// compiled. Use it somewhere statically (or make the method shareable) to have it available.
    /// </summary>
    public sealed class AotInstantiationException(string message) : ReflectionException(message);
}
