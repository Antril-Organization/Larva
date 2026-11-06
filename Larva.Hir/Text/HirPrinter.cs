using System.Globalization;
using System.Text;

namespace Larva.Hir.Text
{
    /// <summary>
    /// HIR as text, for people and tests. With <c>types</c>, every expression shows its type (<c>x:i32</c>), so two
    /// assemblies print the same exactly when they are the same.
    /// </summary>
    public sealed class HirPrinter
    {
        private readonly HirAssembly assembly;
        private readonly bool types;
        private readonly StringBuilder text = new();
        private int indent;

        private HirPrinter(HirAssembly assembly, bool types)
        {
            this.assembly = assembly;
            this.types = types;
        }

        public static string Print(HirAssembly assembly, bool types = false)
        {
            var printer = new HirPrinter(assembly, types);
            printer.Assembly();
            return printer.text.ToString();
        }

        public static string Print(HirType type, HirAssembly assembly) => new HirPrinter(assembly, false).Type(type);

        private void Line(string line)
        {
            text.Append(' ', indent * 4).Append(line).Append('\n');
        }

        private void Assembly()
        {
            Line($"assembly {assembly.Name} v{Version(assembly.Version)}");
            foreach (var reference in assembly.References)
                Line($"reference {reference.Name}{(reference.Version.Length > 0 ? " v" + Version(reference.Version) : "")}");
            for (int i = 0; i < assembly.TypeRefs.Count; i++)
            {
                var r = assembly.TypeRefs[i];
                Line($"typeref TR{i} = [{assembly.References[r.Assembly].Name}]{Qualified(r.Namespace, r.Name)}`{r.Arity}");
            }
            for (int i = 0; i < assembly.MemberRefs.Count; i++)
            {
                var m = assembly.MemberRefs[i];
                Line($"memberref MR{i} = {m.Kind.ToString().ToLowerInvariant()} {TypeName(m.Parent)}::{m.Name}`{m.GenericArity}({string.Join(", ", m.Parameters.Select(Type))}) : {Type(m.Result)}");
            }
            for (int i = 0; i < assembly.Files.Count; i++)
                Line($"file {i} {Quote(assembly.Files[i])}");
            foreach (var attribute in assembly.Attributes)
                Line(Attribute(attribute));
            foreach (var resource in assembly.Resources)
                Line($"resource {Quote(resource.Name)} {resource.Data.Length} bytes {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(resource.Data))[..16]}");
            if (assembly.EntryPoint is { } entry)
                Line($"entry {Member(entry, MemberKind.Method)}");
            for (int t = 0; t < assembly.Types.Count; t++)
                TypeDefinition(t);
        }

        private static string Version(int[] version) => string.Join(".", version);

        private static string Qualified(string ns, string name) => ns.Length == 0 ? name : $"{ns}.{name}";

