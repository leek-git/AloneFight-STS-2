using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace AloneFight;

/// <summary>
/// AloneFight 模组的核心状态与界面重绑（一名本地玩家在单人型跑团中
/// 同时操控 2~4 名英雄）。
///
/// 本文件模块划分：
///   • 选人开团 —— PendingParty、Begin/EndPartySelection、StartPartyRun
///     （每名英雄创建一个 Player，NetId = 槽位 + 1）。
///   • 生命周期 —— TryInitializeFromRun / CleanUp，识别并接管多于 1 名英雄的跑团。
///   • 座位切换 —— RotateToNext / SwitchTo / ApplyCurrentControl：把全局
///     “本地玩家”身份切到当前英雄，并刷新所有绑定界面。
///   • 身份统一切换 —— SeatAllIdentities 同时改写 LocalContext.NetId 与
///     六个房间同步器各自私有的 _localPlayerId。
///   • 视觉反馈 —— RefreshPartyHighlight（左上角高亮框/明暗/迷你数值）、
///     RefreshBattlefieldForeground（战斗中调暗板凳英雄）。
///   • 房间界面重绑 —— 篝火（RefreshRestSiteUi）、事件房（RefreshEventUi，
///     含古代事件对话残留清理）、商店（RefreshMerchantUi）。
///   • 奖励流程辅助 —— FollowRewardHero / ResetControlToLeader。
///   • HUD 重绑 —— RebindTopBar（金币/血量/牌库/头像/药水栏/遗物栏）与
///     RebindCombatUi（牌堆、能量/星星计数器、手牌）。
///
/// Harmony 拦截点集中在 AloneFightPatches；由于游戏相关字段几乎全部私有，
/// 本类大量使用反射访问。
/// </summary>
public static class AloneFightManager
{
    public static List<Player>? PartyPlayers { get; private set; }
    public static int CurrentControlIndex { get; private set; }
    public static bool IsActive => PartyPlayers != null && PartyPlayers.Count > 1;

    public static Player? CurrentControlPlayer =>
        (PartyPlayers != null && CurrentControlIndex >= 0 && CurrentControlIndex < PartyPlayers.Count)
            ? PartyPlayers[CurrentControlIndex] : null;

    private static ulong? _originalNetId;

    // ── 主菜单选人（2~4 名英雄） ───────────────────────────────────

    public const int MinPartySize = 2;
    public const int MaxPartySize = 4;

    /// <summary>是否正处于选人界面（挑选 2~4 名英雄）。</summary>
    public static bool IsSelectingParty { get; private set; }

    /// <summary>选人界面中已选中的英雄（按选择顺序，可重复）。</summary>
    public static List<CharacterModel> PendingParty { get; } = new();

    private static readonly MethodInfo? StartRunMethod =
        AccessTools.Method(typeof(NGame), "StartRun", new[] { typeof(RunState) });

    // NCombatUi 私有字段的反射访问器
    private static readonly AccessTools.FieldRef<NCombatUi, CombatState?> _stateRef =
        AccessTools.FieldRefAccess<NCombatUi, CombatState?>("_state");
    private static readonly AccessTools.FieldRef<NCombatUi, NCombatPilesContainer?> _pilesRef =
        AccessTools.FieldRefAccess<NCombatUi, NCombatPilesContainer?>("_combatPilesContainer");
    private static readonly AccessTools.FieldRef<NCombatUi, NStarCounter?> _starRef =
        AccessTools.FieldRefAccess<NCombatUi, NStarCounter?>("_starCounter");
    private static readonly AccessTools.FieldRef<NCombatUi, NEnergyCounter?> _energyRef =
        AccessTools.FieldRefAccess<NCombatUi, NEnergyCounter?>("_energyCounter");

    private static readonly MethodInfo? _createCardNode =
        AccessTools.Method(typeof(CardPileCmd), "CreateCardNodeAndUpdateVisuals");

    // NMultiplayerPlayerState 私有迷你数值容器（能量/星星/手牌数）的反射访问器
    private static readonly AccessTools.FieldRef<NMultiplayerPlayerState, Control> _mpEnergyRef =
        AccessTools.FieldRefAccess<NMultiplayerPlayerState, Control>("_energyContainer");
    private static readonly AccessTools.FieldRef<NMultiplayerPlayerState, Control> _mpStarRef =
        AccessTools.FieldRefAccess<NMultiplayerPlayerState, Control>("_starContainer");
    private static readonly AccessTools.FieldRef<NMultiplayerPlayerState, Control> _mpCardRef =
        AccessTools.FieldRefAccess<NMultiplayerPlayerState, Control>("_cardContainer");
    private static readonly MethodInfo? _mpRefreshCombatValues =
        AccessTools.Method(typeof(NMultiplayerPlayerState), "RefreshCombatValues");

