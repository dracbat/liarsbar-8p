using Mono.Cecil;
var dll = @"C:\Program Files (x86)\Steam\steamapps\common\Liar's Bar\BepInEx\plugins\LiarsBar8P.dll";
var asm = AssemblyDefinition.ReadAssembly(dll);
Console.WriteLine($"INSTALLED version : {asm.Name.Version}");
string[] req = { "CapPatches","JoinFix","CommandGuard","SeatAssign","SeatExpansion","SeatRing",
                 "LobbyExpansion","RosterFix","DeckFix","DeckDiag","JoinDiag","VersionCheck","VersionHud" };
foreach (var n in req)
{
    var t = asm.MainModule.GetTypes().FirstOrDefault(x => x.Name == n);
    int b = t?.Methods.Count(m => m.HasBody && m.Body.Instructions.Count > 3) ?? 0;
    Console.WriteLine($"  {(b > 0 ? "OK  " : "MISS")}  {n}");
}
