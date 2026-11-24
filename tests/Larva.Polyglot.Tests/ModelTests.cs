using Larva.Hir;
using Larva.Hir.Services;

namespace Larva.Polyglot.Tests;

/// <summary>
/// The model of a small library made in HIR (as a frontend would): its types and members named in every language,
/// overloads, reserved words, renames, and what two declarations with one name give.
/// </summary>
public sealed class ModelTests
{
    private static readonly HirType I32 = HirType.I32, I64 = HirType.Of(Primitive.I64), F64 = HirType.Of(Primitive.F64), Bool = HirType.Bool;

    /// <summary>
    /// <c>Acme.Json</c>: class <c>Parser</c> (a constructor, <c>Parse</c> overloaded by number width, a property
    /// <c>IsStrict</c>, a read-only <c>Depth</c>, a constant, a <c>+</c> operator), interface <c>IReader</c>, enum
    /// <c>Mode</c>, and the namespace's functions (<c>Load(from)</c>).
    /// </summary>
    private static HirAssembly Library(Action<HirAssembly>? more = null)
    {
        var assembly = new HirAssembly { Name = "Acme.Json" };
        var parser = new TypeDef { Namespace = "Acme.Json", Name = "Parser", Kind = TypeKind.Class };
        var parserToken = assembly.Add(parser);
        var self = new NamedType(parserToken, []);
        parser.AddMethod(new MethodDef(".new", HirType.Void, new Parameter("maxDepth", I32)) { Kind = MethodKind.Constructor });
        parser.AddMethod(new MethodDef("Parse", I32, new Parameter("value", I32)));
        parser.AddMethod(new MethodDef("Parse", I32, new Parameter("value", I64)));
        int getStrict = parser.AddMethod(new MethodDef("get_IsStrict", Bool) { Kind = MethodKind.Getter });
        int setStrict = parser.AddMethod(new MethodDef("set_IsStrict", HirType.Void, new Parameter("value", Bool)) { Kind = MethodKind.Setter });
        parser.Properties.Add(new PropertyDef("IsStrict", Bool) { Getter = getStrict, Setter = setStrict });
        int getDepth = parser.AddMethod(new MethodDef("get_Depth", I32) { Kind = MethodKind.Getter });
        parser.Properties.Add(new PropertyDef("Depth", I32) { Getter = getDepth });
        parser.AddField(new FieldDef("MaxDepth", I32) { Flags = FieldFlags.Const, Value = new IntegerConstant(64, Primitive.I32) });
        int plus = parser.AddMethod(new MethodDef("$op_add", self, new Parameter("a", self), new Parameter("b", self)) { Kind = MethodKind.Operator, Flags = MethodFlags.Static });
        parser.Operators.Add(new OperatorDef("+", Fixity.Binary, plus));

        var reader = new TypeDef { Namespace = "Acme.Json", Name = "IReader", Kind = TypeKind.Interface };
        reader.AddMethod(new MethodDef("ReadNext", Bool) { Flags = MethodFlags.Abstract });
        assembly.Add(reader);

        var mode = new TypeDef { Namespace = "Acme.Json", Name = "Mode", Kind = TypeKind.Enum, Underlying = I32 };
        var modeType = new NamedType(assembly.Add(mode), []);
        mode.AddField(new FieldDef("ReadOnly", modeType) { Flags = FieldFlags.Const | FieldFlags.Static, Value = new IntegerConstant(0, Primitive.I32) });
        mode.AddField(new FieldDef("ReadWrite", modeType) { Flags = FieldFlags.Const | FieldFlags.Static, Value = new IntegerConstant(1, Primitive.I32) });

        var module = new TypeDef { Namespace = "Acme.Json", Name = "$Module", Kind = TypeKind.Class, Flags = TypeFlags.Static };
        module.AddMethod(new MethodDef("Load", I32, new Parameter("from", I32)) { Flags = MethodFlags.Static });
        module.AddField(new FieldDef("DefaultDepth", I32) { Flags = FieldFlags.Const, Value = new IntegerConstant(32, Primitive.I32) });
        assembly.Add(module);

        var hidden = new TypeDef { Namespace = "Acme.Json", Name = "Internals", Kind = TypeKind.Class, Access = Access.Internal };
        assembly.Add(hidden);
        more?.Invoke(assembly);
        return assembly;
    }

    private static PolyglotModel Model(HirAssembly assembly, PolyglotOptions? options = null)
    {
        var catalog = HirCatalog.Create(assembly);
        return PolyglotModel.Build(catalog, catalog.Assemblies[0], options);
    }

