using Mono.Cecil;
using Mono.Cecil.Cil;

var dll = @"C:\Users\joshc\homelab\liarsbar-8p\src\LiarsBar8P\bin\Release\net6.0\LiarsBar8P.dll";
var asm = AssemblyDefinition.ReadAssembly(dll);
Console.WriteLine("=== types in plugin ===");
foreach (var t in asm.MainModule.GetTypes().Where(t => t.Namespace == "LiarsBar8P"))
{
    var methods = t.Methods.Where(m => m.HasBody).ToList();
    Console.WriteLine($"  {t.Name,-18} methods={t.Methods.Count}");
    foreach (var m in t.Methods.Where(m => m.HasBody))
    {
        var strs = m.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldstr)
            .Select(i => i.Operand as string)
            .Where(s => s != null && s.Length > 4)
            .Take(2);
        foreach (var s in strs)
            Console.WriteLine($"      \"{(s.Length > 60 ? s.Substring(0,60) : s)}\"");
    }
}
