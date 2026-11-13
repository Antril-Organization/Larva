using Larva.Hir.Lowering;
using Larva.IR;
using Larva.Runtime.Vm;
using static Larva.Hir.Tests.E;

namespace Larva.Hir.Tests;

/// <summary>
/// The resources' bytes are one static blob for the module, with one table of where each starts, however many of the
/// resource services (<c>$resources.bytes</c>, <c>length</c>, <c>data</c>) the program lowers; <c>$resource("name")</c>
/// copies its resource's own bytes. The services and <c>$resource</c> give the right bytes on the VM.
/// </summary>
public sealed class ResourceTests : CheckedTest
{
    [Fact]
    public void Resource_services_share_one_blob_and_give_each_resources_bytes()
    {
        var p = new HirProgram("Res");
        var bytesType = new ArrayType(HirType.U8);
        var (r, rDef) = p.Type("R", TypeKind.Class, flags: TypeFlags.Static | TypeFlags.Abstract);
        MemberToken Service(string kind, HirType result)
        {
            var token = HirProgram.Method(r, rDef, kind, result, MethodFlags.Static | MethodFlags.Extern, MethodKind.Normal, HirProgram.P("index", I));
            rDef.Methods[^1].ExternName = "$resources." + kind;
            return token;
        }
        var bytesOf = Service("bytes", bytesType);
        var lengthOf = Service("length", I);
        var dataOf = Service("data", new PointerType(HirType.U8));
        rDef.Methods[^1].Flags |= MethodFlags.Unsafe;
        Expr Byte(Expr array, int at) => new ConvertExpr(new ElementExpr(array, [Int(at)], HirType.U8), ConversionKind.Numeric, I);
        // Main() : i32 { u8[] a = bytes(1); u8[] c = $resource("b"); return length(0) * 1000 + a.Length * 100 + a[2] + c[3] * 10000; }
        var main = HirProgram.Method(r, rDef, "Main", I, MethodFlags.Static);
        {
            var m = new MethodBuilder(rDef.Methods[^1]);
            var a = m.Local("a", bytesType);
            var c = m.Local("c", bytesType);
            m.Do(Let(a, Call(bytesOf, bytesType, Int(1))),
                Let(c, new ResourceExpr("b", bytesType)),
                Ret(Add(Add(Add(Mul(Call(lengthOf, I, Int(0)), Int(1000)), Mul(new ArrayLengthExpr(a, I), Int(100))), Byte(a, 2)), Mul(Byte(c, 3), Int(10000))))).Done();
        }
        p.Assembly.EntryPoint = main;
        p.Assembly.Resources.Add(new HirResource("a", [1, 2, 3]));
        p.Assembly.Resources.Add(new HirResource("b", [7, 8, 9, 10]));

        var lowered = HirLowerer.Lower(p.Assembly);
        Check("the program lowers", lowered.Success, string.Join("; ", lowered.Errors));
        Require();
        var data = lowered.Module!.Data;
        Check("one blob of every resource's bytes (and the filler byte), for the three services", data.Count(d => d.Element == IrType.U8 && d.Values.Length == 8) == 1,
            string.Join(" ", data.Select(d => d.ToString())));
        Check("one table of starts", data.Count(d => d.Element == IrType.U64 && d.Values.Length == 3) == 1);
        Check("$resource(\"b\") has b's bytes", data.Any(d => d.Element == IrType.U8 && d.Values.Length == 4 && d.Values[3] == 10));
        var vm = Compiler.Compile(lowered.Module!);
        Check("the IR passes the type and borrow checks", vm.Il is not null, string.Join("; ", vm.TypeErrors.Concat<object>(vm.Diagnostics).Take(6)));
        if (vm.Il is null)
            return;
        var result = new Interpreter(vm.Il).Run("Res.R.Main");
        Check("Main() = 103409: length(0) = 3, bytes(1) = [7, 8, 9, 10], $resource(\"b\")[3] = 10", Equals(result, 103409L), $"{result}");
    }
}
