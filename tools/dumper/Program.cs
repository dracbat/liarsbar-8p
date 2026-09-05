using Mono.Cecil;
using System.IO.Compression;

var zip = @"C:\Users\joshc\homelab\liarsbar-8p\dist\LiarsBar-8P.zip";
using var archive = ZipFile.OpenRead(zip);
var entry = archive.Entries.First(e => e.FullName.EndsWith("LiarsBar8P.dll"));
using var s = entry.Open();
using var ms = new MemoryStream();
s.CopyTo(ms); ms.Position = 0;

var asm = AssemblyDefinition.ReadAssembly(ms);
Console.WriteLine($"shipped plugin version : {asm.Name.Version}");
Console.WriteLine();

string[] required = { "CapPatches", "JoinFix", "CommandGuard", "SeatExpansion",
                      "LobbyExpansion", "DeckFix", "DeckDiag", "JoinDiag" };
foreach (var n in required)
{
    var t = asm.MainModule.GetTypes().FirstOrDefault(x => x.Name == n);
    int bodies = t?.Methods.Count(m => m.HasBody && m.Body.Instructions.Count > 3) ?? 0;
    Console.WriteLine($"  {(bodies > 0 ? "OK  " : "MISS")}  {n,-16} methods with code: {bodies}");
}

// prove the card fix grows the right lists
var deck = asm.MainModule.GetTypes().FirstOrDefault(x => x.Name == "DeckFix");
if (deck != null)
{
    var strs = deck.Methods.Where(m => m.HasBody)
        .SelectMany(m => m.Body.Instructions)
        .Where(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Ldstr)
        .Select(i => i.Operand as string)
        .Where(s2 => s2 != null && (s2.Contains("Cards") || s2.Contains("deckfix")))
        .Distinct();
    Console.WriteLine("\nDeckFix targets:");
    foreach (var x in strs) Console.WriteLine($"    \"{x}\"");
}
