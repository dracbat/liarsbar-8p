using Mono.Cecil;
using System.IO.Compression;

// Verify the plugin inside the shipped zip - the exact artifact people download.
var zip = @"C:\Users\joshc\homelab\liarsbar-8p\dist\LiarsBar-8P.zip";
using var archive = ZipFile.OpenRead(zip);
var entry = archive.Entries.First(e => e.FullName.EndsWith("LiarsBar8P.dll"));
using var s = entry.Open();
using var ms = new MemoryStream();
s.CopyTo(ms); ms.Position = 0;

var asm = AssemblyDefinition.ReadAssembly(ms);
Console.WriteLine($"SHIPPED plugin version : {asm.Name.Version}");
Console.WriteLine();

string[] req = { "CapPatches","JoinFix","CommandGuard","SeatAssign","SeatExpansion","SeatRing",
                 "LobbyExpansion","RosterFix","CardTypeFix","DeckFix","DeckDiag","JoinDiag",
                 "VersionCheck","VersionHud" };
bool ok = true;
foreach (var n in req)
{
    var t = asm.MainModule.GetTypes().FirstOrDefault(x => x.Name == n);
    int b = t?.Methods.Count(m => m.HasBody && m.Body.Instructions.Count > 3) ?? 0;
    if (b == 0) ok = false;
    Console.WriteLine($"  {(b > 0 ? "OK  " : "MISS")}  {n}");
}

var ct = asm.MainModule.GetTypes().FirstOrDefault(x => x.Name == "CardTypeFix");
var strs = ct?.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
    .Where(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Ldstr).Select(i => (string)i.Operand) ?? Enumerable.Empty<string>();
Console.WriteLine();
Console.WriteLine($"  card index wrapping present : {strs.Any(x => x != null && x.Contains("wrapping to"))}");
Console.WriteLine($"  ALL REQUIRED CLASSES        : {(ok ? "PRESENT" : "SOMETHING MISSING")}");
