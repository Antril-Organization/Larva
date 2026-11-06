using System.Text;

namespace Larva.Wasm
{
    /// <summary>WebAssembly value types.</summary>
    public enum ValType : byte { I32 = 0x7F, I64 = 0x7E, F32 = 0x7D, F64 = 0x7C }

    /// <summary>A function type: parameters and results.</summary>
    public sealed record FuncType(ValType[] Parameters, ValType[] Results)
    {
        public bool Equals(FuncType? other) => other is not null && Parameters.SequenceEqual(other.Parameters) && Results.SequenceEqual(other.Results);
        public override int GetHashCode() => HashCode.Combine(Parameters.Length, Results.Length, Parameters.FirstOrDefault(), Results.FirstOrDefault());
    }

    /// <summary>
    /// Builds a WebAssembly module in the binary format (version 1, with bulk memory and the non-trapping float
    /// conversions): types, imported functions, one memory and one function table (both exported), globals, exports,
    /// function bodies and active data segments.
    /// </summary>
    public sealed class WasmModule
    {
        private readonly List<FuncType> types = [];
        private readonly List<(string Module, string Name, int Type)> imports = [];
        private readonly List<int> functionTypes = [];
        private readonly List<byte[]> bodies = [];
        private readonly List<(ValType Type, bool Mutable, long Value)> globals = [];
        private readonly List<(string Name, byte Kind, int Index)> exports = [];
        private readonly List<(int Address, byte[] Bytes)> data = [];

        /// <summary>Functions placed in the table (from index 1; 0 stays null), by function index.</summary>
        public List<int> Table { get; } = [];

        public int MemoryPages { get; set; } = 1;

        public int Type(FuncType type)
        {
            int index = types.IndexOf(type);
            if (index >= 0)
                return index;
            types.Add(type);
            return types.Count - 1;
        }

        /// <summary>Imports a function; imports come before defined functions in the index space, so add them first.</summary>
        public int Import(string module, string name, FuncType type)
        {
            if (functionTypes.Count > 0)
                throw new InvalidOperationException("imports must be added before functions");
            imports.Add((module, name, Type(type)));
            return imports.Count - 1;
        }

        public int ImportCount => imports.Count;

        /// <summary>Declares a function (its body comes later) and returns its index.</summary>
        public int Declare(FuncType type)
        {
            functionTypes.Add(Type(type));
            bodies.Add([]);
            return imports.Count + functionTypes.Count - 1;
        }

        /// <summary>A function body: local declarations and code, without the trailing <c>end</c> (added here).</summary>
        public void Define(int function, IReadOnlyList<ValType> locals, byte[] code)
        {
            var body = new Bytes();
            var groups = new List<(int Count, ValType Type)>();
            foreach (var local in locals)
            {
                if (groups.Count > 0 && groups[^1].Type == local)
                    groups[^1] = (groups[^1].Count + 1, local);
                else
                    groups.Add((1, local));
            }
            body.U32((uint)groups.Count);
            foreach (var (count, type) in groups)
            {
                body.U32((uint)count);
                body.Byte((byte)type);
            }
            body.Raw(code);
            body.Byte(0x0B);
            bodies[function - imports.Count] = body.ToArray();
        }

        public int Global(ValType type, bool mutable, long value)
        {
            globals.Add((type, mutable, value));
            return globals.Count - 1;
        }

        public void ExportFunction(string name, int function) => exports.Add((name, 0, function));
        public void ExportGlobal(string name, int global) => exports.Add((name, 3, global));

        public void Data(int address, byte[] bytes) => data.Add((address, bytes));

