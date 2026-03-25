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

    private const float CardScale   = 0.4f;
    private const float CardSpacing = 8f;

    // hover 消失后延迟隐藏，给鼠标从状态栏移向右侧卡牌的时间
    private const double HideDelaySeconds = 0;

    private static readonly Dictionary<NMultiplayerPlayerState, Control> _overlays = [];
    // 每个 state 最多一个挂起的隐藏计时器
    private static readonly Dictionary<NMultiplayerPlayerState, SceneTreeTimer> _hideTimers = [];
    // 鼠标当前是否停在 overlay 上（阻止计时器触发隐藏）
    private static readonly HashSet<NMultiplayerPlayerState> _overlayHovered = [];

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
            // 鼠标已移到 overlay 上，不隐藏
            if (_overlayHovered.Contains(__instance)) return;

            // 已有计时器则不重复创建
            if (_hideTimers.ContainsKey(__instance)) return;

            var tree = __instance.GetTree();
            if (tree == null) { HideOverlay(__instance); return; }

            // 延迟隐藏：给鼠标从状态栏移向右侧卡牌的时间
            var timer = tree.CreateTimer(HideDelaySeconds);
            _hideTimers[__instance] = timer;
            timer.Timeout += () =>
            {
                _hideTimers.Remove(__instance);
                // 计时器到期时再次确认：若鼠标在 overlay 上则仍不隐藏
                bool stillHighlighted = _isHighlightedField.GetValue(__instance) is true;
                if (!stillHighlighted && !_overlayHovered.Contains(__instance))
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
        // Pass：接收 MouseEntered/Exited 信号以维持 hover 状态，同时不阻断下层点击
        overlay.MouseFilter = Control.MouseFilterEnum.Pass;

        // NCard 视觉原点在卡牌中心：第一张牌从 halfCardWidth 开始，
        // 使其视觉左边缘恰好落在 overlay 原点（x=0），方便外部精确定位 overlay
        var cardNodes = new List<NCard>();
        float scaledHalfWidth = CardNaturalWidth * CardScale / 2f;
        float xOffset = scaledHalfWidth;
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

        // 鼠标进入 overlay 区域时取消挂起的隐藏计时器
        overlay.MouseEntered += () =>
        {
            _overlayHovered.Add(state);
            _hideTimers.Remove(state);
        };
        // 鼠标离开 overlay 区域时启动隐藏计时器
        overlay.MouseExited += () =>
        {
            _overlayHovered.Remove(state);
            bool highlighted = _isHighlightedField.GetValue(state) is true;
            if (!highlighted && _overlays.ContainsKey(state) && !_hideTimers.ContainsKey(state))
            {
                var tree = state.GetTree();
                if (tree == null) { HideOverlay(state); return; }
                var timer = tree.CreateTimer(HideDelaySeconds);
                _hideTimers[state] = timer;
                timer.Timeout += () =>
                {
                    _hideTimers.Remove(state);
                    if (_isHighlightedField.GetValue(state) is not true && !_overlayHovered.Contains(state))
                        HideOverlay(state);
                };
            }
        };

        // 先挂载到场景树，使卡牌节点进入 Ready 状态
        Node uiParent = (Node?)NRun.Instance?.GlobalUi ?? state.GetParent();
        uiParent.AddChild(overlay);

        // 节点 Ready 后再调用 UpdateVisuals，否则内部 IsNodeReady() 检查会直接跳过
        foreach (NCard cardNode in cardNodes)
        {
            cardNode.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
        }

        // 延后一帧设置位置，确保 Hitbox 布局已完成，Size 已填充
        Callable.From(() =>
        {
            if (!overlay.IsInsideTree()) return;
            // 锚点取 Hitbox（可见黄色框），而非外层隐形容器
            // NCard 视觉原点在卡牌中心，Y 取 Hitbox 中线
            var hitbox = state.Hitbox;
            overlay.GlobalPosition = new Vector2(
                hitbox.GlobalPosition.X + hitbox.Size.X + 24f ,
                hitbox.GlobalPosition.Y + hitbox.Size.Y / 2f
            );
        }).CallDeferred();

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