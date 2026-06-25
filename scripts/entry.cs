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

        // 身体光晕棱彩：每帧流动身体染色的彩虹色相
        if (Engine.GetMainLoop() is SceneTree tree)
            tree.ProcessFrame += PeekHandPatch.BodyPrismaticTick;

        // 把设置注册到 ModConfig 的「模组配置」标签（软依赖，没装 ModConfig 则静默跳过）
        PeekModConfigBridge.DeferredRegister();

        // 【开发用】场景内实时调参面板（F8 开关，默认隐藏；发布版把 PeekDevPanel.Enabled 置 false）
        PeekDevPanel.Install();

        Log.Debug("Mod initialized!");
    }
}
