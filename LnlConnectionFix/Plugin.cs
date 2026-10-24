using System.Reflection.Emit;
using FrooxEngine;
using HarmonyLib;
using LiteNetLib;
using ResoniteModLoader;

namespace LnlConnectionFix;

public sealed class Plugin : ResoniteMod
{
    public override string Name => "LnlConnectionFix";
    public override string Author => "markn2000";
    public override string Version => "0.1.0";

    public override void OnEngineInit()
    {
        var harmony = new Harmony("markn2000.LnlConnectionFix");
        try
        {
            Apply(harmony);
            Msg("LNL接続辞書の修正を適用しました。新しいListenerで有効になります。");
        }
        catch (Exception error)
        {
            harmony.UnpatchAll(harmony.Id);
            Error($"LNL接続辞書の修正を適用できませんでした。修正を無効にします: {error}");
        }
    }

    internal static void Apply(Harmony harmony) => harmony.Patch(
        typeof(LNL_Listener).GetConstructors().Single(),
        transpiler: new HarmonyMethod(typeof(Plugin), nameof(Transpile)));

    internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var field = AccessTools.DeclaredField(typeof(LNL_Listener), "_peers");
        var constructor = typeof(Dictionary<NetPeer, LNL_Peer>).GetConstructor(Type.EmptyTypes);
        // 通信開始より前のフィールド初期化だけを対象にする。不一致なら適用しない。
        if (field == null || code.Count < 3
            || code[0].opcode != OpCodes.Ldarg_0
            || code[1].opcode != OpCodes.Newobj || !Equals(code[1].operand, constructor)
            || !code[2].StoresField(field)
            || code.Count(instruction => instruction.StoresField(field)) != 1)
            throw new InvalidOperationException("LNL_Listenerの接続辞書初期化が対応する形式と異なります。");

        // 同じスタック形状の呼び出しへ置換し、分岐ラベルと例外ブロックを保持する。
        code[1].opcode = OpCodes.Call;
        code[1].operand = AccessTools.DeclaredMethod(typeof(Plugin), nameof(CreatePeerDictionary));
        return code;
    }

    private static Dictionary<NetPeer, LNL_Peer> CreatePeerDictionary() => new(ReferenceEqualityComparer.Instance);
}
