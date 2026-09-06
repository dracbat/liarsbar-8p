using Mono.Cecil;
using System.IO.Compression;

// Verifies the plugin inside a packaged zip - the exact artifact people download.
//
// A raw byte search of a .NET assembly is unreliable: string literals live in the #US
// heap as UTF-16 and are invisible to a whole-file decode when they start on an odd
// offset. That produced a false "missing" result once and a false "present" another
// time, so this reads the IL instead.
//
//   dotnet run                      verifies ../../dist/LiarsBar-8P.zip
//   dotnet run -- <path-to-zip>     verifies a specific package

var zip = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "dist", "LiarsBar-8P.zip"));

if (!File.Exists(zip))
{
    Console.WriteLine($"package not found: {zip}");
    return 1;
}

using var archive = ZipFile.OpenRead(zip);
var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("LiarsBar8P.dll"));
if (entry == null)
{
    Console.WriteLine("no plugin inside the package");
    return 1;
}

using var stream = entry.Open();
using var ms = new MemoryStream();
stream.CopyTo(ms);
ms.Position = 0;

var asm = AssemblyDefinition.ReadAssembly(ms);
Console.WriteLine($"package : {zip}");
Console.WriteLine($"version : {asm.Name.Version}");
Console.WriteLine();

string[] required =
{
    "CapPatches", "JoinFix", "CommandGuard", "SeatAssign", "SeatExpansion", "SeatRing",
    "LobbyExpansion", "RosterFix", "CardTypeFix", "DeckSizePatch", "DeckFix", "DeckDiag",
    "JoinDiag", "VersionCheck", "VersionHud"
};

bool ok = true;
foreach (var name in required)
{
    var type = asm.MainModule.GetTypes().FirstOrDefault(t => t.Name == name);
    int bodies = type?.Methods.Count(m => m.HasBody && m.Body.Instructions.Count > 3) ?? 0;
    if (bodies == 0) ok = false;
    Console.WriteLine($"  {(bodies > 0 ? "OK  " : "MISS")}  {name}");
}

Console.WriteLine();
Console.WriteLine($"  ALL REQUIRED CLASSES : {(ok ? "PRESENT" : "SOMETHING MISSING")}");
return ok ? 0 : 1;
