namespace Larva.Hir.Lowering
{
    using Larva.IR;
    using H = Larva.Hir;
    using IR = Larva.IR;

    /// <summary>
    /// Equality, hash and text for any type: <c>System.Records.Same</c>, <c>HashOf</c> and <c>TextOf</c> ask for
    /// <c>IHashable.Equals</c>, <c>IHashable.Hash</c> and <c>IToString.ToString</c> on their type parameter, and a compiler
    /// calls them for a field of any type (a generic record's, a tuple's). A type that implements the interface answers
    /// directly once generics are resolved; for the others this registers the last resorts:
    /// <list type="bullet">
    /// <item>an object (a handle): <c>Records.SameObject</c>, <c>ObjectHash</c>, <c>ObjectText</c> (the same object, any's
    /// virtual <c>Hash()</c> and <c>ToString()</c>);</item>
    /// <item>a nullable object: null against null, else its object's answer;</item>
    /// <item>anything else: <c>Records.NoSame</c>, <c>NoHash</c>, <c>NoText</c>.</item>
    /// </list>
    /// </summary>
    public sealed partial class HirLowerer
    {
        private void RegisterStructural()
        {
            int records = Assembly.Types.FindIndex(t => t.FullName == "System.Records");
            int hashable = Assembly.Types.FindIndex(t => t.FullName == "System.IHashable");
            int toString = Assembly.Types.FindIndex(t => t.FullName == "System.IToString");
            int text = Assembly.Types.FindIndex(t => t.FullName == "System.String");
            if (records < 0 || hashable < 0 || toString < 0 || text < 0)
                return;
            int Method(int type, string name, int parameters) =>
                Assembly.Types[type].Methods.FindIndex(m => m.Name == name && m.Parameters.Count == parameters);
            int? Function(string name, int parameters) =>
                Method(records, name, parameters) is int m and >= 0 && MethodFunctions.TryGetValue(H.MemberToken.Def(records, m), out int f) ? f : null;
            int equals = Method(hashable, "Equals", 1), hash = Method(hashable, "Hash", 0), show = Method(toString, "ToString", 0);
            if (equals < 0 || hash < 0 || show < 0)
                return;
            var context = new GenericContext(0);
            var str = Lower(new H.NamedType(H.TypeToken.Def(text), [H.HirType.U8]), "System.Records", context);
            var kinds = new (string Requirement, int Arity, IrType Result, string Object, string Value)[]
            {
                (RequirementName(Types[hashable], Assembly.Types[hashable].Methods[equals]), 2, IrType.Bool, "SameObject", "NoSame"),
                (RequirementName(Types[hashable], Assembly.Types[hashable].Methods[hash]), 1, IrType.U64, "ObjectHash", "NoHash"),
                (RequirementName(Types[toString], Assembly.Types[toString].Methods[show]), 1, str, "ObjectText", "NoText"),
            };
            string fallback = Requirements.FallbackSuffix;
            foreach (var (requirement, arity, result, objectName, valueName) in kinds)
            {
                if (Function(objectName, arity) is int objectFunction)
                    Implementations.Add(new Implementation(requirement + fallback, AddFunction(ObjectFallback(requirement, arity, result, objectFunction))));
                if (Function("NullText", 0) is int nullText)
                    Implementations.Add(new Implementation(requirement + fallback, AddFunction(NullableFallback(requirement, arity, result, nullText))));
                if (Function(valueName, arity) is int valueFunction)
                    Implementations.Add(new Implementation(requirement + fallback + fallback, valueFunction));
            }
        }

        /// <summary>An object of any class (<c>gc S</c>): as an <c>any</c>, to <paramref name="function"/> of System.Records.</summary>
        private IrFunction ObjectFallback(string requirement, int arity, IrType result, int function)
        {
            var b = new FunctionBuilder($"{requirement}$object", result) { Unsafe = true, Linkage = Linkage.Internal };
            var handle = Handle(b.Generic("S"));
            var parameters = new int[arity];
            for (int a = 0; a < arity; a++)
                parameters[a] = b.Argument(handle, $"a{a}");
            var arguments = new List<Operand>();
            for (int a = 0; a < arity; a++)
            {
                int root = b.Local(RootHandle, null, mutable: true);
                b.Assign(root, Rvalue.HandleCast(Operand.Copy(parameters[a]), RootHandle));
                arguments.Add(Operand.Copy(root));
            }
            int done = b.NewBlock();
            b.Call(function, [.. arguments], FunctionBuilder.ReturnPlace, done);
            b.At(done).Return();
            return b.Build();
        }

        /// <summary>
        /// A nullable object (<c>?gc S</c>): equal when both are null or both hold equal objects; null hashes to 0 and shows
        /// as "null"; else the requirement for what it holds.
        /// </summary>
        private IrFunction NullableFallback(string requirement, int arity, IrType result, int nullText)
        {
            var b = new FunctionBuilder($"{requirement}$nullable", result) { Unsafe = true, Linkage = Linkage.Internal };
            var t = Handle(b.Generic("S"));
            var nullable = new OptionalType(t);
            var arguments = new int[arity];
            var inner = new int[arity];
            var isNull = new int[arity];
            for (int a = 0; a < arity; a++)
                arguments[a] = b.Argument(nullable, $"a{a}");
            for (int a = 0; a < arity; a++)
            {
                inner[a] = b.Local(t, null, mutable: true);
                isNull[a] = b.Local(IrType.Bool, null, mutable: true);
            }
            int f = b.Local(new FunctionType([.. Enumerable.Repeat(t, arity)], result), null, mutable: true);
            int some = b.NewBlock(), done = b.NewBlock();
            for (int a = 0; a < arity; a++)
                b.Assign(isNull[a], Rvalue.IsNull(new Place(arguments[a])));
            if (arity == 1)
            {
                int none = b.NewBlock();
                b.If(Operand.Copy(isNull[0]), none, some);
                if (result == IrType.U64)
                    b.At(none).Assign(FunctionBuilder.ReturnPlace, Rvalue.Use(Operand.Const(0, IrType.U64))).Return();
                else
                {
                    int shown = b.NewBlock();
                    b.At(none).Call(nullText, [], FunctionBuilder.ReturnPlace, shown);
                    b.At(shown).Return();
                }
            }
            else
            {
                // Both null: equal. One null: not. Neither: compare what they hold.
                int firstNull = b.NewBlock(), firstSome = b.NewBlock(), unequal = b.NewBlock();
                b.If(Operand.Copy(isNull[0]), firstNull, firstSome);
                b.At(firstNull).Assign(FunctionBuilder.ReturnPlace, Rvalue.Use(Operand.Copy(isNull[1]))).Return();
                b.At(firstSome).If(Operand.Copy(isNull[1]), unequal, some);
                b.At(unequal).Assign(FunctionBuilder.ReturnPlace, Rvalue.Use(Operand.Bool(false))).Return();
            }
            b.At(some);
            for (int a = 0; a < arity; a++)
                b.Assign(inner[a], Rvalue.Unwrap(Operand.Copy(arguments[a])));
            b.Assign(f, Rvalue.Implementation(new Requirement(requirement, [.. Enumerable.Repeat(t, arity)], result)))
             .CallIndirect(Operand.Copy(f), [.. inner.Select(i => Operand.Copy(i))], FunctionBuilder.ReturnPlace, done);
            b.At(done).Return();
            return b.Build();
        }
    }
}