    // 药水栏 / 遗物栏的反射访问器：这两个栏位绑定的是当前座位的“本地英雄”，
    // 每次切座都必须重建，否则获得奖励时的播放动画会按旧英雄查找槽位而崩溃。
    private static readonly AccessTools.FieldRef<NPotionContainer, List<NPotionHolder>> _potionHoldersRef =
        AccessTools.FieldRefAccess<NPotionContainer, List<NPotionHolder>>("_holders");
    private static readonly AccessTools.FieldRef<NRelicInventory, List<NRelicInventoryHolder>> _relicNodesRef =
        AccessTools.FieldRefAccess<NRelicInventory, List<NRelicInventoryHolder>>("_relicNodes");
    private static readonly PropertyInfo? _holderPotionProp =
        typeof(NPotionHolder).GetProperty(
            "Potion", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    // ── 生命周期 ───────────────────────────────────────────────────

    public static void TryInitializeFromRun(RunState? runState)
    {
        if (runState == null) return;
        if (RunManager.Instance.NetService.Type != NetGameType.Singleplayer) return;

        var players = runState.Players.ToList();
        if (players.Count <= 1) return;

        _originalNetId = LocalContext.NetId;
        PartyPlayers = players;
        CurrentControlIndex = 0;
        LocalContext.NetId = players[0].NetId;
        AloneFightLog.BeginRun(runState);
        AloneFightLog.Info($"[AloneFight] 已激活 —— 共 {players.Count} 名英雄，队长：{players[0].Character.Id}");
    }

    public static void CleanUp()
    {
        if (_originalNetId.HasValue) LocalContext.NetId = _originalNetId;
        PartyPlayers = null;
        CurrentControlIndex = 0;
        _originalNetId = null;
        EndPartySelection();
        AloneFightLog.Info("[AloneFight] 状态已清理。");
        AloneFightLog.EndRun();
    }

    // ── 选人与开团 ─────────────────────────────────────────────────

    public static void BeginPartySelection()
    {
        IsSelectingParty = true;
        PendingParty.Clear();
        AloneFightLog.Info("[AloneFight] 打开选人界面（选择 2~4 名英雄）。");
    }

    public static void EndPartySelection()
    {
        IsSelectingParty = false;
        PendingParty.Clear();
    }

    /// <summary>
    /// 创建并启动一个包含 2~4 名本地英雄的单人型跑团。
    /// 流程参照 NGame.StartNewSingleplayerRun，但英雄阵容由选人界面指定。
    /// </summary>
    public static async Task<RunState> StartPartyRun(
        IReadOnlyList<CharacterModel> characters, int ascensionLevel, string seed, List<ActModel> acts)
    {
        UnlockState unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();

        var players = new List<Player>();
        for (int i = 0; i < characters.Count; i++)
        {
            players.Add(Player.CreateForNewRun(characters[i], unlockState, (ulong)(i + 1)));
        }

        AloneFightLog.Info($"[AloneFight] 启动跑团，共 {players.Count} 名英雄：{string.Join(", ", players.Select(p => p.Character.Id.Entry))}");

        RunState runState = RunState.CreateForNewRun(
            players,
            acts.Select(a => a.ToMutable()).ToList(),
            System.Array.Empty<ModifierModel>(),
            GameMode.Standard,
            ascensionLevel,
            seed);

        RunManager.Instance.SetUpNewSingleplayer(runState, shouldSave: true);

        if (StartRunMethod != null)
        {
            var task = (Task)StartRunMethod.Invoke(NGame.Instance, new object[] { runState })!;
            await task;
        }

        return runState;
    }

    // ── 座位切换 ───────────────────────────────────────────────────

    /// <summary>循环切换到下一名存活英雄（阵亡者自动跳过）。</summary>
    public static void RotateToNext()
    {
        if (!IsActive || PartyPlayers == null) return;
        for (int i = 0; i < PartyPlayers.Count; i++)
        {
            CurrentControlIndex = (CurrentControlIndex + 1) % PartyPlayers.Count;
            if (CurrentControlPlayer != null && !CurrentControlPlayer.Creature.IsDead) break;
        }
        ApplyCurrentControl();
    }

    private static void ApplyCurrentControl()
    {
        var p = CurrentControlPlayer;
        if (p == null) return;
        SeatAllIdentities(p);
        AloneFightLog.Debug($"[AloneFight] 控制权 → 英雄 NetId={p.NetId}（{p.Character.Id}）");
        RebindCombatUi();
        BringCurrentHeroToFront();
        RebindTopBar(p);
    }

    // ── 统一切换各同步器的本地玩家身份 ─────────────────────────────
    //
    // 多个同步器各自保存了一份私有“本地玩家 id”（代表当前网络客户端）。
    // AloneFight 中所有英雄共存于同一个空网络客户端，因此每次切座都必须把
    // 这些 id 连同 LocalContext.NetId 一起切到当前英雄，否则投票、商店购买、
    // 宝箱遗物选择、事件选项都会继续作用在进房时那名英雄身上。
    private static readonly Dictionary<Type, FieldInfo?> _syncIdFields = new();

    private static void SeatSynchronizer(object? sync, ulong id)
    {
        if (sync == null) return;
        var type = sync.GetType();
        if (!_syncIdFields.TryGetValue(type, out var field))
        {
            field = type.GetField("_localPlayerId", BindingFlags.Instance | BindingFlags.NonPublic);
            _syncIdFields[type] = field;
        }
        field?.SetValue(sync, id);
    }

    /// <summary>把全局 NetId 与六个房间同步器的本地玩家身份统一切到指定英雄。</summary>
    public static void SeatAllIdentities(Player p)
    {
        LocalContext.NetId = p.NetId;
        var rm = RunManager.Instance;
        SeatSynchronizer(rm.RewardsSetSynchronizer, p.NetId);
        SeatSynchronizer(rm.EventSynchronizer, p.NetId);
        SeatSynchronizer(rm.RestSiteSynchronizer, p.NetId);
        SeatSynchronizer(rm.OneOffSynchronizer, p.NetId);
        SeatSynchronizer(rm.RewardSynchronizer, p.NetId);
        SeatSynchronizer(rm.TreasureRoomRelicSynchronizer, p.NetId);
    }

    /// <summary>当前操控英雄（台前）与板凳英雄的明暗配色。</summary>
    private static readonly Color FrontColor = Colors.White;
    private static readonly Color BackColor = new(0.45f, 0.45f, 0.45f, 1f);

    /// <summary>
    /// 切座后的“请到台前”反馈：左上角队伍栏高亮框 + 明暗变化，
    /// 战斗中则同步调暗战场上的其他玩家单位。
    /// </summary>
    public static void BringCurrentHeroToFront()
    {
        RefreshPartyHighlight();
        RefreshBattlefieldForeground();
    }

    /// <summary>
    /// 按当前操控英雄刷新左上角队伍栏：移动选中框、当前头像高亮其余调暗，
    /// 并切换各头像上的迷你能量/星星/手牌数显示。
    /// </summary>
    public static void RefreshPartyHighlight()
    {
        var current = CurrentControlPlayer;
        if (current == null) return;

        var container = NRun.Instance?.GlobalUi?.MultiplayerPlayerContainer;
        if (container == null) return;

        // 选中框：先清除所有英雄的高亮，再框住当前英雄
        if (PartyPlayers != null)
        {
            foreach (var p in PartyPlayers)
                container.UnhighlightPlayer(p);
        }
        container.HighlightPlayer(current);

        bool inCombat = CombatManager.Instance != null && CombatManager.Instance.IsInProgress;

        foreach (Node child in container.GetChildren())
        {
            if (child is not NMultiplayerPlayerState state) continue;

            var energy = _mpEnergyRef(state);
            var star = _mpStarRef(state);
            var card = _mpCardRef(state);

            // 头像的“台前/板凳”明暗过渡
            bool isFront = state.Player == current;
            TweenColor(state, isFront ? FrontColor : BackColor);

            if (isFront)
            {
                // 当前英雄的数值由主战斗 UI 展示，隐藏头像上的迷你数值
                energy.Visible = false;
                star.Visible = false;
                card.Visible = false;
            }
            else if (inCombat && state.Player.PlayerCombatState != null)
            {
                energy.Visible = true;
                card.Visible = true;
                _mpRefreshCombatValues?.Invoke(state, null); // 内部设置星星可见性与数值文本
            }
            else
            {
                // 非战斗状态下所有迷你数值保持隐藏（与游戏 OnCombatEnded 一致）
                energy.Visible = false;
                star.Visible = false;
                card.Visible = false;
            }
        }
    }

    /// <summary>调暗战场上非当前操控的玩家单位，突出当前英雄。</summary>
    public static void RefreshBattlefieldForeground()
    {
        var room = NCombatRoom.Instance;
        if (room == null) return;

        var current = CurrentControlPlayer;
        foreach (var creature in room.CreatureNodes)
        {
            if (!creature.Entity.IsPlayer || creature.Entity.Player == null) continue;
            if (creature.Entity.IsDead) continue; // 死亡表现保持原样，不做明暗处理
            bool isFront = creature.Entity.Player == current;
            TweenColor(creature, isFront ? FrontColor : BackColor);
        }
    }

    private static void TweenColor(CanvasItem node, Color target)
    {
        if (!GodotObject.IsInstanceValid(node)) return;
        if (node.Modulate == target) return;
        var tween = node.CreateTween();
        tween.TweenProperty(node, "modulate", target, 0.22f)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.Out);
    }

