using Godot;
using Godot.Bridge;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace MultiplayerPeeking;

[ModInitializer("Init")]
public partial class Entry : Node
{
    public const string ModId = "MultiplayerPeeking"; //At the moment, this is used only for the Logger and harmony names.

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Init()
    {
        Harmony harmony = new(ModId);
        harmony.PatchAll();

        ScriptManagerBridge.LookupScriptsInAssembly(typeof(Entry).Assembly);

        // 载入持久化设置，并订阅「设置变更 → 实时重建 overlay/缩略图/光晕」
        PeekSettings.Load();
        PeekSettings.Changed += PeekHandPatch.OnSettingsChanged;

        Log.Debug("Mod initialized!");
    }
}
