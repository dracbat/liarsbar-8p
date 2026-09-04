using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text;

// Recon dumper for Liar's Bar IL2CPP interop assemblies.
// Produces: full member listing, hardcoded-small-int report, keyword hits.

var interop = args.Length > 0 ? args[0]
    : @"C:\Program Files (x86)\Steam\steamapps\common\Liar's Bar\BepInEx\interop";
var outDir = args.Length > 1 ? args[1]
    : @"C:\Users\joshc\homelab\liarsbar-8p\recon";
Directory.CreateDirectory(outDir);

string[] targets = { "Assembly-CSharp", "Assembly-CSharp-firstpass", "Mirror", "Mirror.Components" };

var keywords = new[] { "player","max","seat","chair","slot","deck","card","lobby","room",
                       "spawn","hand","dice","gun","revolver","chamber","turn","chairs",
                       "position","count","limit","chairtransform","playercount" };

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(interop);
var rp = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate };

foreach (var t in targets)
{
    var path = Path.Combine(interop, t + ".dll");
    if (!File.Exists(path)) { Console.WriteLine($"MISSING: {t}.dll"); continue; }

    AssemblyDefinition asm;
    try { asm = AssemblyDefinition.ReadAssembly(path, rp); }
    catch (Exception e) { Console.WriteLine($"FAIL {t}: {e.Message}"); continue; }

    var full = new StringBuilder();
    var ints = new StringBuilder();
    var hits = new StringBuilder();
    int typeCount = 0, methodCount = 0;

    foreach (var type in asm.MainModule.GetTypes())
    {
        typeCount++;
        full.AppendLine($"TYPE {type.FullName}  : {type.BaseType?.Name}");

        foreach (var f in type.Fields)
        {
            var constPart = "";
            if (f.HasConstant && f.Constant is not null)
            {
                constPart = $" = {f.Constant}";
                if (f.Constant is int ci && ci >= 2 && ci <= 12)
                    ints.AppendLine($"CONST {type.FullName}.{f.Name} = {ci}");
            }
            full.AppendLine($"    F {f.FieldType.Name} {f.Name}{constPart}");

            var lname = (type.FullName + "." + f.Name).ToLowerInvariant();
            if (keywords.Any(k => lname.Contains(k)))
                hits.AppendLine($"FIELD {type.FullName}.{f.Name} : {f.FieldType.Name}{constPart}");
        }

        foreach (var p in type.Properties)
            full.AppendLine($"    P {p.PropertyType.Name} {p.Name}");

        foreach (var m in type.Methods)
        {
            methodCount++;
            var sig = string.Join(", ", m.Parameters.Select(x => $"{x.ParameterType.Name} {x.Name}"));
            full.AppendLine($"    M {m.ReturnType.Name} {m.Name}({sig})");

            var lname = (type.FullName + "." + m.Name).ToLowerInvariant();
            if (keywords.Any(k => lname.Contains(k)))
                hits.AppendLine($"METHOD {type.FullName}.{m.Name}({sig}) -> {m.ReturnType.Name}");

            // scan IL for small-int literals (candidate hardcoded caps)
            if (!m.HasBody) continue;
            foreach (var ins in m.Body.Instructions)
            {
                int? v = ins.OpCode.Code switch
                {
                    Code.Ldc_I4_2 => 2, Code.Ldc_I4_3 => 3, Code.Ldc_I4_4 => 4,
                    Code.Ldc_I4_5 => 5, Code.Ldc_I4_6 => 6, Code.Ldc_I4_7 => 7,
                    Code.Ldc_I4_8 => 8,
                    Code.Ldc_I4_S => (sbyte)ins.Operand,
                    Code.Ldc_I4   => (int)ins.Operand,
                    _ => null
                };
                if (v is >= 2 and <= 40)
                    ints.AppendLine($"IL {type.FullName}.{m.Name} : {v} @ IL_{ins.Offset:X4}");
            }
        }
    }

    File.WriteAllText(Path.Combine(outDir, $"{t}.members.txt"), full.ToString());
    File.WriteAllText(Path.Combine(outDir, $"{t}.ints.txt"), ints.ToString());
    File.WriteAllText(Path.Combine(outDir, $"{t}.keywords.txt"), hits.ToString());
    Console.WriteLine($"{t}: {typeCount} types, {methodCount} methods -> dumped");
}
Console.WriteLine("done");
