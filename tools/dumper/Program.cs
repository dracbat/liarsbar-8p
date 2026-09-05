using Mono.Cecil;

var dll = @"C:\Program Files (x86)\Steam\steamapps\common\Liar's Bar\BepInEx\plugins\LiarsBar8P.dll";
var asm = AssemblyDefinition.ReadAssembly(dll);
Console.WriteLine($"INSTALLED version : {asm.Name.Version}");
string[] req = { "CapPatches","JoinFix","CommandGuard","SeatExpansion","LobbyExpansion",
                 "DeckFix","DeckDiag","JoinDiag","VersionCheck" };
foreach (var n in req)
{
    var t = asm.MainModule.GetTypes().FirstOrDefault(x => x.Name == n);
    int b = t?.Methods.Count(m => m.HasBody && m.Body.Instructions.Count > 3) ?? 0;
    Console.WriteLine($"  {(b > 0 ? "OK  " : "MISS")}  {n}");
}
var seat = asm.MainModule.GetTypes().FirstOrDefault(x => x.Name == "SeatExpansion");
bool relayout = seat?.Methods.Any(m => m.Name == "Relayout") ?? false;
Console.WriteLine($"\n  seat relayout (turn order fix) : {relayout}");
var deck = asm.MainModule.GetTypes().FirstOrDefault(x => x.Name == "DeckFix");
var s = deck == null ? Enumerable.Empty<string>() : deck.Methods.Where(m => m.HasBody)
    .SelectMany(m => m.Body.Instructions)
    .Where(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Ldstr)
    .Select(i => (string)i.Operand);
Console.WriteLine($"  grows OrderSprtes / LastRound  : {s.Any(x => x != null && x.Contains("OrderSprtes"))}");