    /// <summary>直接切换到指定英雄（按 Player 引用）。</summary>
    public static void SwitchTo(Player player)
    {
        if (PartyPlayers == null) return;
        int idx = PartyPlayers.IndexOf(player);
        if (idx < 0) return;
        CurrentControlIndex = idx;
        ApplyCurrentControl();
    }

    // ── 篝火房支持 ─────────────────────────────────────────────────

    private static readonly MethodInfo? _updateRestSiteOptions =
        AccessTools.Method(typeof(NRestSiteRoom), "UpdateRestSiteOptions");

    /// <summary>一名英雄选完篝火选项后，自动切到下一名还有未选选项的英雄。</summary>
    public static void RotateToNextWithRestSiteOptions()
    {
        if (PartyPlayers == null) return;
        var sync = RunManager.Instance.RestSiteSynchronizer;
        if (sync == null) return;

        for (int i = 0; i < PartyPlayers.Count; i++)
        {
            CurrentControlIndex = (CurrentControlIndex + 1) % PartyPlayers.Count;
            var p = CurrentControlPlayer;
            if (p != null && !p.Creature.IsDead && sync.GetOptionsForPlayer(p.NetId).Count > 0)
            {
                ApplyCurrentControl();
                RefreshRestSiteUi();
                AloneFightLog.Debug($"[AloneFight] 篝火自动轮换 → 英雄 {p.NetId}（{p.Character.Id.Entry}）");
                return;
            }
        }
        // 所有人都已选完，停在当前英雄，界面会显示继续按钮
        AloneFightLog.Debug("[AloneFight] 所有英雄已完成篝火选择。");
    }

