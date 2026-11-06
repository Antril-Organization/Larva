namespace Larva.Hir.Lowering
{
    using Larva.IR;
    using H = Larva.Hir;
    using IR = Larva.IR;

    /// <summary>
    /// Reflection, written as HIR before lowering (so the rest of lowering does it like any other code), only when the
    /// program uses <c>typeof</c> or <c>GetType()</c>. Each type X gets a describer class <c>X.$Describer</c> (nested in X,
    /// with X's type parameters, derived from <c>System.Reflection.TypeDescriber</c>) holding:
    /// <list type="bullet">
    /// <item>a static <c>$type</c> (one per instance of a generic type) and <c>$Type()</c>, which makes X's
    /// <c>System.Type</c> once: name, namespace, kind, and a new describer, which gives the rest on demand (base type,
    /// type arguments, interfaces, members, enum members);</item>
    /// <item>access to X's members by number: <c>GetField</c>, <c>SetField</c>, <c>Invoke</c>, <c>Construct</c>
    /// (values converted to and from <c>any?</c>);</item>
    /// <item><c>$TypeOf(X*)</c>, <c>$Box(X)</c>, <c>$Unbox(any?)</c>: the implementations of <c>$typeof</c>, <c>$box</c> and
    /// <c>$unbox</c> for X, which generic code reaches through IR <c>impl</c> (<c>typeof(T)</c>, a <c>T</c> as
    /// <c>any</c> and back) once T is known. Types made from others (arrays, nullables, pointers, functions) have
    /// generic implementations on <c>System.Type</c>, and last resorts.</item>
    /// </list>
    /// Classes override <c>Object.RuntimeType()</c> (what <c>GetType()</c> calls); a box's comes from its value's type.
    /// Enums and interfaces are values and handles at run time (an <c>i32</c>, an <c>any</c>): generic code sees them as
    /// those, so they have no implementations of their own.
    /// </summary>
    internal sealed partial class BodyLowerer
    {
        /// <summary>
        /// A call through <c>impl</c>: the function that meets <paramref name="requirement"/> for the concrete types, found
        /// when generics are resolved.
        /// </summary>
        private void ImplementationCall(Requirement requirement, List<Operand> arguments, Place target)
        {
            int function = Temp(new FunctionType(requirement.Parameters, requirement.Result));
            b.Assign(function, Rvalue.Implementation(requirement));
            int next = NewBlock();
            b.CallIndirect(Operand.Copy(function), [.. arguments], target, next, Handlers());
            At(next);
        }

        /// <summary><c>typeof(T)</c>: its describer's <c>$Type()</c>; for a type parameter, the one of the type it stands for.</summary>
        private void TypeOf(H.TypeOfExpr typeOf, Place target)
        {
            if (lowerer.Reflection is not { } kit)
                throw new LoweringException("typeof needs System.Type and the reflection classes (System.Core)");
            if (lowerer.TypeOfCall(typeOf.Operand) is { } call)
            {
                Into(target, call);
                return;
            }
            var pointer = IrType.PtrMut(Lower(typeOf.Operand));
            int none = Temp(pointer);
            b.Unsafe = true;
            b.Assign(none, Rvalue.Cast(Operand.Const(0, IrType.U64), pointer));
            ImplementationCall(new Requirement("$typeof", [pointer], Lower(kit.TypeType)), [Operand.Copy(none)], target);
        }

        /// <summary>A value of a type parameter as an <c>any</c> (or <c>any?</c>): boxed the way its type is, once known.</summary>
        private Rvalue GenericBox(Operand value, IrType from, IrType to)
        {
            if (lowerer.Reflection is not { } kit)
                throw new LoweringException("a type parameter's value as an any needs the reflection classes (System.Core)");
            var root = Lower(kit.AnyNullable);
            int boxed = Temp(root);
            ImplementationCall(new Requirement("$box", [from], root), [value], new Place(boxed));
            return Convert(Operand.Copy(boxed), root, to);
        }

        /// <summary>An <c>any</c> (or <c>any?</c>) as a value of a type parameter: unboxed the way its type is, once known (checked).</summary>
        private Rvalue GenericUnbox(Operand value, IrType from, IrType to)
        {
            if (lowerer.Reflection is not { } kit)
                throw new LoweringException("an any as a type parameter's value needs the reflection classes (System.Core)");
            var root = Lower(kit.AnyNullable);
            int handle = Temp(root);
            b.Assign(handle, Convert(value, from, root));
            int unboxed = Temp(to);
            ImplementationCall(new Requirement("$unbox", [root], to), [Operand.Copy(handle)], new Place(unboxed));
            return FieldCopied(to) ? Operand.Move(unboxed) : Operand.Copy(unboxed);
        }
    }

    public sealed partial class HirLowerer
    {
        internal sealed class ReflectionKit
        {
            public int Type, Describer, FieldInfo, PropertyInfo, MethodInfo, ConstructorInfo, ParameterInfo, Fail, Object, String;
            public H.HirType TypeType = null!, NullableType = null!, Any = null!, AnyNullable = null!, Text = null!, DescriberType = null!;
            public H.MemberToken TypeConstructor, FieldConstructor, PropertyConstructor, MethodConstructor, ConstructorConstructor, ParameterConstructor;
            public H.MemberToken Enter, Exit, ArrayOf, PointerOf, NullableOf, FuncOf, VoidType, Unknown;
            public H.MemberToken NotAnObject, BadTarget, BadValue, ValueField, Missing;

            /// <summary>For each described type: its describer class and the describer's <c>$Type()</c>.</summary>
            public readonly Dictionary<int, (int Describer, int TypeMethod)> Described = [];

            /// <summary>Whether the program asks for members (fields, methods, properties, constructors): describers then give them.</summary>
            public bool Members;

            /// <summary>
            /// The types whose describers give members: the program's own, and the library's that it names in a
            /// <c>typeof</c> (each member table reaches the types its members use, so giving every type one would keep the
            /// whole library in every program).
            /// </summary>
            public readonly HashSet<int> WithMembers = [];

            /// <summary>Implementations of <c>$typeof</c>, <c>$box</c>, <c>$unbox</c> (and their last resorts), registered once functions exist.</summary>
            public readonly List<(string Requirement, H.MemberToken Method)> Implementations = [];
        }

        /// <summary>The reflection classes and what was made for the program's types (null: the program does not use reflection).</summary>
        internal ReflectionKit? Reflection;

        private void PrepareReflection()
        {
            int Find(string name) => Assembly.Types.FindIndex(t => t.FullName == name);
            var kit = new ReflectionKit
            {
                Type = Find("System.Type"),
                Describer = Find("System.Reflection.TypeDescriber"),
                FieldInfo = Find("System.Reflection.FieldInfo"),
                PropertyInfo = Find("System.Reflection.PropertyInfo"),
                MethodInfo = Find("System.Reflection.MethodInfo"),
                ConstructorInfo = Find("System.Reflection.ConstructorInfo"),
                ParameterInfo = Find("System.Reflection.ParameterInfo"),
                Fail = Find("System.Reflection.Fail"),
                Object = Find("System.Any"),
                String = Find("System.String"),
            };
            if (new[] { kit.Type, kit.Describer, kit.FieldInfo, kit.PropertyInfo, kit.MethodInfo, kit.ConstructorInfo, kit.ParameterInfo, kit.Fail, kit.Object, kit.String }.Any(t => t < 0))
                return;
            int getType = Assembly.Types[kit.Object].Methods.FindIndex(m => m.Name == "GetType" && m.Parameters.Count == 0);
            var named = new HashSet<int>();
            var (types, members) = UsesReflection(kit, getType >= 0 ? H.MemberToken.Def(kit.Object, getType) : null, named);
            if (!types)
                return;
            kit.Members = members;
            for (int t = 0; t < Assembly.Types.Count; t++)
            {
                string space = Assembly.Types[t].Namespace;
                if (named.Contains(t) || t == kit.Object || space != "System" && !space.StartsWith("System.", StringComparison.Ordinal))
                    kit.WithMembers.Add(t);
            }
            try
            {
                kit.TypeType = new H.NamedType(H.TypeToken.Def(kit.Type), []);
                kit.NullableType = new H.NullableType(kit.TypeType);
                kit.Any = new H.NamedType(H.TypeToken.Def(kit.Object), []);
                kit.AnyNullable = new H.NullableType(kit.Any);
                kit.Text = new H.NamedType(H.TypeToken.Def(kit.String), [H.HirType.U8]);
                kit.DescriberType = new H.NamedType(H.TypeToken.Def(kit.Describer), []);
                kit.TypeConstructor = Constructor(kit.Type, 4);
                kit.FieldConstructor = Constructor(kit.FieldInfo, 6);
                kit.PropertyConstructor = Constructor(kit.PropertyInfo, 8);
                kit.MethodConstructor = Constructor(kit.MethodInfo, 7);
                kit.ConstructorConstructor = Constructor(kit.ConstructorInfo, 5);
                kit.ParameterConstructor = Constructor(kit.ParameterInfo, 3);
                kit.Enter = LibraryMethod(kit.Type, "Enter", 0);
                kit.Exit = LibraryMethod(kit.Type, "Exit", 0);
                kit.ArrayOf = LibraryMethod(kit.Type, "ArrayOf", 2);
                kit.PointerOf = LibraryMethod(kit.Type, "PointerOf", 1);
                kit.NullableOf = LibraryMethod(kit.Type, "NullableOf", 1);
                kit.FuncOf = LibraryMethod(kit.Type, "FuncOf", 3);
                kit.VoidType = LibraryMethod(kit.Type, "VoidType", 0);
                kit.Unknown = LibraryMethod(kit.Type, "Unknown", 0);
                kit.NotAnObject = LibraryMethod(kit.Fail, "NotAnObject", 1);
                kit.BadTarget = LibraryMethod(kit.Fail, "Target", 1);
                kit.BadValue = LibraryMethod(kit.Fail, "Value", 1);
                kit.ValueField = LibraryMethod(kit.Fail, "ValueField", 1);
                kit.Missing = LibraryMethod(kit.Fail, "Missing", 1);
                Reflection = kit;
                if (Assembly.Types.Any(t => (t.Flags & H.TypeFlags.Generated) != 0 && t.Name.EndsWith(".$Describer", StringComparison.Ordinal)))
                {
                    // Lowered before: the describers are there already.
                    Recover(kit);
                    return;
                }
                int count = Assembly.Types.Count;
                StructuralImplementations(kit);
                for (int t = 0; t < count; t++)
                {
                    if ((Assembly.Types[t].Flags & H.TypeFlags.Generated) == 0)
                        new Describing(this, kit, t).Run();
                }
            }
            catch (LoweringException e)
            {
                Error("reflection", e.Message);
            }
        }

        /// <summary>The requirement each generated method meets, by name (its describer's or System.Type's).</summary>
        private static string? RequirementOf(string method) => method switch
        {
            "$TypeOf" or "$NullableTypeOf" or "$PointerTypeOf" or "$VoidTypeOf" => "$typeof",
            _ when method.StartsWith("$ArrayTypeOf", StringComparison.Ordinal) || method.StartsWith("$FuncTypeOf", StringComparison.Ordinal) => "$typeof",
            "$UnknownTypeOf" => "$typeof" + Requirements.FallbackSuffix,
            "$Box" or "$BoxNullable" => "$box",
            "$BoxUnknown" => "$box" + Requirements.FallbackSuffix,
            "$Unbox" or "$UnboxNullable" => "$unbox",
            "$UnboxUnknown" => "$unbox" + Requirements.FallbackSuffix,
            _ => null,
        };

        /// <summary>An assembly lowered before: what reflection made is found again instead of made twice.</summary>
        private void Recover(ReflectionKit kit)
        {
            for (int t = 0; t < Assembly.Types.Count; t++)
            {
                var type = Assembly.Types[t];
                bool isDescriber = (type.Flags & H.TypeFlags.Generated) != 0 && type.Name.EndsWith(".$Describer", StringComparison.Ordinal);
                if (!isDescriber && t != kit.Type)
                    continue;
                for (int m = 0; m < type.Methods.Count; m++)
                {
                    var method = type.Methods[m];
                    if ((method.Flags & H.MethodFlags.Generated) == 0)
                        continue;
                    if (isDescriber && method.Name == "$Type")
                        kit.Described[type.DeclaringType] = (t, m);
                    if (t == kit.Type && method.Name.StartsWith('$'))
                        structural[method.Name] = H.MemberToken.Def(t, m);
                    if (RequirementOf(method.Name) is { } requirement)
                        kit.Implementations.Add((requirement, H.MemberToken.Def(t, m)));
                }
            }
        }

        private H.MemberToken Constructor(int type, int parameters)
        {
            int m = Assembly.Types[type].Methods.FindIndex(x => x.Kind == H.MethodKind.Constructor && x.Parameters.Count == parameters);
            return m >= 0 ? H.MemberToken.Def(type, m) : throw new LoweringException($"{Assembly.Types[type].FullName} has no constructor taking {parameters} values");
        }

        private H.MemberToken LibraryMethod(int type, string name, int parameters)
        {
            int m = Assembly.Types[type].Methods.FindIndex(x => x.Name == name && x.Parameters.Count == parameters);
            return m >= 0 ? H.MemberToken.Def(type, m) : throw new LoweringException($"{Assembly.Types[type].FullName} has no {name} taking {parameters} values");
        }

        /// <summary>The methods of System.Type that ask for members (and Activator's): a program that calls none needs no member tables.</summary>
        private static readonly HashSet<string> MemberQueries =
        [
            "GetFields", "GetField", "GetProperties", "GetProperty", "GetMethods", "GetMethod", "GetConstructors", "GetConstructor",
        ];

        /// <summary>
        /// Whether some body (outside the reflection library itself) says <c>typeof</c> or calls <c>Object.GetType()</c>, and
        /// whether one asks for members.
        /// </summary>
        private (bool Types, bool Members) UsesReflection(ReflectionKit kit, H.MemberToken? getType, HashSet<int> named)
        {
            int activator = Assembly.Types.FindIndex(t => t.FullName == "System.Activator");
            var walker = new ReflectionUse(Assembly, kit.Type, activator, getType, named);
            for (int t = 0; t < Assembly.Types.Count; t++)
            {
                var type = Assembly.Types[t];
                if (type.Name.EndsWith(".$Describer", StringComparison.Ordinal) || t == kit.Type || t == activator || type.Namespace == "System.Reflection")
                    continue;
                foreach (var method in type.Methods)
                {
                    if (method.Body is { } body)
                        walker.Visit(body.Block);
                }
            }
            return (walker.Types, walker.Members);
        }

        private sealed class ReflectionUse(H.HirAssembly assembly, int typeClass, int activator, H.MemberToken? getType, HashSet<int> named) : H.HirWalker
        {
            public bool Types, Members;

            private void Name(H.HirType type)
            {
                switch (type)
                {
                    case H.NamedType { Type.IsDefinition: true } n:
                        named.Add(n.Type.Index); // not its type arguments: `typeof(Box<str>)` asks about Box
                        break;
                    case H.ArrayType a:
                        Name(a.Element);
                        break;
                    case H.NullableType n:
                        Name(n.Inner);
                        break;
                    case H.PrimitiveType p:
                        int provider = assembly.Types.FindIndex(t => (t.Flags & H.TypeFlags.Primitive) != 0 && t.Underlying is H.PrimitiveType { Kind: var k } && k == p.Kind);
                        if (provider >= 0)
                            named.Add(provider);
                        break;
                }
            }

            public override void Visit(H.Expr? e)
            {
                if (e is null)
                    return;
                if (e is H.TypeOfExpr typeOf)
                {
                    Types = true;
                    Name(typeOf.Operand);
                }
                if (getType is { } g && e is H.CallExpr { Method: var called } && called == g)
                    Types = true;
                if (e is H.CallExpr { Method: { IsDefinition: true } method }
                    && (method.Type == activator || method.Type == typeClass && MemberQueries.Contains(assembly.Types[typeClass].Methods[method.Member].Name)))
                {
                    Types = true;
                    Members = true;
                }
                base.Visit(e);
            }
        }

        // ── HIR building ───────────────────────────────────

        private static H.Expr Int(long value) => new H.LiteralExpr(new H.IntegerConstant(value, H.Primitive.I32), H.HirType.I32);

        private static H.BlockStmt Block(params H.Stmt[] statements) => new(statements);

        private static H.Stmt Return(H.Expr? value) => new H.ReturnStmt(value);

        private static H.Stmt Exec(H.Expr value) => new H.ExprStmt(value);

        private static H.Expr Call(H.MemberToken method, H.HirType result, params H.Expr[] arguments) => new H.CallExpr(null, method, [], arguments, result);

        private static H.Expr Null(H.HirType type) => new H.LiteralExpr(new H.NullConstant(), type);

        private static H.Expr IsNull(H.Expr value) => new H.BinaryExpr(H.BinaryOp.Equal, value, Null(value.Type), H.HirType.Bool);

        private static H.Expr Convert(H.Expr value, H.ConversionKind kind, H.HirType type) => new H.ConvertExpr(value, kind, type);

        /// <summary>A method with a body made here.</summary>
        private static int AddMethod(H.TypeDef owner, string name, H.HirType result, H.Parameter[] parameters, H.Stmt body, H.MethodFlags flags,
            H.MethodKind kind = H.MethodKind.Normal, int generics = 0)
        {
            var method = new H.MethodDef(name, result, parameters)
            {
                Kind = kind,
                Flags = flags | H.MethodFlags.Generated,
                Access = H.Access.Public,
                Body = new H.HirBody([], body is H.BlockStmt block ? block : Block(body)),
            };
            for (int g = 0; g < generics; g++)
                method.Generics.Add(new H.GenericParam($"T{g}"));
            return owner.AddMethod(method);
        }

        /// <summary>
        /// The <c>System.Type</c> of <paramref name="type"/> as an expression (a call of its describer's <c>$Type()</c>, or
        /// of the library's for types made from others); null for a bare type parameter (found through <c>impl</c>).
        /// </summary>
        internal H.Expr? TypeOfCall(H.HirType type)
        {
            var kit = Reflection!;
            H.Expr Of(H.HirType t) => new H.TypeOfExpr(t, kit.TypeType);
            switch (type)
            {
                case H.NamedType { Type.IsDefinition: true } named when kit.Described.TryGetValue(named.Type.Index, out var described):
                    return new H.CallExpr(null, H.MemberToken.Def(described.Describer, described.TypeMethod), named.Arguments, [], kit.TypeType);
                case H.PrimitiveType { Kind: H.Primitive.Void }:
                    return Call(kit.VoidType, kit.TypeType);
                case H.PrimitiveType primitive when ValueInfo(primitive) is { } provider && kit.Described.TryGetValue(provider.Index, out var described):
                    return new H.CallExpr(null, H.MemberToken.Def(described.Describer, described.TypeMethod), [], [], kit.TypeType);
                case H.ArrayType array:
                    return Call(kit.ArrayOf, kit.TypeType, Of(array.Element), Int(array.Rank));
                case H.InlineArrayType inline:
                    return Call(kit.ArrayOf, kit.TypeType, Of(inline.Element), Int(1));
                case H.NullableType { Inner: var inner } when inner is H.PrimitiveType or H.GenericParamType
                    || inner is H.NamedType { Type.IsDefinition: true } n && Types[n.Type.Index] is { IsStruct: true } or { IsEnum: true } or { IsPrimitive: true }:
                    return Call(kit.NullableOf, kit.TypeType, Of(inner));
                case H.NullableType { Inner: var inner }:
                    return Of(inner);
                case H.PointerType pointer:
                    return Call(kit.PointerOf, kit.TypeType, Of(pointer.Target));
                case H.ReferenceType reference:
                    return Of(reference.Target);
                case H.FuncType func:
                    return Call(kit.FuncOf, kit.TypeType, new H.ArrayLiteralExpr(kit.TypeType, [.. func.Parameters.Select(Of)], new H.ArrayType(kit.TypeType)),
                        Of(func.Result), new H.LiteralExpr(new H.BoolConstant(false), H.HirType.Bool));
                case H.NativeFuncType native:
                    return Call(kit.FuncOf, kit.TypeType, new H.ArrayLiteralExpr(kit.TypeType, [.. native.Parameters.Select(Of)], new H.ArrayType(kit.TypeType)),
                        Of(native.Result), new H.LiteralExpr(new H.BoolConstant(true), H.HirType.Bool));
                case H.GenericParamType:
                    return null;
            }
            return Call(kit.Unknown, kit.TypeType);
        }

        /// <summary>What each type's describer is made of (see the class).</summary>
        private sealed class Describing(HirLowerer lowerer, ReflectionKit kit, int index)
        {
            private readonly H.TypeDef def = lowerer.Assembly.Types[index];
            private H.HirType[] own = [];
            private H.HirType self = null!;
            private H.TypeDef describer = null!;
            private int describerIndex;
            private int selfMethod = -1;
            private bool IsPrimitive => (def.Flags & H.TypeFlags.Primitive) != 0;
            private bool IsClass => def.Kind == H.TypeKind.Class;
            private bool IsStruct => def.Kind == H.TypeKind.Struct && !IsPrimitive;
            private bool IsEnum => def.Kind is H.TypeKind.Enum or H.TypeKind.Bitflags;
            private bool IsInterface => def.Kind == H.TypeKind.Interface;
            private bool IsAbstract => (def.Flags & (H.TypeFlags.Abstract | H.TypeFlags.Static)) != 0;
            private string Shown => def.FullName;

            public void Run()
            {
                own = [.. Enumerable.Range(0, def.Generics.Count).Select(i => (H.HirType)new H.GenericParamType(H.GenericOwner.Type, i))];
                self = IsPrimitive ? def.Underlying! : new H.NamedType(H.TypeToken.Def(index), own);
                describer = new H.TypeDef
                {
                    Namespace = def.Namespace,
                    Name = def.Name + ".$Describer",
                    Kind = H.TypeKind.Class,
                    Flags = H.TypeFlags.Final | H.TypeFlags.Generated,
                    Access = H.Access.Internal,
                    DeclaringType = index,
                    Base = kit.DescriberType,
                };
                describer.Generics.AddRange(def.Generics);
                describerIndex = lowerer.Assembly.Add(describer).Index;
                var describerSelf = new H.NamedType(H.TypeToken.Def(describerIndex), own);
                int constructor = AddMethod(describer, ".new", H.HirType.Void, [], Block(), H.MethodFlags.None, H.MethodKind.Constructor);

                // $Type(): the type's Type, made once.
                int cache = describer.AddField(new H.FieldDef("$type", kit.NullableType) { Flags = H.FieldFlags.Static | H.FieldFlags.Generated, Access = H.Access.Private });
                var cached = new H.FieldExpr(null, H.MemberToken.Def(describerIndex, cache), kit.NullableType);
                int dot = def.Name.LastIndexOf('.');
                string name = dot >= 0 ? def.Name[(dot + 1)..] : def.Name;
                string space = dot >= 0 ? (def.Namespace.Length > 0 ? def.Namespace + "." : "") + def.Name[..dot] : def.Namespace;
                var made = new H.NewExpr(kit.TypeConstructor,
                    [Text(name), Text(space), Int(Flags()), Convert(new H.NewExpr(H.MemberToken.Def(describerIndex, constructor), [], [], describerSelf), H.ConversionKind.Upcast, kit.DescriberType)],
                    [], kit.TypeType);
                // The first time (out of line: typeof is the cache's load and test where it is used).
                int make = AddMethod(describer, "$Make", kit.TypeType, [], Block(
                    Exec(Call(kit.Enter, H.HirType.Void)),
                    new H.IfStmt(IsNull(cached), Exec(new H.AssignExpr(cached, Convert(made, H.ConversionKind.Wrap, kit.NullableType), kit.NullableType)), null),
                    Exec(Call(kit.Exit, H.HirType.Void)),
                    Return(Convert(cached, H.ConversionKind.Unwrap, kit.TypeType))), H.MethodFlags.Static | H.MethodFlags.NoInline | H.MethodFlags.Cold);
                int typeMethod = AddMethod(describer, "$Type", kit.TypeType, [], Block(
                    new H.IfStmt(IsNull(cached), Return(new H.CallExpr(null, H.MemberToken.Def(describerIndex, make), own, [], kit.TypeType)), null),
                    Return(Convert(cached, H.ConversionKind.Unwrap, kit.TypeType))), H.MethodFlags.Static);
                kit.Described[index] = (describerIndex, typeMethod);

                // What generic code finds through `impl`.
                if (!IsEnum && !IsInterface)
                {
                    int typeOf = AddMethod(describer, "$TypeOf", kit.TypeType, [new H.Parameter("p", new H.PointerType(self))], Return(TypeOfSelf()), H.MethodFlags.Static);
                    int box = AddMethod(describer, "$Box", kit.AnyNullable, [new H.Parameter("value", self)], Return(ToAny(new H.ParamExpr(0, self))), H.MethodFlags.Static);
                    int unbox = AddMethod(describer, "$Unbox", self, [new H.Parameter("value", kit.AnyNullable)],
                        Return(FromAny(new H.ParamExpr(0, kit.AnyNullable), self, kit.BadValue)), H.MethodFlags.Static);
                    kit.Implementations.Add(("$typeof", H.MemberToken.Def(describerIndex, typeOf)));
                    kit.Implementations.Add(("$box", H.MemberToken.Def(describerIndex, box)));
                    kit.Implementations.Add(("$unbox", H.MemberToken.Def(describerIndex, unbox)));
                }

                // GetType(): each class's own.
                if (IsClass && (def.Flags & H.TypeFlags.Static) == 0)
                {
                    var body = Return(Convert(TypeOfSelf(), H.ConversionKind.Wrap, kit.NullableType));
                    if (index == kit.Object)
                    {
                        int existing = def.Methods.FindIndex(m => m.Name == "RuntimeType" && m.Parameters.Count == 0);
                        if (existing >= 0)
                            def.Methods[existing].Body = new H.HirBody([], Block(body));
                    }
                    else
                    {
                        AddMethod(def, "RuntimeType", kit.NullableType, [], body, H.MethodFlags.Virtual | H.MethodFlags.Override);
                    }
                }

                Overrides();
                AttributeTable();
            }

            /// <summary>
            /// <c>Attributes(member)</c>: new objects of the attributes written on the type (-1) and, when the type gives
            /// members, on its fields (1000000 + index), methods and constructors (2000000 + index) and properties (3000000 + index).
            /// </summary>
            private void AttributeTable()
            {
                var sections = new List<H.SwitchSection>();
                if (def.Attributes.Count > 0)
                    sections.Add(Section(-1, Return(Objects(def.Attributes))));
                if (kit.Members && kit.WithMembers.Contains(index))
                {
                    for (int f = 0; f < def.Fields.Count; f++)
                    {
                        if (def.Fields[f].Attributes.Length > 0)
                            sections.Add(Section(1000000 + f, Return(Objects(def.Fields[f].Attributes))));
                    }
                    for (int m = 0; m < def.Methods.Count; m++)
                    {
                        if (def.Methods[m].Attributes.Count > 0)
                            sections.Add(Section(2000000 + m, Return(Objects(def.Methods[m].Attributes))));
                        // A parameter's: 4000000 + method × 256 + position (ParameterInfo asks with that).
                        var parameters = def.Methods[m].Parameters;
                        for (int p = 0; p < parameters.Count && p < 256; p++)
                        {
                            if (parameters[p].Attributes.Length > 0)
                                sections.Add(Section(4000000 + m * 256 + p, Return(Objects(parameters[p].Attributes))));
                        }
                    }
                    for (int p = 0; p < def.Properties.Count; p++)
                    {
                        if (def.Properties[p].Attributes.Length > 0)
                            sections.Add(Section(3000000 + p, Return(Objects(def.Properties[p].Attributes))));
                    }
                }
                if (sections.Count == 0)
                    return;
                var anyArray = new H.ArrayType(kit.Any);
                Override("Attributes", anyArray, [new H.Parameter("member", H.HirType.I32)],
                    Block(new H.SwitchStmt(new H.ParamExpr(0, H.HirType.I32), [.. sections]), Return(Array(kit.Any, []))));
            }

            /// <summary>The attributes as new objects (each call makes new ones).</summary>
            private H.Expr Objects(IEnumerable<H.HirAttribute> attributes) => Array(kit.Any, attributes.Select(attribute =>
            {
                var constructor = lowerer.Assembly.Method(attribute.Constructor);
                var type = new H.NamedType(H.TypeToken.Def(attribute.Constructor.Type), []);
                var arguments = attribute.Arguments.Select((value, i) => ConstantValue(value, constructor.Parameters[i].Type)).ToArray();
                var initializers = new List<H.MemberInit>();
                foreach (var (name, value) in attribute.Named)
                {
                    for (int t = attribute.Constructor.Type; t >= 0;)
                    {
                        var typeDef = lowerer.Assembly.Types[t];
                        int f = typeDef.Fields.FindIndex(x => x.Name == name);
                        if (f >= 0)
                        {
                            initializers.Add(new H.MemberInit(H.MemberToken.Def(t, f), ConstantValue(value, typeDef.Fields[f].Type)));
                            break;
                        }
                        int p = typeDef.Properties.FindIndex(x => x.Name == name);
                        if (p >= 0)
                        {
                            initializers.Add(new H.MemberInit(H.MemberToken.Def(t, p), ConstantValue(value, typeDef.Properties[p].Type), H.MemberKind.Property));
                            break;
                        }
                        t = typeDef.Base is H.NamedType { Type.IsDefinition: true } b ? b.Type.Index : -1;
                    }
                }
                return Convert(new H.NewExpr(attribute.Constructor, arguments, [.. initializers], type), H.ConversionKind.Upcast, kit.Any);
            }));

            private H.Expr ConstantValue(H.HirConstant value, H.HirType type) => value switch
            {
                H.TypeConstant t => type is H.NullableType ? Convert(TypeOf(t.Type), H.ConversionKind.Wrap, type) : TypeOf(t.Type),
                H.NullConstant => Null(type),
                H.StringConstant s => type is H.NullableType ? Convert(Text(s.Value), H.ConversionKind.Wrap, type) : Text(s.Value),
                _ => new H.LiteralExpr(value, type is H.NullableType { Inner: var inner } ? inner : type) is var literal && type is H.NullableType
                    ? Convert(literal, H.ConversionKind.Wrap, type) : new H.LiteralExpr(value, type),
            };

            private H.Expr Text(string text) => new H.LiteralExpr(new H.StringConstant(text), kit.Text);

            /// <summary>A member's type: a method's own type parameters are not known outside the method (the unknown type).</summary>
            private H.Expr TypeOf(H.HirType type) => MentionsMethodParameter(type) ? Call(kit.Unknown, kit.TypeType) : new H.TypeOfExpr(type, kit.TypeType);

            private static bool MentionsMethodParameter(H.HirType type) => type switch
            {
                H.GenericParamType { Owner: H.GenericOwner.Method } => true,
                H.NamedType n => n.Arguments.Any(MentionsMethodParameter),
                H.ArrayType a => MentionsMethodParameter(a.Element),
                H.InlineArrayType i => MentionsMethodParameter(i.Element) || i.SizeOf is not null && MentionsMethodParameter(i.SizeOf),
                H.NullableType n => MentionsMethodParameter(n.Inner),
                H.ReferenceType r => MentionsMethodParameter(r.Target),
                H.PointerType p => MentionsMethodParameter(p.Target),
                H.FuncType f => f.Parameters.Any(MentionsMethodParameter) || MentionsMethodParameter(f.Result),
                H.NativeFuncType f => f.Parameters.Any(MentionsMethodParameter) || MentionsMethodParameter(f.Result),
                H.PackExpansionType p => true,
                _ => false,
            };

            private H.Expr TypeOfSelf() => new H.CallExpr(null, H.MemberToken.Def(describerIndex, kit.Described[index].TypeMethod), own, [], kit.TypeType);

            /// <summary>The flags System.Type reads (see Reflection.ant).</summary>
            private int Flags()
            {
                int flags = def.Kind switch
                {
                    H.TypeKind.Class => 1,
                    H.TypeKind.Interface => 4,
                    H.TypeKind.Enum => 2 | 8,
                    H.TypeKind.Bitflags => 2 | 8 | 16384,
                    _ => 2,
                };
                if (IsPrimitive)
                    flags |= 16;
                if (def.Generics.Count > 0)
                    flags |= 64;
                if ((def.Flags & H.TypeFlags.Abstract) != 0 && IsClass)
                    flags |= 128;
                if ((def.Flags & H.TypeFlags.Final) != 0 || !IsClass)
                    flags |= 256;
                if ((def.Flags & H.TypeFlags.Static) != 0)
                    flags |= 4096 | 128 | 256;
                if (def.Access == H.Access.Public)
                    flags |= 8192;
                return flags;
            }

            private bool IsReference(H.HirType type) => type is H.NamedType { Type.IsDefinition: true } n
                && lowerer.Assembly.Types[n.Type.Index].Kind is H.TypeKind.Class or H.TypeKind.Interface;

            private bool IsValue(H.HirType type) => type is H.PrimitiveType { Kind: not H.Primitive.Void }
                || type is H.NamedType { Type.IsDefinition: true } n && lowerer.Assembly.Types[n.Type.Index].Kind is H.TypeKind.Struct or H.TypeKind.Enum or H.TypeKind.Bitflags;

            /// <summary>A value as an <c>any?</c> (boxed when it is a value; what is not an object fails when reached).</summary>
            private H.Expr ToAny(H.Expr value)
            {
                var type = value.Type;
                if (type == kit.AnyNullable)
                    return value;
                switch (type)
                {
                    case H.NullableType { Inner: var inner } when IsReference(inner):
                        return Convert(value, H.ConversionKind.Upcast, kit.AnyNullable);
                    case H.NullableType { Inner: var inner } when IsValue(inner) || inner is H.GenericParamType:
                        return new H.CallExpr(null, lowerer.structural["$BoxNullable"], [inner], [value], kit.AnyNullable);
                    case var _ when type == kit.Any:
                        return Convert(value, H.ConversionKind.Wrap, kit.AnyNullable);
                    case var _ when IsReference(type):
                        return Convert(value, H.ConversionKind.Upcast, kit.AnyNullable);
                    case var _ when IsValue(type):
                        return Convert(Convert(value, H.ConversionKind.Box, kit.Any), H.ConversionKind.Wrap, kit.AnyNullable);
                    case H.GenericParamType:
                        return Convert(value, H.ConversionKind.Box, kit.AnyNullable);
                }
                return new H.BlockExpr([Exec(value)], Call(kit.NotAnObject, kit.AnyNullable, Text(H.Text.HirPrinter.Print(type, lowerer.Assembly))), kit.AnyNullable);
            }

            /// <summary>An <c>any?</c> (a parameter: read more than once) as a value of <paramref name="type"/>; another type fails.</summary>
            private H.Expr FromAny(H.Expr value, H.HirType type, H.MemberToken fail)
            {
                H.Expr Failed() => new H.BlockExpr([Exec(Call(fail, kit.Any, Text(H.Text.HirPrinter.Print(type, lowerer.Assembly))))], new H.DefaultExpr(type), type);
                H.Expr Test(H.HirType t) => new H.IsExpr(value, new H.TypePattern(t), H.HirType.Bool);
                H.Expr Unwrapped() => Convert(value, H.ConversionKind.Unwrap, kit.Any);
                if (type == kit.AnyNullable)
                    return value;
                switch (type)
                {
                    case var _ when type == kit.Any:
                        return new H.ConditionalExpr(IsNull(value), Failed(), Unwrapped(), type);
                    case H.NullableType { Inner: var inner } when inner == kit.Any:
                        return value;
                    case H.NullableType { Inner: var inner } when IsReference(inner):
                        return new H.ConditionalExpr(IsNull(value), Null(type),
                            new H.ConditionalExpr(Test(inner), Convert(Convert(Unwrapped(), H.ConversionKind.Downcast, inner), H.ConversionKind.Wrap, type), Failed(), type), type);
                    case H.NullableType { Inner: var inner } when IsValue(inner) || inner is H.GenericParamType:
                        return new H.CallExpr(null, lowerer.structural["$UnboxNullable"], [inner], [value], type);
                    case var _ when IsReference(type):
                        return new H.ConditionalExpr(Test(type), Convert(Unwrapped(), H.ConversionKind.Downcast, type), Failed(), type);
                    case var _ when IsValue(type):
                        return new H.ConditionalExpr(Test(type), Convert(Unwrapped(), H.ConversionKind.Unbox, type), Failed(), type);
                    case H.GenericParamType:
                        return Convert(value, H.ConversionKind.Unbox, type);
                }
                return Failed();
            }

            /// <summary>The target of an instance member: X itself (an object of X, or a boxed X).</summary>
            private H.Expr Target(H.Expr target)
            {
                if (selfMethod < 0)
                {
                    selfMethod = AddMethod(describer, "$Self", self, [new H.Parameter("target", kit.AnyNullable)],
                        Return(FromAny(new H.ParamExpr(0, kit.AnyNullable), self, kit.BadTarget)), H.MethodFlags.Static);
                }
                return new H.CallExpr(null, H.MemberToken.Def(describerIndex, selfMethod), own, [target], self);
            }

            private void Override(string name, H.HirType result, H.Parameter[] parameters, H.Stmt body) =>
                AddMethod(describer, name, result, parameters, body, H.MethodFlags.Virtual | H.MethodFlags.Override
                    | (name is "GetField" or "SetField" or "Invoke" or "Construct" ? 0 : H.MethodFlags.Cold));

            private static bool Shows(string name) => !name.StartsWith('$') && !name.StartsWith('.');

            /// <summary>Whether values of the type can be passed through reflection (as an <c>any?</c>, or failing at run time).</summary>
            private static bool Passable(H.HirType type) => type is not (H.ReferenceType or H.PackExpansionType or H.InlineArrayType);

            private H.Expr Array(H.HirType element, IEnumerable<H.Expr> items) => new H.ArrayLiteralExpr(element, [.. items], new H.ArrayType(element));

            private H.Expr Parameters(IReadOnlyList<H.Parameter> parameters)
            {
                var infoType = new H.NamedType(H.TypeToken.Def(kit.ParameterInfo), []);
                return Array(infoType, parameters.Select((p, i) => (H.Expr)new H.NewExpr(kit.ParameterConstructor, [Text(p.Name), TypeOf(p.Type), Int(i)], [], infoType)));
            }

            private static int Access(H.Access access) => access == H.Access.Public ? 1 : 0;

            private void Overrides()
            {
                var owner = new H.Parameter("owner", kit.TypeType);
                var ownerExpr = new H.ParamExpr(0, kit.TypeType);
                var describerAsBase = Convert(new H.ThisExpr(new H.NamedType(H.TypeToken.Def(describerIndex), own)), H.ConversionKind.Upcast, kit.DescriberType);

                // Base, type arguments, interfaces.
                if (IsClass && index != kit.Object)
                    Override("Base", kit.NullableType, [], Return(Convert(def.Base is { } b ? TypeOf(b) : TypeOf(kit.Any), H.ConversionKind.Wrap, kit.NullableType)));
                if (own.Length > 0)
                    Override("Arguments", new H.ArrayType(kit.TypeType), [], Return(Array(kit.TypeType, own.Select(TypeOf))));
                if (def.Interfaces.Count > 0)
                    Override("Interfaces", new H.ArrayType(kit.TypeType), [], Return(Array(kit.TypeType, def.Interfaces.Select(TypeOf))));

                // Enum members.
                if (IsEnum)
                {
                    var members = def.Fields.Where(f => f.Value is H.EnumConstant).ToList();
                    Override("EnumNames", new H.ArrayType(kit.Text), [], Return(Array(kit.Text, members.Select(f => Text(f.Name)))));
                    var i64 = H.HirType.I64;
                    Override("EnumValues", new H.ArrayType(i64), [], Return(Array(i64, members.Select(f =>
                        (H.Expr)new H.LiteralExpr(new H.IntegerConstant(((H.EnumConstant)f.Value!).Value, H.Primitive.I64), i64)))));
                    // Its members are also its fields (constants, with their attributes); it has no other members to give.
                }

                if (!kit.Members || !kit.WithMembers.Contains(index))
                    return;
                var ownerAndDescriber = new[] { new H.Parameter("owner", kit.TypeType), new H.Parameter("describer", kit.DescriberType) };
                var ownerParam = new H.ParamExpr(0, kit.TypeType);
                var describerParam = new H.ParamExpr(1, kit.DescriberType);
                H.Expr[] ListArguments() => [ownerExpr, describerAsBase];

                // Fields: each one's FieldInfo, read and write in a small function of its own.
                var fieldInfo = new H.NamedType(H.TypeToken.Def(kit.FieldInfo), []);
                // An array kept in the object is not a value a FieldInfo could read or write (it has no handle): not listed.
                var fields = Enumerable.Range(0, def.Fields.Count).Where(f => (def.Fields[f].Flags & (H.FieldFlags.Generated | H.FieldFlags.InPlace)) == 0 && Shows(def.Fields[f].Name)).ToList();
                if (fields.Count > 0)
                {
                    Override("Fields", new H.ArrayType(fieldInfo), [owner], Return(Array(fieldInfo, fields.Select(f =>
                    {
                        var field = def.Fields[f];
                        int flags = Access(field.Access) | ((field.Flags & (H.FieldFlags.Static | H.FieldFlags.Const)) != 0 ? 2 : 0) | ((field.Flags & H.FieldFlags.Readonly) != 0 ? 16 : 0);
                        return Piece($"$f{f}", fieldInfo, ownerAndDescriber,
                            Return(new H.NewExpr(kit.FieldConstructor, [ownerParam, Text(field.Name), Int(flags), describerParam, Int(f), TypeOf(field.Type)], [], fieldInfo)), ListArguments());
                    }))));
                    var target = new H.ParamExpr(1, kit.AnyNullable);
                    var value = new H.ParamExpr(2, kit.AnyNullable);
                    var getSections = new List<H.SwitchSection>();
                    var setSections = new List<H.SwitchSection>();
                    foreach (int f in fields)
                    {
                        var field = def.Fields[f];
                        var token = H.MemberToken.Def(index, f);
                        var pieceTarget = new H.ParamExpr(0, kit.AnyNullable);
                        H.Expr read = field.Value is { } constant && (field.Flags & H.FieldFlags.Const) != 0 ? new H.LiteralExpr(constant, field.Type)
                            : (field.Flags & H.FieldFlags.Static) != 0 ? new H.FieldExpr(null, token, field.Type)
                            : new H.FieldExpr(Target(pieceTarget), token, field.Type);
                        getSections.Add(Section(f, Return(Piece($"$g{f}", kit.AnyNullable, [new H.Parameter("target", kit.AnyNullable)], Return(ToAny(read)), [target]))));
                        if ((field.Flags & H.FieldFlags.Const) != 0)
                            continue;
                        var pieceValue = new H.ParamExpr(1, kit.AnyNullable);
                        H.Stmt write;
                        if ((field.Flags & H.FieldFlags.Static) != 0)
                            write = Exec(new H.AssignExpr(new H.FieldExpr(null, token, field.Type), FromAny(pieceValue, field.Type, kit.BadValue), field.Type));
                        else if (IsClass)
                            write = Exec(new H.AssignExpr(new H.FieldExpr(Target(pieceTarget), token, field.Type), FromAny(pieceValue, field.Type, kit.BadValue), field.Type));
                        else
                            write = Exec(Call(kit.ValueField, H.HirType.Void, Text(field.Name)));
                        var set = Piece($"$s{f}", H.HirType.Void, [new H.Parameter("target", kit.AnyNullable), new H.Parameter("value", kit.AnyNullable)], write, [target, value]);
                        setSections.Add(Section(f, Block(Exec(set), Return(null))));
                    }
                    Override("GetField", kit.AnyNullable, [new H.Parameter("index", H.HirType.I32), new H.Parameter("target", kit.AnyNullable)],
                        Block(new H.SwitchStmt(new H.ParamExpr(0, H.HirType.I32), [.. getSections]), Return(Call(kit.Missing, kit.AnyNullable, Text("field")))));
                    if (setSections.Count > 0)
                    {
                        Override("SetField", H.HirType.Void, [new H.Parameter("index", H.HirType.I32), new H.Parameter("target", kit.AnyNullable), new H.Parameter("value", kit.AnyNullable)],
                            Block(new H.SwitchStmt(new H.ParamExpr(0, H.HirType.I32), [.. setSections]), Exec(Call(kit.Missing, kit.AnyNullable, Text("field")))));
                    }
                }

                if (IsEnum)
                    return;
                // Methods (and accessors, which properties call through Invoke).
                var methodInfo = new H.NamedType(H.TypeToken.Def(kit.MethodInfo), []);
                var listed = Enumerable.Range(0, def.Methods.Count).Where(m =>
                    def.Methods[m] is { Kind: H.MethodKind.Normal or H.MethodKind.Operator } method && (method.Flags & H.MethodFlags.Generated) == 0 && Shows(method.Name)).ToList();
                var callable = Enumerable.Range(0, def.Methods.Count).Where(m =>
                    def.Methods[m] is { Kind: H.MethodKind.Normal or H.MethodKind.Operator or H.MethodKind.Getter or H.MethodKind.Setter } method
                    && (method.Flags & H.MethodFlags.Generated) == 0 && method.Generics.Count == 0 && Shows(method.Name)
                    && method.Parameters.All(p => Passable(p.Type)) && Passable(method.Result)).ToList();
                if (listed.Count > 0)
                {
                    Override("Methods", new H.ArrayType(methodInfo), [owner], Return(Array(methodInfo, listed.Select(m =>
                    {
                        var method = def.Methods[m];
                        int flags = Access(method.Access) | (method.IsStatic ? 2 : 0) | ((method.Flags & H.MethodFlags.Virtual) != 0 ? 4 : 0)
                            | ((method.Flags & H.MethodFlags.Abstract) != 0 ? 8 : 0) | (method.Generics.Count > 0 ? 32 : 0);
                        return Piece($"$m{m}", methodInfo, ownerAndDescriber, Return(new H.NewExpr(kit.MethodConstructor,
                            [ownerParam, Text(method.Name), Int(flags), describerParam, Int(m), TypeOf(method.Result), Parameters(method.Parameters)], [], methodInfo)), ListArguments());
                    }))));
                }
                var argumentsType = new H.ArrayType(kit.AnyNullable);
                if (callable.Count > 0)
                {
                    var target = new H.ParamExpr(1, kit.AnyNullable);
                    var arguments = new H.ParamExpr(2, argumentsType);
                    var sections = callable.Select(m =>
                    {
                        var method = def.Methods[m];
                        var pieceTarget = new H.ParamExpr(0, kit.AnyNullable);
                        var pieceArguments = new H.ParamExpr(1, argumentsType);
                        var passed = method.Parameters.Select((p, i) => FromAny(new H.ElementExpr(pieceArguments, [Int(i)], kit.AnyNullable), p.Type, kit.BadValue)).ToArray();
                        var call = new H.CallExpr(method.IsStatic ? null : Target(pieceTarget), H.MemberToken.Def(index, m), own, passed, method.Result);
                        H.Stmt body = method.Result == H.HirType.Void ? Block(Exec(call), Return(Null(kit.AnyNullable))) : Return(ToAny(call));
                        var invoke = Piece($"$i{m}", kit.AnyNullable, [new H.Parameter("target", kit.AnyNullable), new H.Parameter("arguments", argumentsType)], body, [target, arguments]);
                        return Section(m, Return(invoke));
                    }).ToArray();
                    Override("Invoke", kit.AnyNullable, [new H.Parameter("index", H.HirType.I32), new H.Parameter("target", kit.AnyNullable), new H.Parameter("arguments", argumentsType)],
                        Block(new H.SwitchStmt(new H.ParamExpr(0, H.HirType.I32), sections), Return(Call(kit.Missing, kit.AnyNullable, Text("method")))));
                }

                // Properties (plain ones: indexers are left out).
                var propertyInfo = new H.NamedType(H.TypeToken.Def(kit.PropertyInfo), []);
                var properties = Enumerable.Range(0, def.Properties.Count).Where(p => def.Properties[p].Indices.Length == 0 && Shows(def.Properties[p].Name)).ToList();
                if (properties.Count > 0)
                {
                    Override("Properties", new H.ArrayType(propertyInfo), [owner], Return(Array(propertyInfo, properties.Select(p =>
                    {
                        var property = def.Properties[p];
                        int flags = Access(property.Access) | ((property.Flags & H.MemberFlags.Static) != 0 ? 2 : 0);
                        int getter = property.Getter >= 0 && callable.Contains(property.Getter) ? property.Getter : -1;
                        int setter = property.Setter >= 0 && callable.Contains(property.Setter) ? property.Setter : -1;
                        return Piece($"$p{p}", propertyInfo, ownerAndDescriber, Return(new H.NewExpr(kit.PropertyConstructor,
                            [ownerParam, Text(property.Name), Int(flags), describerParam, Int(p), TypeOf(property.Type), Int(getter), Int(setter)], [], propertyInfo)), ListArguments());
                    }))));
                }

                // Constructors (none for abstract and static classes).
                var constructorInfo = new H.NamedType(H.TypeToken.Def(kit.ConstructorInfo), []);
                var constructors = IsAbstract || IsInterface ? [] : Enumerable.Range(0, def.Methods.Count)
                    .Where(m => def.Methods[m].Kind == H.MethodKind.Constructor && def.Methods[m].Generics.Count == 0 && def.Methods[m].Parameters.All(p => Passable(p.Type))).ToList();
                if (constructors.Count > 0)
                {
                    Override("Constructors", new H.ArrayType(constructorInfo), [owner], Return(Array(constructorInfo, constructors.Select(m =>
                        Piece($"$c{m}", constructorInfo, ownerAndDescriber, Return(new H.NewExpr(kit.ConstructorConstructor,
                            [ownerParam, Int(Access(def.Methods[m].Access)), describerParam, Int(m), Parameters(def.Methods[m].Parameters)], [], constructorInfo)), ListArguments())))));
                }
                if (constructors.Count > 0 || IsStruct || IsPrimitive)
                {
                    var arguments = new H.ParamExpr(1, argumentsType);
                    var sections = constructors.Select(m =>
                    {
                        var pieceArguments = new H.ParamExpr(0, argumentsType);
                        var passed = def.Methods[m].Parameters.Select((p, i) => FromAny(new H.ElementExpr(pieceArguments, [Int(i)], kit.AnyNullable), p.Type, kit.BadValue)).ToArray();
                        var made = IsPrimitive ? (H.Expr)new H.DefaultExpr(self) : new H.NewExpr(H.MemberToken.Def(index, m), passed, [], self);
                        var construct = Piece($"$n{m}", kit.Any, [new H.Parameter("arguments", argumentsType)], Return(Convert(ToAny(made), H.ConversionKind.Unwrap, kit.Any)), [arguments]);
                        return Section(m, Return(construct));
                    }).ToList();
                    if (IsStruct || IsPrimitive)
                    {
                        // -1: the zero value.
                        sections.Add(Section(-1, Return(Convert(ToAny(IsStruct ? new H.NewExpr(null, [], [], self) : new H.DefaultExpr(self)), H.ConversionKind.Unwrap, kit.Any))));
                    }
                    Override("Construct", kit.Any, [new H.Parameter("index", H.HirType.I32), new H.Parameter("arguments", argumentsType)],
                        Block(new H.SwitchStmt(new H.ParamExpr(0, H.HirType.I32), [.. sections]),
                            Return(Convert(Call(kit.Missing, kit.AnyNullable, Text("constructor")), H.ConversionKind.Unwrap, kit.Any))));
                }
            }

            private static H.SwitchSection Section(int value, H.Stmt body) =>
                new([new H.ConstantPattern(new H.IntegerConstant(value, H.Primitive.I32))], null, body);

            /// <summary>A static function of the describer for one member (small functions lower faster than one large one): its call.</summary>
            private H.Expr Piece(string name, H.HirType result, H.Parameter[] parameters, H.Stmt body, H.Expr[] arguments)
            {
                // What describes a member runs once per type: small beats fast. What reads, writes and calls may be hot.
                var flags = H.MethodFlags.Static | (name[1] is 'f' or 'm' or 'p' or 'c' ? H.MethodFlags.Cold : 0);
                int method = AddMethod(describer, name, result, parameters, body, flags);
                return new H.CallExpr(null, H.MemberToken.Def(describerIndex, method), own, arguments, result);
            }
        }

        // ── Types made from others ─────────────────────────

        private readonly Dictionary<string, H.MemberToken> structural = [];

        /// <summary>
        /// The generic implementations for types made from others, added to System.Type: arrays, grids, nullable values,
        /// pointers, functions, void, and the last resorts (a type nothing describes).
        /// </summary>
        private void StructuralImplementations(ReflectionKit kit)
        {
            var typeDef = Assembly.Types[kit.Type];
            H.HirType T(int i) => new H.GenericParamType(H.GenericOwner.Method, i);
            H.Expr Of(H.HirType t) => new H.TypeOfExpr(t, kit.TypeType);
            void Add(string name, string requirement, H.HirType result, H.Parameter[] parameters, H.Stmt body, int generics)
            {
                int m = AddMethod(typeDef, name, result, parameters, body, H.MethodFlags.Static, generics: generics);
                structural[name] = H.MemberToken.Def(kit.Type, m);
                if (requirement.Length > 0)
                    kit.Implementations.Add((requirement, H.MemberToken.Def(kit.Type, m)));
            }
            for (int rank = 1; rank <= 8; rank++)
            {
                Add($"$ArrayTypeOf{rank}", "$typeof", kit.TypeType, [new H.Parameter("p", new H.PointerType(new H.ArrayType(T(0), rank)))],
                    Return(Call(kit.ArrayOf, kit.TypeType, Of(T(0)), Int(rank))), 1);
            }
            Add("$NullableTypeOf", "$typeof", kit.TypeType, [new H.Parameter("p", new H.PointerType(new H.NullableType(T(0))))], Return(Call(kit.NullableOf, kit.TypeType, Of(T(0)))), 1);
            Add("$PointerTypeOf", "$typeof", kit.TypeType, [new H.Parameter("p", new H.PointerType(new H.PointerType(T(0))))], Return(Call(kit.PointerOf, kit.TypeType, Of(T(0)))), 1);
            Add("$VoidTypeOf", "$typeof", kit.TypeType, [new H.Parameter("p", new H.PointerType(H.HirType.Void))], Return(Call(kit.VoidType, kit.TypeType)), 0);
            for (int n = 0; n <= MaxFuncParameters; n++)
            {
                var parameters = Enumerable.Range(0, n).Select(T).ToArray();
                var func = new H.FuncType(parameters, T(n));
                Add($"$FuncTypeOf{n}", "$typeof", kit.TypeType, [new H.Parameter("p", new H.PointerType(func))],
                    Return(Call(kit.FuncOf, kit.TypeType, new H.ArrayLiteralExpr(kit.TypeType, [.. parameters.Select(Of)], new H.ArrayType(kit.TypeType)), Of(T(n)),
                        new H.LiteralExpr(new H.BoolConstant(false), H.HirType.Bool))), n + 1);
            }
            Add("$UnknownTypeOf", "$typeof" + Requirements.FallbackSuffix, kit.TypeType, [new H.Parameter("p", new H.PointerType(T(0)))], Return(Call(kit.Unknown, kit.TypeType)), 1);

            // Nullable values as `any?` and back (null stays null), and the last resorts.
            var nullable = new H.NullableType(T(0));
            var value = new H.ParamExpr(0, nullable);
            Add("$BoxNullable", "$box", kit.AnyNullable, [new H.Parameter("value", nullable)], Block(
                new H.IfStmt(IsNull(value), Return(Null(kit.AnyNullable)), null),
                Return(Convert(Convert(value, H.ConversionKind.Unwrap, T(0)), H.ConversionKind.Box, kit.AnyNullable))), 1);
            var boxed = new H.ParamExpr(0, kit.AnyNullable);
            Add("$UnboxNullable", "$unbox", nullable, [new H.Parameter("value", kit.AnyNullable)], Block(
                new H.IfStmt(IsNull(boxed), Return(Null(nullable)), null),
                Return(Convert(Convert(boxed, H.ConversionKind.Unbox, T(0)), H.ConversionKind.Wrap, nullable))), 1);
            Add("$BoxUnknown", "$box" + Requirements.FallbackSuffix, kit.AnyNullable, [new H.Parameter("value", T(0))],
                Return(Call(kit.NotAnObject, kit.AnyNullable, new H.LiteralExpr(new H.StringConstant("this value"), kit.Text))), 1);
            Add("$UnboxUnknown", "$unbox" + Requirements.FallbackSuffix, T(0), [new H.Parameter("value", kit.AnyNullable)], Block(
                Exec(Call(kit.BadValue, kit.Any, new H.LiteralExpr(new H.StringConstant("value of a type that is not an object"), kit.Text))),
                Return(new H.DefaultExpr(T(0)))), 1);
        }

        /// <summary>After members have functions: the implementations registered, with those written in IR (nullable handles).</summary>
        private void RegisterReflection()
        {
            if (Reflection is not { } kit)
                return;
            foreach (var (requirement, method) in kit.Implementations)
                Implementations.Add(new Implementation(requirement, MethodFunctions[method]));
            var typeHandle = Lower(kit.TypeType, "reflection", new GenericContext(0));
            var root = Lower(kit.AnyNullable, "reflection", new GenericContext(0));
            // A nullable handle (`C?` of a class, `?gc S`): its type is the class's; boxing keeps null.
            {
                var b = new FunctionBuilder("$typeof.optional", typeHandle) { Unsafe = true, Linkage = Linkage.Internal };
                var t = Handle(b.Generic("S"));
                b.Argument(IrType.PtrMut(new OptionalType(t)), "p");
                var pointer = IrType.PtrMut(t);
                int f = b.Local(new FunctionType([pointer], typeHandle), null, mutable: true);
                int none = b.Local(pointer, null, mutable: true);
                int done = b.NewBlock();
                b.Assign(f, Rvalue.Implementation(new Requirement("$typeof", [pointer], typeHandle)))
                 .Assign(none, Rvalue.Cast(Operand.Const(0, IrType.U64), pointer))
                 .CallIndirect(Operand.Copy(f), [Operand.Copy(none)], FunctionBuilder.ReturnPlace, done);
                b.At(done).Return();
                Implementations.Add(new Implementation("$typeof", AddFunction(b.Build())));
            }
            {
                var b = new FunctionBuilder("$box.optional", root) { Unsafe = true, Linkage = Linkage.Internal };
                var t = Handle(b.Generic("S"));
                int value = b.Argument(new OptionalType(t), "value");
                int isNull = b.Local(IrType.Bool, null, mutable: true);
                int inner = b.Local(t, null, mutable: true);
                int f = b.Local(new FunctionType([t], root), null, mutable: true);
                int some = b.NewBlock(), none = b.NewBlock(), done = b.NewBlock();
                b.Assign(isNull, Rvalue.IsNull(new Place(value))).If(Operand.Copy(isNull), none, some);
                b.At(none).Assign(FunctionBuilder.ReturnPlace, Rvalue.ZeroFiller(root)).Return();
                b.At(some)
                 .Assign(inner, Rvalue.Unwrap(Operand.Copy(value)))
                 .Assign(f, Rvalue.Implementation(new Requirement("$box", [t], root)))
                 .CallIndirect(Operand.Copy(f), [Operand.Copy(inner)], FunctionBuilder.ReturnPlace, done);
                b.At(done).Return();
                Implementations.Add(new Implementation("$box", AddFunction(b.Build())));
            }
            {
                var t = Handle(new IR.GenericParamType(0));
                var b = new FunctionBuilder("$unbox.optional", new OptionalType(t)) { Unsafe = true, Linkage = Linkage.Internal };
                b.Generic("S");
                int value = b.Argument(root, "value");
                int isNull = b.Local(IrType.Bool, null, mutable: true);
                int inner = b.Local(t, null, mutable: true);
                int f = b.Local(new FunctionType([root], t), null, mutable: true);
                int some = b.NewBlock(), none = b.NewBlock(), done = b.NewBlock();
                b.Assign(isNull, Rvalue.IsNull(new Place(value))).If(Operand.Copy(isNull), none, some);
                b.At(none).Assign(FunctionBuilder.ReturnPlace, Rvalue.ZeroFiller(new OptionalType(t))).Return();
                b.At(some)
                 .Assign(f, Rvalue.Implementation(new Requirement("$unbox", [root], t)))
                 .CallIndirect(Operand.Copy(f), [Operand.Copy(value)], new Place(inner), done);
                b.At(done).Assign(FunctionBuilder.ReturnPlace, Rvalue.Wrap(Operand.Copy(inner))).Return();
                Implementations.Add(new Implementation("$unbox", AddFunction(b.Build())));
            }
        }
    }
}