    private static PolyglotType Type(PolyglotModel model, string name) => model.Types.Single(t => t.SourceName == name);

    private static PolyglotMember Member(PolyglotModel model, string type, string name) => Type(model, type).Members.First(m => m.Name == name);

    private static string Names(PolyglotMember member, TargetLanguage language) => string.Join("/", member.Names[language]);

    [Fact]
    public void Only_what_is_public_is_offered()
    {
        var model = Model(Library());
        Assert.DoesNotContain(model.Types, t => t.Source.Name == "Internals");
        Assert.Equal(["Acme.Json", "Acme.Json.IReader", "Acme.Json.Mode", "Acme.Json.Parser"], model.Types.Select(t => t.SourceName).Order());
        Assert.False(model.HasErrors, string.Join("\n", model.Diagnostics));
    }

    [Fact]
    public void Types_namespaces_and_interfaces_in_each_language()
    {
        var model = Model(Library());
        var parser = Type(model, "Acme.Json.Parser");
        Assert.Equal("acme_json_parser_t", parser.Names[TargetLanguage.C]);
        Assert.Equal("Parser", parser.Names[TargetLanguage.CSharp]);
        Assert.Equal("acme.json", parser.Namespaces[TargetLanguage.Python]);
        Assert.Equal("acme.json", parser.Namespaces[TargetLanguage.Java]);
        Assert.Equal("acme/json", parser.Namespaces[TargetLanguage.TypeScript]);
        Assert.Equal("Acme.Json", parser.Namespaces[TargetLanguage.CSharp]);

        var reader = Type(model, "Acme.Json.IReader");
        Assert.Equal("IReader", reader.Names[TargetLanguage.CSharp]);
        Assert.Equal("Reader", reader.Names[TargetLanguage.Python]);
        Assert.Equal("Reader", reader.Names[TargetLanguage.Java]);
        Assert.Equal("Reader", reader.Names[TargetLanguage.TypeScript]);

        var module = Type(model, "Acme.Json");
        Assert.Equal("JsonFunctions", module.Names[TargetLanguage.CSharp]);
        Assert.Equal("JsonFunctions", module.Names[TargetLanguage.Java]);
        Assert.Equal("", module.Names[TargetLanguage.Python]);
    }

    [Fact]
    public void An_interface_keeps_its_I_where_the_bare_name_is_taken()
    {
        var model = Model(Library(a => a.Add(new TypeDef { Namespace = "Acme.Json", Name = "Reader", Kind = TypeKind.Class })));
        Assert.Equal("IReader", Type(model, "Acme.Json.IReader").Names[TargetLanguage.Python]);
        Assert.Equal("Reader", Type(model, "Acme.Json.Reader").Names[TargetLanguage.Python]);
        Assert.False(model.HasErrors, string.Join("\n", model.Diagnostics));
    }

    [Fact]
    public void Members_in_each_language()
    {
        var model = Model(Library());
        var strict = Member(model, "Acme.Json.Parser", "IsStrict");
        Assert.Equal("acme_json_parser_get_is_strict/acme_json_parser_set_is_strict", Names(strict, TargetLanguage.C));
        Assert.Equal("IsStrict", Names(strict, TargetLanguage.CSharp));
        Assert.Equal("is_strict", Names(strict, TargetLanguage.Python));
        Assert.Equal("isStrict/setStrict", Names(strict, TargetLanguage.Java));
        Assert.Equal("isStrict", Names(strict, TargetLanguage.TypeScript));

        var depth = Member(model, "Acme.Json.Parser", "Depth");
        Assert.Equal("getDepth", Names(depth, TargetLanguage.Java));
        Assert.Equal("acme_json_parser_get_depth", Names(depth, TargetLanguage.C));

        var max = Member(model, "Acme.Json.Parser", "MaxDepth");
        Assert.Equal("ACME_JSON_PARSER_MAX_DEPTH", Names(max, TargetLanguage.C));
        Assert.Equal("MaxDepth", Names(max, TargetLanguage.CSharp));
        Assert.Equal("MAX_DEPTH", Names(max, TargetLanguage.Python));

        var constructor = Member(model, "Acme.Json.Parser", "new");
        Assert.Equal("acme_json_parser_new", Names(constructor, TargetLanguage.C));
        Assert.Equal("Parser", Names(constructor, TargetLanguage.CSharp));
        Assert.Equal("__init__", Names(constructor, TargetLanguage.Python));
        Assert.Equal("constructor", Names(constructor, TargetLanguage.TypeScript));
        Assert.Equal(["max_depth"], constructor.Parameters[TargetLanguage.Python]);
        Assert.Equal(["maxDepth"], constructor.Parameters[TargetLanguage.Java]);

        var read = Member(model, "Acme.Json.Mode", "ReadOnly");
        Assert.Equal("ACME_JSON_MODE_READ_ONLY", Names(read, TargetLanguage.C));
        Assert.Equal("READ_ONLY", Names(read, TargetLanguage.Python));
        Assert.Equal("ReadOnly", Names(read, TargetLanguage.TypeScript));

        var plus = Member(model, "Acme.Json.Parser", "operator +");
        Assert.Equal("operator +", Names(plus, TargetLanguage.CSharp));
        Assert.Equal("__add__", Names(plus, TargetLanguage.Python));
        Assert.Equal("add", Names(plus, TargetLanguage.Java));
        Assert.Equal("acme_json_parser_op_add", Names(plus, TargetLanguage.C));
    }

