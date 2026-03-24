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
    // NCard.defaultSize = (300, 422)，见游戏源码
    private const float CardNaturalWidth  = 300f;
    private const float CardNaturalHeight = 422f;
    private const float CardScale   = 0.40f;
    private const float CardSpacing = 8f;

    // hover 消失后延迟隐藏，避免鼠标短暂离开时闪烁
    private const double HideDelaySeconds = 0.15;

    private static readonly Dictionary<NMultiplayerPlayerState, Control> _overlays = [];
    // 每个 state 最多一个挂起的隐藏计时器
    private static readonly Dictionary<NMultiplayerPlayerState, SceneTreeTimer> _hideTimers = [];

    private static readonly FieldInfo _isHighlightedField =
        AccessTools.Field(typeof(NMultiplayerPlayerState), "_isHighlighted");

    // Harmony Postfix：在 UpdateHighlightedState 执行完毕后运行
    public static void Postfix(NMultiplayerPlayerState __instance)
    {
        bool highlighted = _isHighlightedField.GetValue(__instance) is true;

        if (highlighted)
        {
            // 取消挂起的隐藏计时器
            _hideTimers.Remove(__instance);

            if (!_overlays.ContainsKey(__instance))
                ShowOverlay(__instance);
        }
        else if (_overlays.ContainsKey(__instance))
        {
            // 已有计时器则不重复创建
            if (_hideTimers.ContainsKey(__instance)) return;

            var tree = __instance.GetTree();
            if (tree == null) { HideOverlay(__instance); return; }

            // 延迟隐藏：给鼠标短暂回来的机会
            var timer = tree.CreateTimer(HideDelaySeconds);
            _hideTimers[__instance] = timer;
            timer.Timeout += () =>
            {
                _hideTimers.Remove(__instance);
                // 计时器到期时再次确认 highlighted 状态
                bool stillHighlighted = _isHighlightedField.GetValue(__instance) is true;
                if (!stillHighlighted)
                    HideOverlay(__instance);
            };
        }
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
        // 设为 Ignore，不拦截鼠标事件，避免遮挡 Hitbox 导致 hover 失效
        overlay.MouseFilter = Control.MouseFilterEnum.Ignore;

        var cardNodes = new List<NCard>();
        float xOffset = 0f;
        foreach (CardModel card in handCards)
        {
            NCard? cardNode = NCard.Create(card);
            if (cardNode == null) continue;

            cardNode.Scale    = new Vector2(CardScale, CardScale);
            cardNode.Position = new Vector2(xOffset, 0f);
            cardNode.MouseFilter = Control.MouseFilterEnum.Ignore;
            overlay.AddChild(cardNode);
            cardNodes.Add(cardNode);

            xOffset += CardNaturalWidth * CardScale + CardSpacing;
        }

        // 先挂载到场景树，使卡牌节点进入 Ready 状态
        Node uiParent = (Node?)NRun.Instance?.GlobalUi ?? state.GetParent();
        uiParent.AddChild(overlay);

        // 节点 Ready 后再调用 UpdateVisuals，否则内部 IsNodeReady() 检查会直接跳过
        foreach (NCard cardNode in cardNodes)
        {
            cardNode.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
        }

        // 将悬浮框定位在状态栏正上方
        float scaledHeight = CardNaturalHeight * CardScale;
        overlay.GlobalPosition = state.GlobalPosition + new Vector2(0f, -(scaledHeight + 12f));

        _overlays[state] = overlay;
    }

    private static void HideOverlay(NMultiplayerPlayerState state)
    {
        if (!_overlays.Remove(state, out var overlay)) return;

        // 先把卡牌从 overlay 摘除，再归还对象池；
        // 否则 overlay.QueueFree() 会释放仍被 pool 引用的节点
        var cards = new List<NCard>();
        foreach (Node child in overlay.GetChildren())
        {
            if (child is NCard card)
                cards.Add(card);
        }
        foreach (NCard card in cards)
        {
            overlay.RemoveChild(card);
            NodePool.Free(card);
        }

        overlay.QueueFree();
    }
}