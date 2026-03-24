using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Pooling;

namespace MultiplayerPeeking;

[HarmonyPatch(typeof(NMultiplayerPlayerState), "UpdateHighlightedState")]
public static class PeekHandPatch
{
    // NCard 的自然宽度（像素），用于手动排列卡牌位置
    private const float CardNaturalWidth = 130f;
    private const float CardScale = 0.65f;
    private const float CardSpacing = 6f;

    private static readonly Dictionary<NMultiplayerPlayerState, Control> _overlays = new();

    // 提前缓存 FieldInfo，避免每帧反射查找
    private static readonly FieldInfo _isHighlightedField =
        AccessTools.Field(typeof(NMultiplayerPlayerState), "_isHighlighted");

    // Harmony Postfix：在 UpdateHighlightedState 执行完毕后运行
    public static void Postfix(NMultiplayerPlayerState __instance)
    {
        bool highlighted = (bool)_isHighlightedField.GetValue(__instance);
        bool hasOverlay = _overlays.ContainsKey(__instance);

        if (highlighted && !hasOverlay)
            ShowOverlay(__instance);
        else if (!highlighted && hasOverlay)
            HideOverlay(__instance);
    }

    private static void ShowOverlay(NMultiplayerPlayerState state)
    {
        Player player = state.Player;

        // 仅在战斗中、且只对远程玩家生效
        if (player?.PlayerCombatState == null) return;
        if (player.NetId == LocalContext.NetId) return;

        var handCards = player.PlayerCombatState.Hand.Cards;
        if (handCards.Count == 0) return;

        var overlay = new Control();
        overlay.Name = "PeekHandOverlay";

        float xOffset = 0f;
        foreach (CardModel card in handCards)
        {
            NCard? cardNode = NCard.Create(card);
            if (cardNode == null) continue;

            cardNode.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            cardNode.Scale = new Vector2(CardScale, CardScale);
            cardNode.Position = new Vector2(xOffset, 0f);
            overlay.AddChild(cardNode);

            xOffset += CardNaturalWidth * CardScale + CardSpacing;
        }

        // 挂载到全局 UI 根节点，确保渲染在最上层
        Node uiParent = (Node?)NRun.Instance?.GlobalUi ?? state.GetParent();
        uiParent.AddChild(overlay);

        // 悬浮框紧靠状态栏右侧，纵向与状态栏顶端对齐
        overlay.GlobalPosition = state.GlobalPosition + new Vector2(state.Size.X + 12f, 0f);

        _overlays[state] = overlay;
    }

    private static void HideOverlay(NMultiplayerPlayerState state)
    {
        if (!_overlays.Remove(state, out var overlay)) return;

        // 将 NCard 节点归还对象池
        foreach (Node child in overlay.GetChildren())
        {
            if (child is NCard card)
                NodePool.Free(card);
        }

        overlay.QueueFree();
    }
}