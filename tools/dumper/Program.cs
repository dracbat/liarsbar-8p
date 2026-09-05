using Mono.Cecil;

var interop = @"C:\Program Files (x86)\Steam\steamapps\common\Liar's Bar\BepInEx\interop";
var r = new DefaultAssemblyResolver(); r.AddSearchDirectory(interop);
var asm = AssemblyDefinition.ReadAssembly(Path.Combine(interop, "Assembly-CSharp.dll"),
                                          new ReaderParameters { AssemblyResolver = r });

foreach (var typeName in new[] { "DeckGamePlayManager", "Manager" })
{
    var t = asm.MainModule.GetTypes().First(x => x.FullName == typeName);
    Console.WriteLine($"=== {typeName}: every list / array property ===");
    foreach (var p in t.Properties)
    {
        var pt = p.PropertyType;
        bool isList = pt.Name.StartsWith("List`") || pt.Name.StartsWith("SyncList")
                      || pt.Name.Contains("Array") || pt.IsArray;
        if (!isList) continue;
        string desc = pt.Name;
        if (pt is GenericInstanceType g)
            desc = $"{pt.Name.Split('`')[0]}<{string.Join(",", g.GenericArguments.Select(a => a.Name))}>";
        Console.WriteLine($"  {p.Name,-24} {desc}");
    }
    Console.WriteLine();
}