        private void TypeDefinition(int index)
        {
            var type = assembly.Types[index];
            Line("");
            foreach (var attribute in type.Attributes)
                Line(Attribute(attribute));
            var head = new StringBuilder();
            head.Append(Lower(type.Access)).Append(' ');
            foreach (var flag in Enum.GetValues<TypeFlags>())
            {
                if (flag != TypeFlags.None && type.Flags.HasFlag(flag))
                    head.Append(Lower(flag)).Append(' ');
            }
            head.Append(Lower(type.Kind)).Append(' ').Append(type.FullName).Append(Generics(type.Generics));
            if (type.DeclaringType >= 0)
                head.Append(" in ").Append(assembly.Types[type.DeclaringType].FullName);
            var supers = new List<string>();
            if (type.Base is { } b)
                supers.Add(Type(b));
            supers.AddRange(type.Interfaces.Select(Type));
            if (type.Underlying is { } u)
                supers.Add(Type(u));
            if (supers.Count > 0)
                head.Append(" : ").Append(string.Join(", ", supers));
            head.Append(Location(type.Location));
            Line(head.ToString());
            Line("{");
            indent++;
            foreach (var field in type.Fields)
            {
                foreach (var attribute in field.Attributes)
                    Line(Attribute(attribute));
                Line($"field {Lower(field.Access)} {Flags(field.Flags)}{Type(field.Type)} {field.Name}{(field.Value is { } v ? " = " + Constant(v) : "")}");
            }
            foreach (var property in type.Properties)
            {
                foreach (var attribute in property.Attributes)
                    Line(Attribute(attribute));
                string indices = property.Indices.Length > 0 ? $"[{string.Join(", ", property.Indices.Select(Type))}]" : "";
                Line($"property {Lower(property.Access)} {Flags(property.Flags)}{Type(property.Type)} {property.Name}{indices} get #{property.Getter} set #{property.Setter}");
            }
            foreach (var e in type.Events)
            {
                foreach (var attribute in e.Attributes)
                    Line(Attribute(attribute));
                Line($"event {Lower(e.Access)} {Flags(e.Flags)}{Type(e.Type)} {e.Name} add #{e.Adder} remove #{e.Remover}");
            }
            foreach (var op in type.Operators)
                Line($"operator {Lower(op.Fixity)} {Quote(op.Symbol)} precedence {op.Precedence} {Lower(op.Associativity)} #{op.Method}");
            for (int m = 0; m < type.Methods.Count; m++)
                Method(m, type.Methods[m]);
            indent--;
            Line("}");
        }

        private static string Flags<T>(T flags) where T : struct, Enum
        {
            var words = new StringBuilder();
            foreach (var flag in Enum.GetValues<T>())
            {
                if (Convert.ToUInt64(flag) != 0 && flags.HasFlag(flag))
                    words.Append(Lower(flag)).Append(' ');
            }
            return words.ToString();
        }

        private void Method(int index, MethodDef method)
        {
            foreach (var attribute in method.Attributes)
                Line(Attribute(attribute));
            var head = new StringBuilder($"#{index} {(method.Kind == MethodKind.Normal ? "" : Lower(method.Kind) + " ")}{Lower(method.Access)} {Flags(method.Flags)}{method.Name}{Generics(method.Generics)}(");
            for (int p = 0; p < method.Parameters.Count; p++)
            {
                var parameter = method.Parameters[p];
                if (p > 0)
                    head.Append(", ");
                foreach (var attribute in parameter.Attributes)
                    head.Append(Attribute(attribute)).Append(' ');
                head.Append(Flags(parameter.Flags)).Append(Type(parameter.Type)).Append(' ').Append(parameter.Name);
                if (parameter.Default is { } d)
                    head.Append(" = ").Append(Constant(d));
            }
            head.Append(") : ").Append(Type(method.Result));
            if (method.ExternName is { } name)
                head.Append(" extern ").Append(Quote(name));
            if (method.Library is { } library)
                head.Append(" from ").Append(Quote(library));
            head.Append(Location(method.Location));
            if (method.Body is not { } body)
            {
                Line(head.ToString());
                return;
            }
            Line(head.ToString());
            Line("{");
            indent++;
            for (int l = 0; l < body.Locals.Length; l++)
                Line($"local ${l} {(body.Locals[l].Mutable ? "mut " : "")}{Type(body.Locals[l].Type)} {body.Locals[l].Name}");
            foreach (var statement in body.Block.Statements)
                Statement(statement);
            indent--;
            Line("}");
        }

        private string Location(SourceLocation at) => at.IsKnown ? $" @{at.File}:{at.Line}:{at.Column}" : "";

        private string Generics(List<GenericParam> generics) => generics.Count == 0 ? "" : $"<{string.Join(", ", generics.Select(Generic))}>";

