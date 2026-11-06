namespace Larva.Hir.Lowering
{
    using Larva.IR;
    using H = Larva.Hir;

    /// <summary>
    /// Values of interface types and boxed values.
    /// <para>
    /// An interface value is a handle to an object, the same as <c>any</c> (converting between them costs nothing). Every
    /// vtable starts with <c>$interfaces</c>: a raw array of <c>Larva.Interface { id, table }</c> entries, one per interface
    /// instance the class implements, whose id is the <c>TypeId</c> of that interface's itable struct with its arguments
    /// (so <c>IComparable&lt;str&gt;</c> and <c>IComparable&lt;i32&gt;</c> are told apart). An itable holds one function per
    /// method of the interface (and of those it extends): thunks taking the object as <c>any</c>, which call the type's
    /// implementation. A call through an interface value finds the itable (<c>I.$find</c>, a short search) and calls
    /// indirectly; <c>is</c> and <c>as</c> ask the same search.
    /// </para>
    /// <para>
    /// A value (a primitive, a value type, an enum) becomes an object in a box: <c>V$box { $class, $vt, value }</c>, a class
    /// of its own whose vtable answers <c>any</c>'s methods with the value's (ToString, Hash) and the value's interfaces with
    /// thunks that read the value out. Boxes are made the first time a program boxes a value of that type.
    /// </para>
    /// </summary>
    public sealed partial class HirLowerer
    {
        /// <summary>The struct of an interface entry of a vtable: <c>{ i32 id, u64 table }</c>.</summary>
        internal int InterfaceEntry;

        /// <summary>The type of a vtable's <c>$interfaces</c>: a raw array of entries.</summary>
        internal IrType InterfaceList => new IR.PointerType(new IR.ArrayType(IrType.Struct(InterfaceEntry)), Mutable: true);

        /// <summary>
        /// The fixed entries every vtable has after <c>$interfaces</c>: an interface's itable sits in entry
        /// <see cref="Color"/> of its name when no other interface of the class took it first. A call through an
        /// interface reads that entry and checks its id, and only searches <c>$interfaces</c> when it does not match.
        /// </summary>
        internal const int FixedEntries = 8;

        /// <summary>The field of a vtable's first method slot (after <c>$interfaces</c> and the fixed entries).</summary>
        internal const int SlotBase = 1 + FixedEntries;

        /// <summary>The fields every vtable starts with: <c>$interfaces</c>, then the fixed entries.</summary>
        internal IrType[] VtablePrefix => [InterfaceList, .. Enumerable.Repeat(IrType.Struct(InterfaceEntry), FixedEntries)];

        internal static readonly string[] VtablePrefixNames = ["$interfaces", .. Enumerable.Range(0, FixedEntries).Select(i => $"$fixed{i}")];

        /// <summary>
        /// The fixed entry of an interface: from its name alone (FNV-1a), so code lowered apart agrees on it; the id check
        /// keeps a disagreement or a collision correct, only slower.
        /// </summary>
        internal static int Color(string name)
        {
            uint hash = 2166136261;
            foreach (char c in name)
                hash = (hash ^ c) * 16777619;
            return (int)(hash % FixedEntries);
        }

        /// <summary>An interface's itable: its struct, the methods in its fields' order, the function that finds it in an object.</summary>
        internal sealed record Itable(int Struct, List<(TypeInfo Interface, H.MethodDef Method, H.HirType[] Arguments)> Methods, int Finder);

        internal readonly Dictionary<int, Itable> Itables = [];

        /// <summary>The $impl wrapper of each (type, interface method with its arguments): what thunks call.</summary>
        internal readonly Dictionary<(int Type, string Key), int> Wrappers = [];

        /// <summary>The thunks of each type that implements interfaces, by wrapper key: <c>fn(any self, …)</c>.</summary>
        private readonly Dictionary<(int Type, string Key), int> classThunks = [];

        internal string WrapperKey(TypeInfo iface, H.MethodDef method, H.HirType[] arguments) =>
            $"{RequirementName(iface, method)}<{string.Join(",", arguments.Select(x => H.Text.HirPrinter.Print(x, Assembly)))}>";

        /// <summary>Every interface instance a type implements (its own and those they extend), in the type's terms.</summary>
        internal List<H.NamedType> ImplementedInterfaces(TypeInfo info)
        {
            var result = new List<H.NamedType>();
            void Visit(H.HirType type)
            {
                if (type is not H.NamedType { Type.IsDefinition: true } named || !Types[named.Type.Index].IsInterface || result.Contains(named))
                    return;
                result.Add(named);
                foreach (var extended in Types[named.Type.Index].Definition.Interfaces)
                    Visit(Substitute(extended, named.Arguments));
            }
            foreach (var implemented in info.Definition.Interfaces)
                Visit(implemented);
            return result;
        }

        /// <summary>The itable structs and finders of every interface, then the thunks of every type that implements one.</summary>
        private void DeclareItables()
        {
            foreach (var info in Types)
            {
                if (!info.IsInterface)
                    continue;
                var context = new GenericContext(info.GenericCount);
                var methods = InterfaceMethods(new H.NamedType(H.TypeToken.Def(info.Index), OwnParameters(info))).ToList();
                var fields = methods.Select(m => (IrType)new FunctionType(
                    [RootHandle, .. m.Method.Parameters.Select(p => Lower(Substitute(p.Type, m.Arguments), info.Name, context))],
                    Lower(Substitute(m.Method.Result, m.Arguments), info.Name, context))).ToArray();
                int structIndex = Structs.Count;
                Structs.Add(new StructDef(info.Name + "$itable", fields)
                {
                    FieldNames = [.. methods.Select(m => m.Method.Name)],
                    Linkage = LinkageOf(info),
                    Generics = IrGenerics(info.Definition.Generics),
                });
                int finder = Functions.Count;
                Functions.Add(null);
                Itables[info.Index] = new Itable(structIndex, methods, finder);
            }
            foreach (var info in Types)
            {
                // Classes' thunks now; a value type's come with its box (if the program boxes one).
                if (!info.IsClass)
                    continue;
                foreach (var implemented in ImplementedInterfaces(info))
                {
                    foreach (var (iface, method, arguments) in InterfaceMethods(implemented))
                    {
                        string key = WrapperKey(iface, method, arguments);
                        if (classThunks.ContainsKey((info.Index, key)) || !Wrappers.ContainsKey((info.Index, key)))
                            continue;
                        classThunks[(info.Index, key)] = Functions.Count;
                        Functions.Add(null);
                    }
                }
            }
        }

        /// <summary>The bodies of the finders and of the thunks of classes (value types get theirs with their box).</summary>
        private void BuildItables()
        {
            foreach (var (index, itable) in Itables)
                Functions[itable.Finder] = Finder(Types[index], itable);
            foreach (var ((type, key), function) in classThunks)
            {
                var info = Types[type];
                if (info.IsClass)
                    Functions[function] = Thunk(info, key, $"{info.Name}.$itable.{key}", box: null);
            }
        }

        /// <summary>
        /// <c>I.$find&lt;P…&gt;(any obj) -&gt; ?*I$itable&lt;P…&gt;</c>: the object's itable of I with these arguments, or null
        /// when its class does not implement it (or it has no vtable).
        /// </summary>
        private IrFunction Finder(TypeInfo info, Itable itable)
        {
            var own = info.OwnArguments;
            var tableType = new IR.PointerType(IrType.Struct(itable.Struct, own), Mutable: true);
            var result = new OptionalType(tableType);
            var b = new FunctionBuilder(info.Name + ".$find", result) { Unsafe = true, Linkage = Linkage.Internal };
            DeclareGenerics(b, IrGenerics(info.Definition.Generics));
            int obj = b.Argument(RootHandle, "obj", mutable: false);
            int vt = b.Local(VtablePointer(IrType.Struct(ObjectVtable)), "vt", mutable: true);
            int address = b.Local(IrType.U64, null, mutable: true);
            int noVtable = b.Local(IrType.Bool, null, mutable: true);
            int list = b.Local(InterfaceList, "list", mutable: true);
            int count = b.Local(IrType.U64, "count", mutable: true);
            int i = b.Local(IrType.U64, "i", mutable: true);
            int want = b.Local(IrType.I32, "want", mutable: true);
            int id = b.Local(IrType.I32, null, mutable: true);
            int test = b.Local(IrType.Bool, null, mutable: true);
            int raw = b.Local(IrType.U64, null, mutable: true);
            int found = b.Local(tableType, null, mutable: true);
            int search = b.NewBlock(), head = b.NewBlock(), body = b.NewBlock(), next = b.NewBlock(), hit = b.NewBlock(), none = b.NewBlock();
            b.Assign(vt, Operand.Copy(new Place(obj).Deref().Field(1)))
             .Assign(address, Rvalue.Cast(Operand.Copy(vt), IrType.U64))
             .Assign(noVtable, Rvalue.Binary(BinaryOp.Eq, Operand.Copy(address), Operand.Const(0, IrType.U64)))
             .If(Operand.Copy(noVtable), none, search);
            b.At(search)
             .Assign(list, Operand.Copy(new Place(vt).Deref().Field(0)))
             .Assign(count, Rvalue.Len(new Place(list).Deref()))
             .Assign(want, Rvalue.TypeId(IrType.Struct(itable.Struct, own)))
             .Assign(i, Operand.Const(0, IrType.U64))
             .Goto(head);
            b.At(head).Assign(test, Rvalue.Binary(BinaryOp.Lt, Operand.Copy(i), Operand.Copy(count))).If(Operand.Copy(test), body, none);
            b.At(body)
             .Assign(id, Operand.Copy(new Place(list).Deref().Index(i).Field(0)))
             .Assign(test, Rvalue.Binary(BinaryOp.Eq, Operand.Copy(id), Operand.Copy(want)))
             .If(Operand.Copy(test), hit, next);
            b.At(next).Assign(i, Rvalue.Binary(BinaryOp.Add, Operand.Copy(i), Operand.Const(1, IrType.U64))).Goto(head);
            b.At(hit)
             .Assign(raw, Operand.Copy(new Place(list).Deref().Index(i).Field(1)))
             .Assign(found, Rvalue.Cast(Operand.Copy(raw), tableType))
             .Assign(FunctionBuilder.ReturnPlace, Rvalue.Wrap(Operand.Copy(found)))
             .Return();
            b.At(none).Assign(FunctionBuilder.ReturnPlace, Rvalue.Zero(result)).Return();
            return b.Build();
        }

        /// <summary>
        /// A thunk: <c>fn(any self, params…) -&gt; R</c> calling the type's $impl wrapper with the object (a class) or the
        /// value in the box (<paramref name="box"/>).
        /// </summary>
        private IrFunction Thunk(TypeInfo info, string key, string name, Box? box)
        {
            int wrapper = Wrappers[(info.Index, key)];
            var plan = Plans.First(p => p.Function == wrapper);
            var b = new FunctionBuilder(name, plan.Result) { Unsafe = true, Linkage = Linkage.Internal };
            DeclareGenerics(b, plan.Generics);
            int self = b.Argument(RootHandle, "self", mutable: false);
            var parameters = new int[plan.Parameters.Length];
            for (int p = 1; p < plan.Parameters.Length; p++)
                parameters[p] = b.Argument(plan.Parameters[p], $"p{p}", mutable: true);
            var arguments = new List<Operand>();
            var selfType = plan.Parameters[0];
            int receiver = b.Local(selfType, "receiver", mutable: true);
            if (box is null)
            {
                b.Assign(receiver, Rvalue.HandleCast(Operand.Copy(self), selfType));
            }
            else
            {
                int boxed = b.Local(Handle(IrType.Struct(box.Struct, info.OwnArguments)), "box", mutable: true);
                b.Assign(boxed, Rvalue.HandleCast(Operand.Copy(self), Handle(IrType.Struct(box.Struct, info.OwnArguments))));
                CopyValue(b, new Place(receiver), new Place(boxed).Deref().Field(2), selfType, plan.Generics);
            }
            arguments.Add(Operand.Move(receiver));
            for (int p = 1; p < plan.Parameters.Length; p++)
                arguments.Add(Operand.Move(parameters[p]));
            int next = b.NewBlock();
            b.Call(wrapper, info.OwnArguments, [.. arguments], FunctionBuilder.ReturnPlace, next, []);
            b.At(next).Return();
            return b.Build();
        }

        /// <summary>
        /// A copy of the value at <paramref name="source"/> into <paramref name="target"/>: a plain copy, or field by field
        /// for value types holding objects (IR does not copy those whole).
        /// </summary>
        internal void CopyValue(FunctionBuilder b, Place target, Place source, IrType type, GenericParam[] generics)
        {
            var module = new IrModule([.. Structs], []);
            void Copy(Place to, Place from, IrType t)
            {
                if (t is StructType st && !Structs[st.Id].Mutable && !Constraints.Satisfies(t, TypeConstraint.Copy, new TypeContext(module, generics)))
                {
                    b.Assign(to, Rvalue.ZeroFiller(t));
                    var fields = Structs[st.Id].Fields;
                    for (int f = 0; f < fields.Length; f++)
                        Copy(to.Field(f), from.Field(f), fields[f].Substitute(st.Arguments));
                    return;
                }
                b.Assign(to, Operand.Copy(from));
            }
            lock (Structs)
                Copy(target, source, type);
        }

        /// <summary>The interface list of a vtable being made: an entry per interface instance, with its itable filled.</summary>
        internal void FillInterfaces(FunctionBuilder b, Place vtable, IReadOnlyList<(TypeInfo Owner, H.NamedType Interface, IrType[] OwnerArguments, Func<string, int?> Thunk)> interfaces)
        {
            // The first interface of each color gets the fixed entry (the class's own before its bases', as the search finds them).
            var taken = new bool[FixedEntries];
            int count = b.Local(IrType.U64, null, mutable: true);
            int list = b.Local(InterfaceList, "$interfaces", mutable: true);
            int at = b.Local(IrType.U64, null, mutable: true);
            b.Assign(count, Operand.Const(interfaces.Count, IrType.U64))
             .Assign(list, new ArrayRvalue(AllocKind.Raw, IrType.Struct(InterfaceEntry), Operand.Copy(count)) { Filled = true });
            for (int e = 0; e < interfaces.Count; e++)
            {
                var (owner, iface, ownerArguments, thunk) = interfaces[e];
                var ifaceInfo = Types[iface.Type.Index];
                var itable = Itables[ifaceInfo.Index];
                var context = new GenericContext(owner.GenericCount);
                var arguments = iface.Arguments.Select(a => Lower(a, owner.Name, context).Substitute(ownerArguments)).ToArray();
                var tableType = IrType.Struct(itable.Struct, arguments);
                int table = b.Local(tableType, null, mutable: true);
                b.Assign(table, Rvalue.ZeroFiller(tableType));
                var methods = InterfaceMethods(iface).ToList();
                for (int k = 0; k < methods.Count; k++)
                {
                    var (declaring, method, methodArguments) = methods[k];
                    if (thunk(WrapperKey(declaring, method, methodArguments)) is { } function)
                        b.Assign(new Place(table).Field(k), Rvalue.FunctionRef(function, ownerArguments));
                }
                var pointer = new IR.PointerType(tableType, Mutable: true);
                int made = b.Local(pointer, null, mutable: true);
                int address = b.Local(IrType.U64, null, mutable: true);
                b.Assign(made, new AllocRvalue(AllocKind.Raw, Operand.Move(table)))
                 .Assign(address, Rvalue.Cast(Operand.Copy(made), IrType.U64))
                 .Assign(at, Operand.Const(e, IrType.U64))
                 .Assign(new Place(list).Deref().Index(at).Field(0), Rvalue.TypeId(tableType))
                 .Assign(new Place(list).Deref().Index(at).Field(1), Operand.Copy(address));
                int color = Color(ifaceInfo.Name);
                if (!taken[color])
                {
                    taken[color] = true;
                    b.Assign(vtable.Field(1 + color).Field(0), Rvalue.TypeId(tableType))
                     .Assign(vtable.Field(1 + color).Field(1), Operand.Copy(address));
                }
            }
            // An unused entry has an id no itable has.
            for (int c = 0; c < FixedEntries; c++)
            {
                if (!taken[c])
                    b.Assign(vtable.Field(1 + c).Field(0), Operand.Const(-1, IrType.I32));
            }
            b.Assign(vtable.Field(0), Operand.Copy(list));
        }

        /// <summary>The interfaces a class's vtable lists: its own and its bases' (their thunks, with their arguments seen from it).</summary>
        internal List<(TypeInfo Owner, H.NamedType Interface, IrType[] OwnerArguments, Func<string, int?> Thunk)> ClassInterfaces(TypeInfo info)
        {
            var result = new List<(TypeInfo, H.NamedType, IrType[], Func<string, int?>)>();
            for (int t = info.Index; t >= 0; t = Types[t].Base)
            {
                var owner = Types[t];
                var ownerArguments = AncestorIrArguments(info, t);
                foreach (var iface in ImplementedInterfaces(owner))
                {
                    int type = t;
                    result.Add((owner, iface, ownerArguments, key => classThunks.TryGetValue((type, key), out int f) ? f : null));
                }
            }
            return result;
        }

        // ── Boxes ──────────────────────────────────────────

        internal sealed record Box(int Struct, int Vtable, int VtableGlobal, int Maker);

        private readonly Dictionary<int, Box> boxes = [];

        /// <summary>The type that gives a value its members: its own (value types, enums), a primitive's provider (Int32).</summary>
        internal TypeInfo? ValueInfo(H.HirType type) => type switch
        {
            H.NamedType { Type.IsDefinition: true } n => Types[n.Type.Index],
            H.PrimitiveType p => Types.FirstOrDefault(t => t.IsPrimitive && t.Definition.Underlying is H.PrimitiveType { Kind: var k } && k == p.Kind),
            _ => null,
        };

        /// <summary>The box of a value type (made the first time it is asked for).</summary>
        internal Box BoxOf(TypeInfo info)
        {
            lock (boxes)
            {
                if (boxes.TryGetValue(info.Index, out var existing))
                    return existing;
                var root = Types.FirstOrDefault(t => t.IsClass && t.Base < 0 && t.Definition.FullName == "System.Any");
                var context = new GenericContext(info.GenericCount);
                var own = info.OwnArguments;
                var valueType = info.IsPrimitive || info.IsEnum
                    ? Lower(info.IsPrimitive ? info.Definition.Underlying! : new H.NamedType(H.TypeToken.Def(info.Index), []), info.Name, context)
                    : StructOf(info, own);
                // The slots of `any` (ToString, Hash…): the value's method of that name, else any's own.
                var slots = root?.Slots ?? [];
                var slotTypes = (root?.SlotIntroducers ?? []).Select(SlotType).ToArray();
                int boxStruct, vtableStruct, global, maker;
                lock (Structs)
                {
                    boxStruct = Structs.Count;
                    Structs.Add(new StructDef(info.Name + "$box", IrType.I32, VtablePointer(IrType.Struct(ObjectVtable)), valueType)
                    {
                        FieldNames = ["$class", "$vt", "value"],
                        Mutable = true,
                        Base = root is not null ? (StructType)IrType.Struct(root.Struct) : (StructType)IrType.Struct(ObjectStruct),
                        Linkage = Linkage.Internal,
                        Generics = IrGenerics(info.Definition.Generics),
                    });
                    vtableStruct = Structs.Count;
                    Structs.Add(new StructDef(info.Name + "$box$vt", [.. VtablePrefix, .. slotTypes])
                    {
                        FieldNames = [.. VtablePrefixNames, .. slots.Select(s => Types[s.Type].Definition.Methods[s.Method].Name)],
                        Mutable = true,
                        Base = root is not null ? (StructType)IrType.Struct(root.Vtable) : (StructType)IrType.Struct(ObjectVtable),
                        Linkage = Linkage.Internal,
                        Generics = IrGenerics(info.Definition.Generics),
                    });
                }
                lock (Globals)
                {
                    global = Globals.Count;
                    Globals.Add(new GlobalDef(info.Name + "$box$vtable", IrType.U64) { GenericCount = info.GenericCount });
                }
                var box = new Box(boxStruct, vtableStruct, global, -1);
                // Thunks: any's slots answered by the value's methods, and the value's interfaces.
                var slotFunctions = new (int Function, bool Own)[slots.Count];
                for (int s = 0; s < slots.Count; s++)
                {
                    var slotMethod = Types[slots[s].Type].Definition.Methods[slots[s].Method];
                    int own_ = info.Definition.Methods.FindIndex(m => !m.IsStatic && m.Name == slotMethod.Name && m.Parameters.Count == 0 && m.Result == slotMethod.Result);
                    if (slotMethod.Name == "RuntimeType" && Reflection is { } kit && kit.Described.TryGetValue(info.Index, out var described))
                    {
                        // GetType() of a box: its value's type.
                        slotFunctions[s] = (AddFunction(BoxTypeThunk(info, described, (FunctionType)slotTypes[s])), true);
                        continue;
                    }
                    slotFunctions[s] = own_ >= 0 ? (AddFunction(SlotThunk(info, box, own_, slotTypes[s])), true) : (MethodFunctions[H.MemberToken.Def(slots[s].Type, slots[s].Method)], false);
                }
                var thunks = new Dictionary<string, int>();
                foreach (var iface in ImplementedInterfaces(info))
                {
                    foreach (var (declaring, method, arguments) in InterfaceMethods(iface))
                    {
                        string key = WrapperKey(declaring, method, arguments);
                        if (!thunks.ContainsKey(key) && Wrappers.ContainsKey((info.Index, key)))
                            thunks[key] = AddFunction(Thunk(info, key, $"{info.Name}$box.$itable.{key}", box));
                    }
                }
                maker = AddFunction(BoxVtableMaker(info, box, slotFunctions, thunks));
                box = box with { Maker = maker };
                boxes[info.Index] = box;
                return box;
            }
        }

        private int AddFunction(IrFunction function)
        {
            lock (Functions)
            {
                Functions.Add(function);
                return Functions.Count - 1;
            }
        }

        /// <summary><c>V$box.RuntimeType(any self)</c>: the value's type (its describer's <c>$Type()</c>).</summary>
        private IrFunction BoxTypeThunk(TypeInfo info, (int Describer, int TypeMethod) described, FunctionType slot)
        {
            var b = new FunctionBuilder($"{info.Name}$box.RuntimeType", slot.Result) { Linkage = Linkage.Internal };
            DeclareGenerics(b, FunctionGenerics(info, null, info.Name));
            b.Argument(slot.Parameters[0], "self", mutable: false);
            var typeHandle = ((OptionalType)slot.Result).Inner;
            int type = b.Local(typeHandle, null, mutable: true);
            int next = b.NewBlock();
            b.Call(MethodFunctions[H.MemberToken.Def(described.Describer, described.TypeMethod)], info.OwnArguments, [], new Place(type), next, []);
            b.At(next).Assign(FunctionBuilder.ReturnPlace, Rvalue.Wrap(Operand.Copy(type))).Return();
            return b.Build();
        }

        /// <summary><c>V$box.Name(any self)</c>: a slot of any's vtable answered by the value's own method.</summary>
        private IrFunction SlotThunk(TypeInfo info, Box box, int method, IrType slotType)
        {
            var plan = PlanOf[(info.Index, method)];
            var function = (FunctionType)slotType;
            var b = new FunctionBuilder($"{info.Name}$box.{info.Definition.Methods[method].Name}", function.Result) { Unsafe = true, Linkage = Linkage.Internal };
            DeclareGenerics(b, plan.Generics);
            int self = b.Argument(function.Parameters[0], "self", mutable: false);
            var own = info.OwnArguments;
            var boxHandle = Handle(IrType.Struct(box.Struct, own));
            int boxed = b.Local(boxHandle, "box", mutable: true);
            b.Assign(boxed, Rvalue.HandleCast(Operand.Copy(self), boxHandle));
            Operand receiver;
            if (plan.This is ReferenceType { Pointee: var pointee })
            {
                // A value type's method takes &mut self: a copy of the value, borrowed.
                int value = b.Local(pointee, "value", mutable: true);
                CopyValue(b, new Place(value), new Place(boxed).Deref().Field(2), pointee, plan.Generics);
                int reference = b.Local(plan.This, null, mutable: true);
                b.Assign(reference, Rvalue.RefMut(new Place(value)));
                receiver = Operand.Move(reference);
            }
            else
            {
                int value = b.Local(plan.This!, "value", mutable: true);
                b.Assign(value, Operand.Copy(new Place(boxed).Deref().Field(2)));
                receiver = Operand.Copy(value);
            }
            int next = b.NewBlock();
            b.Call(plan.Function, own, [receiver], FunctionBuilder.ReturnPlace, next, []);
            b.At(next).Return();
            return b.Build();
        }

        /// <summary><c>V$box.$vtable()</c>: the box's vtable (made once, kept in its global).</summary>
        private IrFunction BoxVtableMaker(TypeInfo info, Box box, (int Function, bool Own)[] slotFunctions, Dictionary<string, int> thunks)
        {
            var own = info.OwnArguments;
            var vtType = IrType.Struct(box.Vtable, own);
            var handle = VtablePointer(vtType);
            var b = new FunctionBuilder(info.Name + "$box.$vtable", handle) { Unsafe = true, Linkage = Linkage.Internal };
            DeclareGenerics(b, FunctionGenerics(info, null, info.Name));
            int current = b.Local(IrType.U64, null, mutable: true);
            int isNull = b.Local(IrType.Bool, null, mutable: true);
            int make = b.NewBlock(), done = b.NewBlock();
            b.Assign(current, Rvalue.ReadGlobal(box.VtableGlobal, own))
             .Assign(isNull, Rvalue.Binary(BinaryOp.Eq, Operand.Copy(current), Operand.Const(0, IrType.U64)))
             .If(Operand.Copy(isNull), make, done);
            b.At(make);
            int table = b.Local(vtType, null, mutable: true);
            b.Assign(table, Rvalue.ZeroFiller(vtType));
            var interfaces = ImplementedInterfaces(info).Select(i => (info, i, own, (Func<string, int?>)(key => thunks.TryGetValue(key, out int f) ? f : null))).ToList();
            FillInterfaces(b, new Place(table), interfaces);
            for (int s = 0; s < slotFunctions.Length; s++)
            {
                var (function, isOwn) = slotFunctions[s];
                b.Assign(new Place(table).Field(s + SlotBase), isOwn ? Rvalue.FunctionRef(function, own) : Rvalue.FunctionRef(function));
            }
            int made = b.Local(handle, null, mutable: true);
            b.Assign(made, new AllocRvalue(AllocKind.Raw, Operand.Move(table)));
            int address = b.Local(IrType.U64, null, mutable: true);
            b.Assign(address, Rvalue.Cast(Operand.Copy(made), IrType.U64))
             .StoreGlobal(box.VtableGlobal, own, Operand.Copy(address))
             .Assign(FunctionBuilder.ReturnPlace, Operand.Copy(made))
             .Return();
            b.At(done);
            b.Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Operand.Copy(current), handle)).Return();
            return b.Build();
        }
    }
}