    /// <summary>按当前操控英雄刷新篝火选项界面。</summary>
    public static void RefreshRestSiteUi()
    {
        var room = NRestSiteRoom.Instance;
        if (room == null || _updateRestSiteOptions == null) return;
        _updateRestSiteOptions.Invoke(room, null);
    }

    // ── 事件房支持 ─────────────────────────────────────────────────

    private static readonly AccessTools.FieldRef<NEventRoom, EventModel>? _eventRoomEventRef =
        AccessTools.FieldRefAccess<NEventRoom, EventModel>("_event");
    private static readonly AccessTools.FieldRef<NEventRoom, CancellationTokenSource>? _eventRoomCtsRef =
        AccessTools.FieldRefAccess<NEventRoom, CancellationTokenSource>("_cts");
    private static readonly MethodInfo? _eventSetupLayout =
        AccessTools.Method(typeof(NEventRoom), "SetupLayout");
    private static readonly MethodInfo? _refreshEventStateMethod =
        AccessTools.Method(typeof(NEventRoom), "RefreshEventState");
    private static readonly MethodInfo? _onEnteringEventCombatMethod =
        AccessTools.Method(typeof(NEventRoom), "OnEnteringEventCombat");
    private static readonly EventInfo? _eventStateChangedInfo =
        typeof(EventModel).GetEvent("StateChanged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly EventInfo? _enteringEventCombatInfo =
        typeof(EventModel).GetEvent("EnteringEventCombat", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// 把事件房界面重绑到当前英雄自己的 EventModel，重建标题/描述/选项。
    /// 每名英雄都持有独立的事件实例。
    /// </summary>
    public static void RefreshEventUi()
    {
        var room = NEventRoom.Instance;
        var current = CurrentControlPlayer;
        if (room == null || current == null || _eventRoomEventRef == null) return;

        var newEvent = RunManager.Instance.EventSynchronizer.GetEventForPlayer(current);
        // 内嵌战斗的事件由战斗流程驱动，不做重绑
        if (newEvent.LayoutType == EventLayoutType.Combat) return;

        var oldEvent = _eventRoomEventRef(room);
        if (oldEvent == newEvent)
        {
            CollapseMapIfEventUnfinished(newEvent);
            return;
        }

        // SetupLayout() 是异步方法：内部 await Cmd.Wait(0.2s)（基于房间的 _cts）
        // 之后才重建对话/选项。快速连切多个英雄时会有多个重建任务同时在飞，
        // 旧任务醒来后会重复追加对话/选项。这里取消旧令牌，保证只有最后一次
        // 重建能执行完成。
        if (_eventRoomCtsRef != null)
        {
            try
            {
                _eventRoomCtsRef(room)?.Cancel();
                _eventRoomCtsRef(room) = new CancellationTokenSource();
            }
            catch (Exception e)
            {
                AloneFightLog.Error($"[AloneFight] 重置事件房取消令牌失败：{e.Message}");
            }
        }

        // 把房间对旧英雄事件的回调退订掉，避免多次切座后订阅堆积
        if (oldEvent != null)
        {
            if (_eventStateChangedInfo?.RemoveMethod != null && _refreshEventStateMethod != null)
            {
                var handler = Delegate.CreateDelegate(typeof(Action<EventModel>), room, _refreshEventStateMethod);
                _eventStateChangedInfo.RemoveMethod.Invoke(oldEvent, new object[] { handler });
            }
            if (_enteringEventCombatInfo?.RemoveMethod != null && _onEnteringEventCombatMethod != null)
            {
                var handler = Delegate.CreateDelegate(typeof(Action), room, _onEnteringEventCombatMethod);
                _enteringEventCombatInfo.RemoveMethod.Invoke(oldEvent, new object[] { handler });
            }
        }

        // 古代事件（捏奥等）的关键修复：NAncientEventLayout.SetDialogue 只会向
        // 对话容器“追加”对话行节点；原版要等事件状态变化、RefreshEventState
        // 触发 ClearDialogue 时才会清空。如果上一名英雄还没选选项就切走，他的
        // 对话行仍留在容器里，新英雄的对话行被追加在后面，而对话索引只按新行
        // 计数 —— 滚动位置与“是否最后一行”的判定全部错位，选项按钮永远不会
        // 启用，表现为选项点不动。原版一个事件房只展示一个事件，不会遇到此问题。
        if (room.Layout is NAncientEventLayout ancientLayout)
        {
            ancientLayout.ClearDialogue();
        }

        _eventRoomEventRef(room) = newEvent;
        _eventSetupLayout?.Invoke(room, null);

        CollapseMapIfEventUnfinished(newEvent);

        AloneFightLog.Debug($"[AloneFight] 事件界面已重绑到英雄 {current.NetId}（{current.Character.Id.Entry}）。");
    }

    /// <summary>
    /// 已点“继续”的英雄会让地图盖在共享的事件房上方。当当前查看的英雄
    /// 自己的事件尚未完成时，把地图收起来使其选项可点；最后一名英雄点继续后
    /// 地图会按原版逻辑重新打开。
    /// </summary>
    private static void CollapseMapIfEventUnfinished(EventModel eventModel)
    {
        if (eventModel.IsFinished) return;
        if (NMapScreen.Instance is { } map && map.IsOpen)
        {
            map.SetTravelEnabled(false);
            map.Close(animateOut: false);
        }
    }

    // ── 战后奖励流程的身份跟随 ─────────────────────────────────────

    /// <summary>
    /// 战后逐个领奖期间，让控制权跟随当前展示奖励的英雄
    /// （切换 NetId + 左上角高亮，不重建战斗 UI）。
    /// </summary>
    public static void FollowRewardHero(Player player)
    {
        if (PartyPlayers == null) return;
        int idx = PartyPlayers.IndexOf(player);
        if (idx < 0) return;
        CurrentControlIndex = idx;
        SeatAllIdentities(player);
        AloneFightLog.Debug($"[AloneFight] 奖励焦点 → 英雄 {player.NetId}（{player.Character.Id.Entry}）。");
        RefreshPartyHighlight();
        RebindTopBar(player);
    }

    /// <summary>奖励流程结束后，把控制权交还队长（第一名英雄）。</summary>
    public static void ResetControlToLeader()
    {
        if (PartyPlayers == null || PartyPlayers.Count == 0) return;
        CurrentControlIndex = 0;
        var leader = PartyPlayers[0];
        SeatAllIdentities(leader);
        RefreshPartyHighlight();
        RebindTopBar(leader);
    }

    // ── 顶栏重绑 —— 金币/血量/头像/牌库/药水/遗物全部跟随当前英雄 ──

    public static void RebindTopBar(Player p)
    {
        var topBar = NRun.Instance?.GlobalUi?.TopBar;
        if (topBar == null || !GodotObject.IsInstanceValid(topBar)) return;

        try
        {
            RebindGoldWidget(topBar.Gold, p);
            RebindHpWidget(topBar.Hp, p);
            topBar.Deck.GetType().GetMethod("Initialize")!.Invoke(topBar.Deck, new object[] { p });
            RebindPortrait(topBar.Portrait, p);
            RebindPotionBar(topBar.PotionContainer, p);
            var relicInventory = NRun.Instance?.GlobalUi?.RelicInventory;
            if (relicInventory != null) RebindRelicBar(relicInventory, p);
        }
        catch (Exception e)
        {
            AloneFightLog.Error("[AloneFight] 顶栏重绑失败", e);
        }
    }

    /// <summary>
    /// 为当前英雄重建药水栏。获得药水后 PotionCmd.AnimatePotion 会被无条件调用，
    /// 若药栏仍显示上一名英雄，会因找不到槽位抛出
    /// “Sequence contains no matching element”，导致整个领奖/事件流程中途崩溃。
    /// </summary>
    private static void RebindPotionBar(NPotionContainer container, Player p)
    {
        if (!GodotObject.IsInstanceValid(container)) return;

        var holders = _potionHoldersRef(container);

        // GrowPotionHolders 只会“增加”槽位，从不收缩。若上一名英雄拥有扩容遗物
        // （MaxPotionCount 更大），切到容量更小的英雄时多余槽位会残留 —— 只有
        // UI 是空槽，数据层没有对应 PotionSlot，看似多了一栏却无法存放药水。
        // 这里按当前英雄的容量销毁多余槽位。
        for (int i = holders.Count - 1; i >= p.MaxPotionCount; i--)
        {
            var surplus = holders[i];
            surplus.GetParent()?.RemoveChild(surplus);
            surplus.QueueFreeSafely();
            holders.RemoveAt(i);
        }

        // 清空每个槽位上当前英雄的药水视觉节点
        foreach (var holder in holders.ToList())
        {
            foreach (var potionNode in holder.GetChildren().OfType<NPotion>().ToList())
            {
                holder.RemoveChildSafely(potionNode);
                potionNode.QueueFreeSafely();
            }
            _holderPotionProp?.SetValue(holder, null);
            var emptyIcon = holder.GetNodeOrNull<TextureRect>("%EmptyIcon");
            if (emptyIcon != null) emptyIcon.Modulate = Colors.White;
        }

        // Initialize 内部会解绑旧英雄，按 LocalContext.GetMe（此时已切为 p）
        // 重新绑定，并从 p.Potions 重新填充药栏
        container.Initialize(p.RunState);
    }

    /// <summary>为当前英雄重建遗物栏（崩溃类型与药水栏相同）。</summary>
    private static void RebindRelicBar(NRelicInventory inventory, Player p)
    {
        if (!GodotObject.IsInstanceValid(inventory)) return;

        var nodes = _relicNodesRef(inventory);
        foreach (var node in nodes.ToList())
        {
            inventory.RemoveChildSafely(node);
            node.QueueFreeSafely();
        }
        nodes.Clear();

        inventory.Initialize((RunState)p.RunState);
    }

    /// <summary>NTopBarGold.Initialize 会订阅玩家的 GoldChanged，重绑前先退订旧英雄。</summary>
    private static void RebindGoldWidget(object widget, Player p)
    {
        var t = widget.GetType();
        if (t.GetField("_player", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(widget) is Player old)
        {
            var update = t.GetMethod("UpdateGold", BindingFlags.Instance | BindingFlags.NonPublic)!;
            old.GoldChanged -= (Action)Delegate.CreateDelegate(typeof(Action), widget, update);
        }
        t.GetMethod("Initialize")!.Invoke(widget, new object[] { p });
    }

    /// <summary>NTopBarHp.Initialize 会订阅血量变化，重绑前先退订旧英雄。</summary>
    private static void RebindHpWidget(object widget, Player p)
    {
        var t = widget.GetType();
        if (t.GetField("_player", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(widget) is Player old)
        {
            var update = t.GetMethod("UpdateHealth", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var del = (Action<int, int>)Delegate.CreateDelegate(typeof(Action<int, int>), widget, update);
            old.Creature.CurrentHpChanged -= del;
            old.Creature.MaxHpChanged -= del;
        }
        t.GetMethod("Initialize")!.Invoke(widget, new object[] { p });
    }

    /// <summary>头像是角色图标的容器节点：移除旧图标后按新英雄重新 Initialize。</summary>
    private static void RebindPortrait(object portrait, Player p)
    {
        if (portrait is not Node node) return;
        foreach (var child in node.GetChildren().ToList())
            node.RemoveChildSafely(child);
        node.GetType().GetMethod("Initialize")!.Invoke(node, new object[] { p });
    }

    // ── 战斗界面重绑 ───────────────────────────────────────────────

    public static void RebindCombatUi()
    {
        var ui = NCombatRoom.Instance?.Ui;
        if (ui == null) return;
        var state = _stateRef(ui);
        if (state == null) return;
        var me = LocalContext.GetMe(state);
        if (me == null) return;

        ClearHand();
        _pilesRef(ui)?.Initialize(me);
        _starRef(ui)?.Initialize(me);
        ui.EndTurnButton?.Initialize(state);

        // 销毁旧能量计数器并按新英雄重建
        var old = _energyRef(ui);
        if (old != null && GodotObject.IsInstanceValid(old)) old.QueueFreeSafely();
        var newEnergy = NEnergyCounter.Create(me);
        _energyRef(ui) = newEnergy;
        ui.EnergyCounterContainer.AddChildSafely(newEnergy);
        _starRef(ui)?.Reparent(newEnergy);

        RebuildHand(me);
    }

    private static void ClearHand()
    {
        var hand = NPlayerHand.Instance;
        if (hand == null) return;
        var holders = hand.ActiveHolders.ToList();
        foreach (var h in holders) hand.RemoveCardHolder(h);
    }

    private static void RebuildHand(Player me)
    {
        var hand = NPlayerHand.Instance;
        if (hand == null || me.PlayerCombatState == null || _createCardNode == null) return;

        var handPile = PileType.Hand.GetPile(me);
        if (handPile == null) return;

        foreach (var card in handPile.Cards)
        {
            var nCard = (NCard?)_createCardNode.Invoke(null, new object[] { card, PileType.Hand, true });
            if (nCard != null) hand.Add(nCard);
        }
    }

    // ── 商店房支持 ─────────────────────────────────────────────────
    //
    // MerchantRoom 在进房时已为每名玩家独立滚动了一份 MerchantInventory，但
    // NMerchantRoom._Ready 只把货架绑定到队长那份，且 NMerchantInventory.Initialize
    // 带有“已填充过就不再执行”的一次性守卫。因此切座时直接把货架的 Inventory
    // 换成当前英雄那份，并对所有现有槽位重新 FillSlot，不再调用 Initialize。
    private static readonly AccessTools.FieldRef<NMerchantInventory, Control>? _rugCharCardsRef =
        AccessTools.FieldRefAccess<NMerchantInventory, Control>("_characterCardContainer");
    private static readonly AccessTools.FieldRef<NMerchantInventory, Control>? _rugColorlessRef =
        AccessTools.FieldRefAccess<NMerchantInventory, Control>("_colorlessCardContainer");
    private static readonly AccessTools.FieldRef<NMerchantInventory, Control>? _rugRelicsRef =
        AccessTools.FieldRefAccess<NMerchantInventory, Control>("_relicContainer");
    private static readonly AccessTools.FieldRef<NMerchantInventory, Control>? _rugPotionsRef =
        AccessTools.FieldRefAccess<NMerchantInventory, Control>("_potionContainer");
    private static readonly AccessTools.FieldRef<NMerchantInventory, NMerchantCardRemoval>? _rugRemovalRef =
        AccessTools.FieldRefAccess<NMerchantInventory, NMerchantCardRemoval>("_cardRemovalNode");
    private static readonly AccessTools.FieldRef<NMerchantInventory, NMerchantDialogue>? _rugDialogueRef =
        AccessTools.FieldRefAccess<NMerchantInventory, NMerchantDialogue>("_merchantDialogue");

    private static readonly PropertyInfo? _rugInventoryProp =
        typeof(NMerchantInventory).GetProperty(nameof(NMerchantInventory.Inventory));
    private static readonly MethodInfo? _rugSubscribeMethod =
        AccessTools.Method(typeof(NMerchantInventory), "SubscribeToEntries");
    // NMerchantSlot.Initialize 时向“当前货架玩家”订阅 Player.GoldChanged += UpdateVisual
    // （用于购买后价格置灰），_ExitTree 按“当时的”货架玩家退订。我们切换货架库存时
    // 必须把该订阅一并迁移，否则旧英雄身上会永久挂着已释放槽位的回调 —— 之后他
    // 获得金币（如战后金币奖励）时回调访问已释放的 MegaLabel 抛
    // ObjectDisposedException，使领奖链路在 SuccessfullySelected 置位前中断，
    // 表现为金币已加但奖励选项不消失、可反复领取。
    private static readonly MethodInfo? _slotUpdateVisualMethod =
        AccessTools.Method(typeof(NMerchantSlot), "UpdateVisual");

    private static Action BuildSlotUpdateVisual(NMerchantSlot slot) =>
        (Action)Delegate.CreateDelegate(typeof(Action), slot, _slotUpdateVisualMethod!);

    /// <summary>把已打开的商店界面重绑到当前英雄自己的那份滚动库存。</summary>
    public static void RefreshMerchantUi()
    {
        var roomNode = NRun.Instance?.MerchantRoom;
        var current = CurrentControlPlayer;
        if (roomNode == null || current == null) return;
        var rug = roomNode.Inventory;
        if (rug == null || !GodotObject.IsInstanceValid(rug)) return;

        try
        {
            // Room.GetLocalInventory() 按 LocalContext.GetMe 取库存，此时身份已切换
            var newInventory = roomNode.Room.GetLocalInventory();
            var oldInventory = _rugInventoryProp?.GetValue(rug) as MerchantInventory;
            if (oldInventory == null || oldInventory == newInventory) return;

            // 先把货架自身（以及对话气泡）对旧商品的 PurchaseCompleted/
            // PurchaseFailed 订阅全部退订……
            object? dialogue = null;
            try { dialogue = _rugDialogueRef?.Invoke(rug); } catch { /* 无关紧要 */ }
            foreach (var entry in oldInventory.AllEntries)
                PruneMerchantEntrySubscribers(entry, t => t == rug || (dialogue != null && t == dialogue));

            // 槽位集合在本次重绑中固定不变（Initialize 的一次性守卫使我们无法重调
            // slot.Initialize），先缓存，用于订阅迁移。
            var slots = rug.GetAllSlots().ToList();

            // ……再退订每个槽位对其当前商品的订阅，保证反复切座不会堆积重复回调
            foreach (var slot in slots)
            {
                if (slot.Entry is { } oldEntry)
                    PruneMerchantEntrySubscribers(oldEntry, t => t == slot);
                // 把槽位的金币变化回调从旧英雄身上摘除
                if (_slotUpdateVisualMethod != null)
                    oldInventory.Player.GoldChanged -= BuildSlotUpdateVisual(slot);
            }

            _rugInventoryProp!.GetSetMethod(nonPublic: true)!.Invoke(rug, new object?[] { newInventory });
            _rugSubscribeMethod?.Invoke(rug, null);

            RefillCardSlots(_rugCharCardsRef?.Invoke(rug), newInventory.CharacterCardEntries);
            RefillCardSlots(_rugColorlessRef?.Invoke(rug), newInventory.ColorlessCardEntries);
            RefillRelicSlots(_rugRelicsRef?.Invoke(rug), newInventory.RelicEntries);
            RefillPotionSlots(_rugPotionsRef?.Invoke(rug), newInventory.PotionEntries);
            var removal = _rugRemovalRef?.Invoke(rug);
            if (removal != null && newInventory.CardRemovalEntry != null)
            {
                ResetCardRemovalLatch(removal);
                removal.FillSlot(newInventory.CardRemovalEntry);
            }

            // 槽位的金币变化回调改挂到新英雄，购买/加金时价格置灰才对当前英雄生效；
            // 同时保证商店退出时 _ExitTree 能在正确的玩家身上退订干净。
            if (_slotUpdateVisualMethod != null)
                foreach (var slot in slots)
                    newInventory.Player.GoldChanged += BuildSlotUpdateVisual(slot);

            roomNode.MerchantButton.IsLocalPlayerDead = current.Creature.IsDead;
            AloneFightLog.Debug($"[AloneFight] 商店已重绑到英雄 {current.NetId}（{current.Character.Id.Entry}）。");
        }
        catch (Exception e)
        {
            AloneFightLog.Error("[AloneFight] 商店重绑失败", e);
        }
    }

    private static void RefillCardSlots(Control? container, IReadOnlyList<MerchantCardEntry> entries)
    {
        if (container == null) return;
        var slots = container.GetChildren().OfType<NMerchantCard>().ToList();
        for (int i = 0; i < slots.Count && i < entries.Count; i++)
        {
            ResetSlotLatch(slots[i]);
            slots[i].FillSlot(entries[i]);
        }
    }

    private static void RefillRelicSlots(Control? container, IReadOnlyList<MerchantRelicEntry> entries)
    {
        if (container == null) return;
        var slots = container.GetChildren().OfType<NMerchantRelic>().ToList();
        for (int i = 0; i < slots.Count && i < entries.Count; i++)
        {
            ResetSlotLatch(slots[i]);
            slots[i].FillSlot(entries[i]);
        }
    }

    private static void RefillPotionSlots(Control? container, IReadOnlyList<MerchantPotionEntry> entries)
    {
        if (container == null) return;
        var slots = container.GetChildren().OfType<NMerchantPotion>().ToList();
        for (int i = 0; i < slots.Count && i < entries.Count; i++)
        {
            ResetSlotLatch(slots[i]);
            slots[i].FillSlot(entries[i]);
        }
    }

    // 槽位的“已售罄”视觉状态是单向锁存：NMerchantCard/NMerchantRelic/NMerchantPotion
    // 的 UpdateVisual 在无货分支会 Visible=false、MouseFilter=Ignore，但有货分支
    // 从不恢复（原版同一槽位终生只卖一件商品）。我们复用槽位轮流展示每名英雄的
    // 库存，因此 P1 买走一件后切到其他英雄，该槽位会一直空白/不可点。FillSlot 前
    // 先恢复可交互外观；若新商品本身也已售罄，随后的 UpdateVisual 会再次置空。
    // ConditionalWeakTable 的值类型必须是引用类型，这里装箱保存原始鼠标过滤模式
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NMerchantSlot, object>
        _slotOriginalMouseFilter = new();

    private static void ResetSlotLatch(NMerchantSlot slot)
    {
        slot.Visible = true;
        slot.Modulate = Colors.White;
        if (!_slotOriginalMouseFilter.TryGetValue(slot, out var boxed))
        {
            boxed = slot.MouseFilter;
            _slotOriginalMouseFilter.Add(slot, boxed);
        }
        var filter = (Control.MouseFilterEnum)boxed;
        // 仅恢复被售罄分支改成 Ignore 的情形，保留场景原本可能的 Pass 设置
        slot.MouseFilter = filter == Control.MouseFilterEnum.Ignore ? Control.MouseFilterEnum.Stop : filter;
    }

    // 删卡服务槽位另有三重锁存：_isUnavailable 一旦为 true，UpdateVisual 直接早退；
    // 动画停在 "Used" 帧；命中框/焦点/价格容器全部关闭。换人重填前逐项复位。
    private static readonly FieldInfo? _removalUnavailableField =
        typeof(NMerchantCardRemoval).GetField("_isUnavailable", BindingFlags.Instance | BindingFlags.NonPublic);

    private static void ResetCardRemovalLatch(NMerchantCardRemoval removal)
    {
        ResetSlotLatch(removal);
        _removalUnavailableField?.SetValue(removal, false);
        removal.Hitbox.MouseFilter = Control.MouseFilterEnum.Stop;
        removal.FocusMode = Control.FocusModeEnum.All;

        var animator = removal.GetNodeOrNull<AnimationPlayer>("%Animation");
        if (animator != null)
        {
            var anim = animator.GetAnimationList()
                .Select(a => a.ToString())
                .FirstOrDefault(a => a != "Used");
            if (anim != null)
            {
                animator.Stop();
                animator.Play(anim);
            }
        }
    }

    /// <summary>
    /// 从一个 MerchantEntry 上移除目标匹配的事件处理器。
    /// MerchantEntry 使用字段支撑的普通事件（PurchaseCompleted /
    /// PurchaseFailed / EntryUpdated）。
    /// </summary>
    private static void PruneMerchantEntrySubscribers(MerchantEntry entry, Func<object?, bool> match)
    {
        foreach (var eventName in new[] { "PurchaseCompleted", "PurchaseFailed", "EntryUpdated" })
        {
            var field = typeof(MerchantEntry).GetField(
                eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field?.GetValue(entry) is not Delegate del) continue;

            Delegate? kept = del;
            foreach (var handler in del.GetInvocationList())
            {
                if (match(handler.Target))
                    kept = Delegate.Remove(kept, handler);
            }
            if (!ReferenceEquals(kept, del))
                field.SetValue(entry, kept);
        }
    }
}