        private string Generic(GenericParam g)
        {
            var parts = new List<string>();
            if (g.Kinds != ConstraintKinds.None)
                parts.Add(Flags(g.Kinds).TrimEnd());
            parts.AddRange(g.Types.Select(Type));
            if (g.OneOf.Length > 0)
                parts.Add($"[{string.Join(", ", g.OneOf.Select(Type))}]");
            parts.AddRange(g.Operators.Select(o => $"operator {Quote(o.Symbol)}({string.Join(", ", o.Operands.Select(Type))}) : {Type(o.Result)}"));
            parts.AddRange(g.ConvertsTo.Select(t => $"as {Type(t)}"));
            parts.AddRange(g.Constructors.Select(c => $"new({string.Join(", ", c.Select(Type))})"));
            if (g.FixedSize > 0)
                parts.Add($"fixed({g.FixedSize})");
            string text = g.Name + (g.Variadic ? "..." : "");
            if (parts.Count > 0)
                text += " : " + string.Join(" + ", parts);
            if (g.Default is { } d)
                text += " = " + Type(d);
            return text;
        }

        private string Attribute(HirAttribute attribute)
        {
            var args = attribute.Arguments.Select(Constant).Concat(attribute.Named.Select(n => $"{n.Name} = {Constant(n.Value)}"));
            return $"[{Member(attribute.Constructor, MemberKind.Method)}({string.Join(", ", args)})]";
        }

        private static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

        private static string Quote(string value)
        {
            var quoted = new StringBuilder("\"");
            foreach (char c in value)
            {
                quoted.Append(c switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    < ' ' => $"\\u{(int)c:x4}",
                    _ => c.ToString(),
                });
            }
            return quoted.Append('"').ToString();
        }

        // ── Names ──────────────────────────────────────────

        private string TypeName(TypeToken token)
        {
            if (token.IsDefinition)
                return token.Index < assembly.Types.Count ? assembly.Types[token.Index].FullName : $"T{token.Index}?";
            var r = assembly.TypeRefs[token.Index];
            return $"[{assembly.References[r.Assembly].Name}]{Qualified(r.Namespace, r.Name)}";
        }

        /// <summary>A member by name: methods also by index (<c>Name#3</c>: overloads share a name), references by their entry.</summary>
        private string Member(MemberToken token, MemberKind kind)
        {
            if (!token.IsDefinition)
            {
                var r = token.Member < assembly.MemberRefs.Count ? assembly.MemberRefs[token.Member] : null;
                return r is null ? $"MR{token.Member}?" : $"{TypeName(r.Parent)}::{r.Name}~{token.Member}";
            }
            string owner = TypeName(TypeToken.Def(token.Type));
            var type = token.Type < assembly.Types.Count ? assembly.Types[token.Type] : null;
            string? name = kind switch
            {
                MemberKind.Field => type?.Fields.ElementAtOrDefault(token.Member)?.Name,
                MemberKind.Property => type?.Properties.ElementAtOrDefault(token.Member)?.Name,
                MemberKind.Event => type?.Events.ElementAtOrDefault(token.Member)?.Name,
                _ => type?.Methods.ElementAtOrDefault(token.Member)?.Name is { } method ? $"{method}#{token.Member}" : null,
            };
            return $"{owner}::{name ?? "?" + token.Member}";
        }

        public string Type(HirType type) => type switch
        {
            PrimitiveType p => p.Kind.Keyword(),
            NamedType n => TypeName(n.Type) + (n.Arguments.Length > 0 ? $"<{string.Join(", ", n.Arguments.Select(Type))}>" : ""),
            ArrayType a => $"{Type(a.Element)}[{new string(',', a.Rank - 1)}]",
            InlineArrayType i => $"fixed {Type(i.Element)}[{(i.SizeOf is null ? i.Count.ToString() : (i.Count == 1 ? "" : $"{i.Count} * ") + $"sizeof({Type(i.SizeOf)})")}]",
            NullableType n => Type(n.Inner) + "?",
            ReferenceType r => (r.Mutable ? "&mut " : "&") + Type(r.Target),
            PointerType p => Type(p.Target) + (p.Mutable ? "*" : " const*"),
            FuncType f => $"func<{string.Join(", ", f.Parameters.Select(Type))}> : {Type(f.Result)}",
            NativeFuncType f => $"func*<{string.Join(", ", f.Parameters.Select(Type))}> : {Type(f.Result)}",
            GenericParamType g => (g.Owner == GenericOwner.Type ? "!" : "!!") + g.Index,
            PackExpansionType e => Type(e.Pattern) + "...",
            _ => type.ToString(),
        };

