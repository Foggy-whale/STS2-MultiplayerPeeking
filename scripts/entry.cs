using Godot;
using Godot.Bridge;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

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

        // 每帧：①身体光晕棱彩流动 ②长按中键临时隐藏缩略图（读中键实时按住状态）
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            tree.ProcessFrame += PeekHandPatch.BodyPrismaticTick;
            tree.ProcessFrame += PeekHandPatch.HoldHideTick;
        }

        // 活动屏幕门控：打开地图/卡组/抽牌堆/弃牌堆等时收起缩略图/大图（只在战斗视图显示）
        ActiveScreenContext.Instance.Updated += PeekHandPatch.OnScreenChanged;

        // 把设置注册到 ModConfig 的「模组配置」标签（软依赖，没装 ModConfig 则静默跳过）
        PeekModConfigBridge.DeferredRegister();

        // 【开发用】场景内实时调参面板（F8 开关，默认隐藏；发布版把 PeekDevPanel.Enabled 置 false）
        PeekDevPanel.Install();

        // 首次广播提示：由 PeekHintBanner 的 Harmony 补丁在【联机选角色】屏幕触发（PatchAll 已应用），无需在此 Install

        Log.Debug("Mod initialized!");
    }
}