        public byte[] ToArray()
        {
            var module = new Bytes();
            module.Raw([0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00]);

            Section(module, 1, s =>
            {
                s.U32((uint)types.Count);
                foreach (var type in types)
                {
                    s.Byte(0x60);
                    s.U32((uint)type.Parameters.Length);
                    foreach (var p in type.Parameters)
                        s.Byte((byte)p);
                    s.U32((uint)type.Results.Length);
                    foreach (var r in type.Results)
                        s.Byte((byte)r);
                }
            });
            if (imports.Count > 0)
            {
                Section(module, 2, s =>
                {
                    s.U32((uint)imports.Count);
                    foreach (var (moduleName, name, type) in imports)
                    {
                        s.Name(moduleName);
                        s.Name(name);
                        s.Byte(0x00);
                        s.U32((uint)type);
                    }
                });
            }
            Section(module, 3, s =>
            {
                s.U32((uint)functionTypes.Count);
                foreach (int type in functionTypes)
                    s.U32((uint)type);
            });
            Section(module, 4, s =>
            {
                s.U32(1);
                s.Byte(0x70);                       // funcref
                s.Byte(0x00);                       // min only
                s.U32((uint)(Table.Count + 1));
            });
            Section(module, 5, s =>
            {
                s.U32(1);
                s.Byte(0x00);
                s.U32((uint)MemoryPages);
            });
            if (globals.Count > 0)
            {
                Section(module, 6, s =>
                {
                    s.U32((uint)globals.Count);
                    foreach (var (type, mutable, value) in globals)
                    {
                        s.Byte((byte)type);
                        s.Byte(mutable ? (byte)1 : (byte)0);
                        if (type == ValType.I32)
                        {
                            s.Byte(0x41);
                            s.S64((int)value);
                        }
                        else
                        {
                            s.Byte(0x42);
                            s.S64(value);
                        }
                        s.Byte(0x0B);
                    }
                });
            }
            Section(module, 7, s =>
            {
                s.U32((uint)exports.Count + 2);
                s.Name("memory");
                s.Byte(0x02);
                s.U32(0);
                s.Name("table");
                s.Byte(0x01);
                s.U32(0);
                foreach (var (name, kind, index) in exports)
                {
                    s.Name(name);
                    s.Byte(kind);
                    s.U32((uint)index);
                }
            });
            if (Table.Count > 0)
            {
                Section(module, 9, s =>
                {
                    s.U32(1);
                    s.Byte(0x00);                   // active, table 0, funcref
                    s.Byte(0x41);
                    s.S64(1);
                    s.Byte(0x0B);
                    s.U32((uint)Table.Count);
                    foreach (int function in Table)
                        s.U32((uint)function);
                });
            }
            if (data.Count > 0)
                Section(module, 12, s => s.U32((uint)data.Count)); // data count: needed by memory.copy / memory.fill validation
            else
                Section(module, 12, s => s.U32(0));
            Section(module, 10, s =>
            {
                s.U32((uint)bodies.Count);
                foreach (var body in bodies)
                {
                    s.U32((uint)body.Length);
                    s.Raw(body);
                }
            });
            if (data.Count > 0)
            {
                Section(module, 11, s =>
                {
                    s.U32((uint)data.Count);
                    foreach (var (address, bytes) in data)
                    {
                        s.Byte(0x00);
                        s.Byte(0x41);
                        s.S64(address);
                        s.Byte(0x0B);
                        s.U32((uint)bytes.Length);
                        s.Raw(bytes);
                    }
                });
            }
            return module.ToArray();
        }

        private static void Section(Bytes module, byte id, Action<Bytes> write)
        {
            var content = new Bytes();
            write(content);
            module.Byte(id);
            module.U32((uint)content.Length);
            module.Raw(content.ToArray());
        }
    }

    /// <summary>A growable byte buffer with LEB128 encoders.</summary>
    public sealed class Bytes
    {
        private readonly List<byte> bytes = [];

        public int Length => bytes.Count;

        public void Byte(byte value) => bytes.Add(value);

        public void Raw(IEnumerable<byte> values) => bytes.AddRange(values);

        public void U32(uint value)
        {
            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0)
                    b |= 0x80;
                bytes.Add(b);
            } while (value != 0);
        }

        public void S64(long value)
        {
            while (true)
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                bool done = value == 0 && (b & 0x40) == 0 || value == -1 && (b & 0x40) != 0;
                bytes.Add(done ? b : (byte)(b | 0x80));
                if (done)
                    return;
            }
        }

        public void Name(string name)
        {
            var utf8 = Encoding.UTF8.GetBytes(name);
            U32((uint)utf8.Length);
            bytes.AddRange(utf8);
        }

        public byte[] ToArray() => [.. bytes];
    }
}