        private string Constant(HirConstant constant) => constant switch
        {
            IntegerConstant i => $"{i.Value.ToString(CultureInfo.InvariantCulture)}{i.Type.Keyword()}",
            FloatConstant f => $"0x{f.Bits.ToString("x", CultureInfo.InvariantCulture)}{f.Type.Keyword()}",
            BoolConstant b => b.Value ? "true" : "false",
            CharConstant c => $"'\\u{c.Value:x}'",
            StringConstant s => Quote(s.Value),
            NullConstant => "null",
            TypeConstant t => $"typeof({Type(t.Type)})",
            EnumConstant e => $"{Type(e.Type)}({e.Value.ToString(CultureInfo.InvariantCulture)}{(e.Text is null ? "" : $" \"{e.Text}\"")})",
            ArrayConstant a => $"{Type(a.Element)}[{string.Join(", ", a.Items.Select(Constant))}]",
            _ => constant.ToString(),
        };

        // ── Statements ─────────────────────────────────────

        private void Statement(Stmt statement)
        {
            string at = Location(statement.At);
            switch (statement)
            {
                case BlockStmt block:
                    Line("{" + at);
                    Body(block.Statements);
                    Line("}");
                    break;
                case LocalStmt local:
                    Line($"let ${local.Local}{(local.Value is { } v ? " = " + Expr(v) : "")}{at}");
                    break;
                case ExprStmt e:
                    Line(Expr(e.Expression) + at);
                    break;
                case IfStmt s:
                    Line($"if {Expr(s.Condition)}{at}");
                    Nested(s.Then);
                    if (s.Else is { } otherwise)
                    {
                        Line("else");
                        Nested(otherwise);
                    }
                    break;
                case WhileStmt s:
                    Line($"{Label(s.Label)}while {Expr(s.Condition)}{at}");
                    Nested(s.Body);
                    break;
                case DoWhileStmt s:
                    Line($"{Label(s.Label)}do{at}");
                    Nested(s.Body);
                    Line($"while {Expr(s.Condition)}");
                    break;
                case ForStmt s:
                    Line($"{Label(s.Label)}for{at}");
                    indent++;
                    foreach (var init in s.Initializers)
                        Statement(init);
                    indent--;
                    Line($"cond {(s.Condition is { } c ? Expr(c) : "-")}; step {string.Join(", ", s.Steps.Select(Expr))}");
                    Nested(s.Body);
                    break;
                case ForeachStmt s:
                    Line($"{Label(s.Label)}foreach ${s.Local} in {Expr(s.Collection)}{at}");
                    Nested(s.Body);
                    break;
                case SwitchStmt s:
                    Line($"{Label(s.Label)}switch {Expr(s.Value)}{at}");
                    foreach (var section in s.Sections)
                    {
                        Line($"case {string.Join(" | ", section.Patterns.Select(Pattern))}{(section.Guard is { } g ? " when " + Expr(g) : "")}");
                        Nested(section.Body);
                    }
                    break;
                case BreakStmt s:
                    Line($"break{(s.Label >= 0 ? " L" + s.Label : "")}{at}");
                    break;
                case ContinueStmt s:
                    Line($"continue{(s.Label >= 0 ? " L" + s.Label : "")}{at}");
                    break;
                case ReturnStmt s:
                    Line($"return{(s.Value is { } rv ? " " + Expr(rv) : "")}{at}");
                    break;
                case ThrowStmt s:
                    Line($"throw{(s.Value is { } tv ? " " + Expr(tv) : "")}{at}");
                    break;
                case TryStmt s:
                    Line("try" + at);
                    Nested(s.Body);
                    foreach (var clause in s.Catches)
                    {
                        Line($"catch {(clause.Type is { } ct ? Type(ct) : "any")}{(clause.Local >= 0 ? " $" + clause.Local : "")}{(clause.Filter is { } f ? " when " + Expr(f) : "")}");
                        Nested(clause.Body);
                    }
                    if (s.Finally is { } fin)
                    {
                        Line("finally");
                        Nested(fin);
                    }
                    break;
                case UsingStmt s:
                    Line($"using ${s.Local} = {Expr(s.Resource)}{at}");
                    Nested(s.Body);
                    break;
                case DeleteStmt s:
                    Line($"delete {Expr(s.Value)}{at}");
                    break;
                case RegionStmt s:
                    Line("region" + at);
                    Nested(s.Body);
                    break;
                case UnsafeStmt s:
                    Line("unsafe" + at);
                    Nested(s.Body);
                    break;
                case CheckedStmt s:
                    Line((s.Checked ? "checked" : "unchecked") + at);
                    Nested(s.Body);
                    break;
                case YieldStmt s:
                    Line((s.Value is { } yv ? "yield " + Expr(yv) : "yield break") + at);
                    break;
                default:
                    Line(statement.ToString());
                    break;
            }
        }

        private static string Label(int label) => label >= 0 ? $"L{label}: " : "";

        private void Body(Stmt[] statements)
        {
            indent++;
            foreach (var statement in statements)
                Statement(statement);
            indent--;
        }

        private void Nested(Stmt statement)
        {
            if (statement is BlockStmt block && !block.At.IsKnown)
            {
                Line("{");
                Body(block.Statements);
                Line("}");
            }
            else
            {
                indent++;
                Statement(statement);
                indent--;
            }
        }

        // ── Expressions ────────────────────────────────────

        private string Typed(string text, HirType type) => types ? $"{text}:{Type(type)}" : text;

        private string List(IEnumerable<Expr> expressions) => string.Join(", ", expressions.Select(Expr));

        private string TypeArguments(HirType[] arguments) => arguments.Length == 0 ? "" : $"<{string.Join(", ", arguments.Select(Type))}>";

        private string Expr(Expr e) => e switch
        {
            LiteralExpr l => Typed(Constant(l.Value), l.Type),
            LocalExpr l => Typed($"${l.Local}", l.Type),
            ParamExpr p => Typed($"%{p.Parameter}", p.Type),
            ThisExpr t => Typed("this", t.Type),
            FieldExpr f => Typed($"{(f.Instance is { } i ? Expr(i) + "." : "")}{Member(f.Field, MemberKind.Field)}", f.Type),
            PropertyExpr p => Typed($"{(p.Instance is { } i ? Expr(i) + "." : "")}{Member(p.Property, MemberKind.Property)}{(p.Indices.Length > 0 ? $"[{List(p.Indices)}]" : "")}", p.Type),
            ElementExpr x => Typed($"{Expr(x.Array)}[{List(x.Indices)}]", x.Type),
            CallExpr c => Typed($"{(c.NonVirtual ? "base " : "")}{(c.Receiver is { } r ? Expr(r) + "." : "")}{Member(c.Method, MemberKind.Method)}{TypeArguments(c.TypeArguments)}({List(c.Arguments)})", c.Type),
            InvokeExpr i => Typed($"invoke {Expr(i.Function)}({List(i.Arguments)})", i.Type),
            NewExpr n => Typed($"new {Type(n.Type)}{(n.Constructor is { } k ? " " + Member(k, MemberKind.Method) : "")}({List(n.Arguments)}){Inits(n.Initializers)}", n.Type),
            NewArrayExpr n => Typed($"new {Type(n.Element)}[{List(n.Lengths)}]", n.Type),
            ArrayLiteralExpr a => Typed($"{Type(a.Element)}[{List(a.Items)}]", a.Type),
            UnaryExpr u => Typed($"({Unary(u.Op, Expr(u.Operand))})", u.Type),
            BinaryExpr b => Typed($"({Expr(b.Left)} {Binary(b.Op)} {Expr(b.Right)})", b.Type),
            AssignExpr a => Typed($"({Expr(a.Target)} {(a.Compound is { } op ? Binary(op) : "")}= {Expr(a.Value)})", a.Type),
            ConvertExpr c => Typed($"{Lower(c.Kind)}<{Type(c.Type)}>{(c.Method is { } m ? "[" + Member(m, MemberKind.Method) + "]" : "")}({Expr(c.Value)})", c.Type),
            IsExpr i => Typed($"({Expr(i.Value)} is {Pattern(i.Pattern)})", i.Type),
            AsExpr a => Typed($"({Expr(a.Value)} as {Type(a.Type)})", a.Type),
            SwitchExpr s => Typed($"{Expr(s.Value)} switch {{ {string.Join("; ", s.Arms.Select(a => $"{Pattern(a.Pattern)}{(a.Guard is { } g ? " when " + Expr(g) : "")} => {Expr(a.Value)}"))} }}", s.Type),
            ConditionalExpr c => Typed($"({Expr(c.Condition)} ? {Expr(c.Then)} : {Expr(c.Else)})", c.Type),
            CoalesceExpr c => Typed($"({Expr(c.Left)} ?? {Expr(c.Right)})", c.Type),
            ConditionalAccessExpr c => Typed($"({Expr(c.Receiver)} ?-> {Expr(c.Access)})", c.Type),
            ReceiverExpr r => Typed("<receiver>", r.Type),
            LambdaExpr l => Typed($"{(l.Async ? "async " : "")}lambda({string.Join(", ", l.Parameters.Select(p => "$" + p))}) {Inline(l.Body)}", l.Type),
            MethodRefExpr m => Typed($"&{(m.Target is { } t ? Expr(t) + "." : "")}{Member(m.Method, MemberKind.Method)}{TypeArguments(m.TypeArguments)}", m.Type),
            AwaitExpr a => Typed($"await {Expr(a.Task)}", a.Type),
            TypeOfExpr t => Typed($"typeof({Type(t.Operand)})", t.Type),
            SizeOfExpr s => Typed($"sizeof({Type(s.Operand)})", s.Type),
            ResourceExpr r => Typed($"$resource(\"{r.Name}\")", r.Type),
            ArrayLengthExpr l => Typed(l.Dimension is { } d ? $"len {Expr(l.Array)} dim {Expr(d)}" : $"len {Expr(l.Array)}", l.Type),
            DefaultExpr d => Typed("default", d.Type),
            WithExpr w => Typed($"({Expr(w.Value)} with{Inits(w.Changes)})", w.Type),
            RangeExpr r => Typed($"({(r.Low is { } lo ? Expr(lo) : "")}{(r.Inclusive ? "..=" : "..")}{(r.High is { } hi ? Expr(hi) : "")})", r.Type),
            InterpolatedExpr i => Typed($"$({string.Join(", ", i.Parts.Select(p => Expr(p.Value) + (p.Alignment != 0 ? "," + p.Alignment : "") + (p.Format is { } f ? ":" + Quote(f) : "")))})", i.Type),
            BorrowExpr b => Typed($"{(b.Mutable ? "&mut " : "&")}{Expr(b.Place)}", b.Type),
            AddressOfExpr a => Typed($"addr {Expr(a.Place)}", a.Type),
            DerefExpr d => Typed($"*{Expr(d.Pointer)}", d.Type),
            CheckedExpr c => Typed($"{(c.Checked ? "checked" : "unchecked")}({Expr(c.Value)})", c.Type),
            BlockExpr b => Typed($"{{ {string.Join(" ", b.Statements.Select(Inline))} => {Expr(b.Value)} }}", b.Type),
            _ => e.ToString(),
        };

        private string Inits(MemberInit[] inits) => inits.Length == 0 ? "" : $" {{ {string.Join(", ", inits.Select(i => $"{Member(i.Member, i.Kind)} = {Expr(i.Value)}"))} }}";

        /// <summary>A statement on one line (lambda bodies, block expressions).</summary>
        private string Inline(Stmt statement)
        {
            var printer = new HirPrinter(assembly, types);
            printer.Statement(statement);
            return string.Join(" ", printer.text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        }

        private static string Unary(UnaryOp op, string operand) => op switch
        {
            UnaryOp.Negate => "-" + operand,
            UnaryOp.Plus => "+" + operand,
            UnaryOp.Not => "!" + operand,
            UnaryOp.BitNot => "~" + operand,
            UnaryOp.PreIncrement => "++" + operand,
            UnaryOp.PreDecrement => "--" + operand,
            UnaryOp.PostIncrement => operand + "++",
            _ => operand + "--",
        };

        private static string Binary(BinaryOp op) => op switch
        {
            BinaryOp.Add => "+", BinaryOp.Subtract => "-", BinaryOp.Multiply => "*", BinaryOp.Divide => "/",
            BinaryOp.Remainder => "%", BinaryOp.Power => "**",
            BinaryOp.BitAnd => "&", BinaryOp.BitOr => "|", BinaryOp.BitXor => "^", BinaryOp.ShiftLeft => "<<", BinaryOp.ShiftRight => ">>",
            BinaryOp.Equal => "==", BinaryOp.NotEqual => "!=", BinaryOp.Less => "<", BinaryOp.LessEqual => "<=",
            BinaryOp.Greater => ">", BinaryOp.GreaterEqual => ">=",
            BinaryOp.And => "&&", _ => "||",
        };

        private string Pattern(Pattern pattern) => pattern switch
        {
            DiscardPattern => "_",
            ConstantPattern c => Constant(c.Value),
            RelationalPattern r => $"{Binary(r.Op)} {Constant(r.Value)}",
            RangePattern r => $"{Constant(r.Low)}{(r.Inclusive ? "..=" : "..")}{Constant(r.High)}",
            TypePattern t => Type(t.Type),
            BindingPattern b => $"${b.Local}{(b.Inner is { } i ? " @ " + Pattern(i) : "")}",
            AndPattern a => $"({Pattern(a.Left)} and {Pattern(a.Right)})",
            OrPattern o => $"({Pattern(o.Left)} or {Pattern(o.Right)})",
            NotPattern n => $"not {Pattern(n.Operand)}",
            PropertyPattern p => $"{(p.Type is { } t ? Type(t) + " " : "")}{{ {string.Join(", ", p.Members.Select(m => $"{Member(m.Member, m.Kind)}: {Pattern(m.Pattern)}"))} }}",
            PositionalPattern p => $"{(p.Type is { } t ? Type(t) : "")}{(p.Deconstructor is { } d ? "[" + Member(d, MemberKind.Method) + "]" : "")}({string.Join(", ", p.Items.Select(Pattern))})",
            ListPattern l => $"[{string.Join(", ", l.Items.Select((item, i) => (i == l.Slice ? ".." + (l.SliceLocal >= 0 ? "$" + l.SliceLocal + " " : "") : "") + Pattern(item)))}{(l.Slice == l.Items.Length ? ", .." + (l.SliceLocal >= 0 ? "$" + l.SliceLocal : "") : "")}]",
            _ => pattern.ToString(),
        };
    }
}