    [Fact]
    public void A_namespaces_functions_and_reserved_words()
    {
        var model = Model(Library());
        var load = Member(model, "Acme.Json", "Load");
        Assert.Equal("acme_json_load", Names(load, TargetLanguage.C));
        Assert.Equal("Load", Names(load, TargetLanguage.CSharp));
        Assert.Equal("load", Names(load, TargetLanguage.Python));
        Assert.Equal(["from_"], load.Parameters[TargetLanguage.Python]);
        Assert.Equal(["from"], load.Parameters[TargetLanguage.CSharp]);
        Assert.Equal(["from"], load.Parameters[TargetLanguage.Java]);
        Assert.Equal("ACME_JSON_DEFAULT_DEPTH", Names(Member(model, "Acme.Json", "DefaultDepth"), TargetLanguage.C));
    }

    [Fact]
    public void Overloads_by_number_width_share_one_entry_in_dispatching_languages_and_take_suffixes_in_C()
    {
        var model = Model(Library());
        var parses = Type(model, "Acme.Json.Parser").Members.Where(m => m.Name == "Parse").ToList();
        Assert.Equal(2, parses.Count);
        Assert.All(parses, p => Assert.Equal("parse", Names(p, TargetLanguage.Python)));
        Assert.All(parses, p => Assert.Contains(TargetLanguage.Python, p.ValueDispatch));
        // In TypeScript an i32 is a number and an i64 a bigint: told apart without looking at the value.
        Assert.All(parses, p => Assert.DoesNotContain(TargetLanguage.TypeScript, p.ValueDispatch));
        Assert.All(parses, p => Assert.Equal("Parse", Names(p, TargetLanguage.CSharp)));
        Assert.Equal("acme_json_parser_parse", Names(parses[0], TargetLanguage.C));
        Assert.Equal("acme_json_parser_parse_i64", Names(parses[1], TargetLanguage.C));
        Assert.DoesNotContain(model.Diagnostics, d => d.Code == "PG0103");
    }

    [Fact]
    public void Overloads_a_dispatching_language_cannot_tell_apart_are_an_error()
    {
        var model = Model(Library(a =>
        {
            var parser = a.Types.First(t => t.Name == "Parser");
            var mode = new NamedType(TypeToken.Def(a.Types.FindIndex(t => t.Name == "Mode")), []);
            parser.AddMethod(new MethodDef("Open", Bool, new Parameter("mode", mode)));
            parser.AddMethod(new MethodDef("Open", Bool, new Parameter("flags", I32)));
        }));
        // Python tells an IntEnum from an int; TypeScript's enums are numbers.
        var errors = model.Diagnostics.Where(d => d.Code == "PG0103").ToList();
        Assert.Single(errors);
        Assert.Contains("typescript", errors[0].Message);
        Assert.Contains("Acme.Json.Parser.Open(Mode)", errors[0].Message);
    }

    [Fact]
    public void Two_names_that_meet_after_shaping_are_an_error()
    {
        var model = Model(Library(a =>
        {
            var parser = a.Types.First(t => t.Name == "Parser");
            parser.AddMethod(new MethodDef("GetValue", I32));
            parser.AddMethod(new MethodDef("getValue", I32));
        }));
        var errors = model.Diagnostics.Where(d => d.Code == "PG0101").ToList();
        // Every language joins them: Pascal, camel and snake case alike.
        Assert.Contains(errors, e => e.Message.Contains("`get_value` in python"));
        Assert.Contains(errors, e => e.Message.Contains("`getValue` in java"));
        Assert.Contains(errors, e => e.Message.Contains("`GetValue` in csharp"));
        Assert.True(model.HasErrors);
    }

