using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Common;

namespace LiarsBar8P;

/// <summary>
/// Reading and rewriting constants inside the game's compiled code.
///
/// The game is IL2CPP: some of its player limits are immediate operands baked into
/// machine code, with no field, property or call a Harmony patch could reach. Two of
/// them matter - the deck size in the deal, and the seat number the turn order wraps at.
///
/// Every write here is guarded the same way: locate the exact instruction sequence,
/// confirm the operand still holds the value the game shipped with, write, and restore
/// the original page protection. A pattern that does not match is reported and skipped,
/// so a game update makes a fix stop applying rather than corrupt anything.
/// </summary>
internal static class NativeCode
{
    private const uint PAGE_EXECUTE_READWRITE = 0x40;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

    /// <summary>Address of a game method's compiled code, via its IL2CPP MethodInfo.</summary>
    internal static IntPtr CodePointer(Type declaring, string method)
    {
        try
        {
            var mi = AccessTools.Method(declaring, method);
            if (mi == null) return IntPtr.Zero;

            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(mi);
            if (field == null) return IntPtr.Zero;

            var methodInfo = (IntPtr)field.GetValue(null);
            if (methodInfo == IntPtr.Zero) return IntPtr.Zero;

            // An Il2CppMethodInfo begins with the pointer to the compiled code.
            return Marshal.ReadIntPtr(methodInfo);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[native] could not resolve {declaring.Name}.{method}: {e.Message}");
            return IntPtr.Zero;
        }
    }

    internal static bool TryReadByte(IntPtr code, int offset, out byte value)
    {
        try { value = Marshal.ReadByte(code, offset); return true; }
        catch { value = 0; return false; }
    }

    internal static bool TryReadInt32(IntPtr code, int offset, out int value)
    {
        try { value = Marshal.ReadInt32(code, offset); return true; }
        catch { value = 0; return false; }
    }

    /// <summary>Write over executable code, restoring the original protection afterwards.</summary>
    internal static bool Write(IntPtr site, int size, Action write)
    {
        try
        {
            if (!VirtualProtect(site, (UIntPtr)size, PAGE_EXECUTE_READWRITE, out uint old)) return false;
            write();
            VirtualProtect(site, (UIntPtr)size, old, out _);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[native] write failed: {e.Message}");
            return false;
        }
    }

    internal static bool WriteByte(IntPtr site, byte value) =>
        Write(site, 1, () => Marshal.WriteByte(site, value));

    internal static bool WriteInt32(IntPtr site, int value) =>
        Write(site, 4, () => Marshal.WriteInt32(site, value));
}