    [Fact]
    public void A_property_getter_and_a_method_of_the_same_name_meet_in_java()
    {
        var model = Model(Library(a => a.Types.First(t => t.Name == "Parser").AddMethod(new MethodDef("GetDepth", I32))));
        Assert.Contains(model.Diagnostics, d => d.Code == "PG0101" && d.Message.Contains("`getDepth` in java"));
        Assert.DoesNotContain(model.Diagnostics, d => d.Code == "PG0101" && d.Message.Contains("python"));
    }

    [Fact]
    public void An_operator_is_not_offered_twice_where_a_method_already_has_its_name()
    {
        var model = Model(Library(a => a.Types.First(t => t.Name == "Parser").AddMethod(new MethodDef("Add", I32, new Parameter("other", I32)))));
        var plus = Member(model, "Acme.Json.Parser", "operator +");
        Assert.Empty(plus.Names[TargetLanguage.Java]);
        Assert.Equal("__add__", Names(plus, TargetLanguage.Python));
        Assert.False(model.HasErrors, string.Join("\n", model.Diagnostics));
    }

    [Fact]
    public void A_member_that_would_hide_what_every_object_has_is_escaped()
    {
        var model = Model(Library(a =>
        {
            var parser = a.Types.First(t => t.Name == "Parser");
            int get = parser.AddMethod(new MethodDef("get_Class", I32) { Kind = MethodKind.Getter });
            parser.Properties.Add(new PropertyDef("Class", I32) { Getter = get });
        }));
        var member = Member(model, "Acme.Json.Parser", "Class");
        Assert.Equal("getClass_", Names(member, TargetLanguage.Java));
        Assert.Equal("class_", Names(member, TargetLanguage.Python));
        Assert.Equal("Class", Names(member, TargetLanguage.CSharp));
        Assert.Contains(model.Diagnostics, d => d.Code == "PG0106" && !d.Error);
    }

    [Fact]
    public void Renames_come_from_the_options_and_a_setter_follows_its_getter()
    {
        var model = Model(Library(), new PolyglotOptions
        {
            Rename = (language, kind, source, proposed) => (language, source) switch
            {
                (TargetLanguage.Python, "Acme.Json.Load") => "loads",
                (TargetLanguage.Java, "Acme.Json.Parser.IsStrict") => "isPedantic",
                _ => proposed,
            },
        });
        Assert.Equal("loads", Names(Member(model, "Acme.Json", "Load"), TargetLanguage.Python));
        Assert.Equal("Load", Names(Member(model, "Acme.Json", "Load"), TargetLanguage.CSharp));
        Assert.Equal("isPedantic/setPedantic", Names(Member(model, "Acme.Json.Parser", "IsStrict"), TargetLanguage.Java));
    }

    [Fact]
    public void Options_choose_languages_acronyms_and_the_java_package()
    {
        var model = Model(Library(a => a.Add(new TypeDef { Namespace = "Acme.Json", Name = "HTTPSource", Kind = TypeKind.Class })), new PolyglotOptions
        {
            Languages = [TargetLanguage.CSharp, TargetLanguage.Java],
            Acronyms = new Dictionary<TargetLanguage, AcronymPolicy> { [TargetLanguage.CSharp] = AcronymPolicy.ShortUpper },
            JavaPackageRoot = "com.acme",
        });
        Assert.Equal([TargetLanguage.CSharp, TargetLanguage.Java], model.Targets.Select(t => t.Language));
        var source = Type(model, "Acme.Json.HTTPSource");
        Assert.Equal("HttpSource", source.Names[TargetLanguage.CSharp]);
        Assert.Equal("HTTPSource", source.Names[TargetLanguage.Java]);
        Assert.Equal("com.acme.acme.json", source.Namespaces[TargetLanguage.Java]);
    }

    [Fact]
    public void The_names_table_has_a_row_per_declaration_and_a_column_per_language()
    {
        var table = NamesTable.Write(Model(Library(), new PolyglotOptions { Languages = [TargetLanguage.Python, TargetLanguage.Java] }), "Acme.Json.Parser");
        var lines = table.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("python", lines[0]);
        Assert.Contains("java", lines[0]);
        Assert.Contains(lines, l => l.Contains("Acme.Json.Parser.IsStrict") && l.Contains("is_strict") && l.Contains("isStrict/setStrict"));
        Assert.DoesNotContain(lines, l => l.Contains("Acme.Json.Mode"));
    }
}
