using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace AloneFight;

/// <summary>
/// AloneFight 模组的全部 Harmony 补丁。按文件顺序分为：
///
///  1. 主菜单     —— 在单人子菜单加入“独自多人战斗”入口按钮。
///  2. 选人界面   —— 多选 2~4 名英雄（左键 +1 / 右键 -1），并启动跑团。
///  3. 跑团生命周期 —— RunManager 启动/清理时激活或销毁队伍状态。
///  4. 怪物数值   —— （已停用）怪物血量按队伍实际人数走官方多人缩放。（没人说四人队伍怪物血量这么高啊qwq，注释打开重新编译即可恢复1人难度。
///  5. 战斗回合   —— 点一次结束回合，所有存活英雄同时就绪。
///  6. 座位切换   —— Tab 键循环 + 左上角头像点击直达；切换后重绑房间界面。
///  7. 战后奖励   —— 每次只显示一名英雄的奖励、可自由轮换；一次继续代表全员
///                   结算（普通房全员放弃未领奖励；Boss/胜利房全员投换幕票）。
///  8. 队伍栏     —— 当前英雄高亮，头像点击接线。
///  9. 篝火房     —— 各英雄独立选择篝火选项并自动轮换。
/// 10. 事件房     —— 各英雄独立选择事件选项（重写 LocalPlayer 取值）。
/// 11. 地图投票   —— 一名英雄投地图节点，全队跟随同票。
/// 12. 换幕投票   —— 一名英雄投换幕就绪票，全队同时就绪，进入下一层。
/// 13. 宝箱遗物   —— 清除游戏的自动随机预投票，让每名英雄依次亲自选遗物。
/// 14. 宝箱金币   —— 开箱金币发放给全队（空网络不会转发队友消息）。
/// 15. 通关进度   —— 多英雄跑团通关时，临时把队长 NetId 伪装成本地平台 id，
///                   使通关聚合进度（连胜/统计等）正常写入存档。
///
/// 跨模块的状态统一下放在 <see cref="AloneFightManager"/>，本类只包含
/// 对游戏侧的拦截点。
/// </summary>
[HarmonyPatch]
internal static class AloneFightPatches
{
    // ════════════════════════════════════════════════════════════════
    // 1. 主菜单 —— 在单人子菜单中加入“独自多人战斗”入口
    // ════════════════════════════════════════════════════════════════

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NSingleplayerSubmenu), MethodType.Constructor)]
    static void PostfixSingleplayerSubmenuCtor(NSingleplayerSubmenu __instance)
    {
        // 子菜单场景在 _Ready 之前就实例化了按钮；延迟一帧等“标准模式”按钮
        // 进入场景树后再克隆它。
        Callable.From(() => AddAloneFightButton(__instance)).CallDeferred();
    }

    private static void AddAloneFightButton(NSingleplayerSubmenu submenu)
    {
        if (!GodotObject.IsInstanceValid(submenu)) return;

        var standardField = AccessTools.Field(typeof(NSingleplayerSubmenu), "_standardButton");
        if (standardField?.GetValue(submenu) is not NSubmenuButton standard || !GodotObject.IsInstanceValid(standard))
            return;

        var parent = standard.GetParent();
        if (parent == null || parent.HasNode("AloneFightButton")) return;

        var dailyField = AccessTools.Field(typeof(NSingleplayerSubmenu), "_dailyButton");
        var customField = AccessTools.Field(typeof(NSingleplayerSubmenu), "_customButton");
        var existing = new List<NSubmenuButton> { standard };
        if (dailyField?.GetValue(submenu) is NSubmenuButton daily) existing.Add(daily);
        if (customField?.GetValue(submenu) is NSubmenuButton custom) existing.Add(custom);

        var clone = (NSubmenuButton)standard.Duplicate();
        clone.Name = "AloneFightButton";

        // NSubmenuButton 的悬停高亮由 BgPanel 上的 ShaderMaterial 驱动。
        // 克隆体会与原按钮共享同一份材质资源，导致悬停克隆按钮时“标准模式”
        // 也一起高亮 —— 必须给克隆体复制一份独立材质。
        var cloneBg = clone.GetNode<Control>("BgPanel");
        if (cloneBg.Material != null)
            cloneBg.Material = (Material)cloneBg.Material.Duplicate();

        parent.AddChildSafely(clone); // 触发克隆体的 _Ready → ConnectSignals

        if (AccessTools.Field(typeof(NSubmenuButton), "_title")?.GetValue(clone) is MegaLabel title)
            title.SetTextAutoSize("独自多人战斗");
        if (AccessTools.Field(typeof(NSubmenuButton), "_description")?.GetValue(clone) is MegaRichTextLabel desc)
            desc.Text = "选择 2~4 名英雄（可重复），在本地轮流操控整支小队";

        clone.Connect(NClickableControl.SignalName.Released,
            Callable.From<NButton>(_ => OpenAloneFightCharacterSelect(submenu)));

        LayoutButtonsInARow(existing, clone);
    }

    /// <summary>
    /// 子菜单场景的按钮是手工定位的。把全部按钮沿原来的行（或列）重新均布，
    /// 使新入口与标准/每日/自定义按钮真正并排，而不是叠在或挤在它们下方。
    /// </summary>
    private static void LayoutButtonsInARow(List<NSubmenuButton> existing, NSubmenuButton clone)
    {
        var all = existing.Concat(new[] { clone }).ToList();
        int n = all.Count;
        float size = existing[0].Size.X;
        float gap = 0f;

        float minX = all.Min(b => b.Position.X);
        float minY = all.Min(b => b.Position.Y);
        float maxRight = all.Max(b => b.Position.X + b.Size.X);
        float maxBottom = all.Max(b => b.Position.Y + b.Size.Y);
        bool horizontal = (maxRight - minX) >= (maxBottom - minY);

        if (horizontal)
        {
            // 推算原按钮间距（克隆体与标准按钮重叠，不参与计算）
            var xs = existing.Select(b => b.Position.X).OrderBy(v => v).ToList();
            if (xs.Count >= 2)
                gap = xs[1] - xs[0] - size;

            float avail = clone.GetParent() is Control pc && pc.Size.X > 0 ? pc.Size.X - minX : float.MaxValue;
            float total = n * size + (n - 1) * Mathf.Max(gap, 0f);
            if (total > avail)
                gap = Mathf.Max(0f, (avail - n * size) / (n - 1)); // 空间不够就压缩间距，绝不重叠

            float y = existing[0].Position.Y;
            for (int i = 0; i < n; i++)
                all[i].Position = new Vector2(minX + i * (size + gap), y);
        }
        else
        {
            float h = existing[0].Size.Y;
            var ys = existing.Select(b => b.Position.Y).OrderBy(v => v).ToList();
            if (ys.Count >= 2)
                gap = ys[1] - ys[0] - h;
            float top = ys.FirstOrDefault();
            float x = existing[0].Position.X;
            for (int i = 0; i < n; i++)
                all[i].Position = new Vector2(x, top + i * (h + Mathf.Max(gap, 0f)));
        }
    }

    private static void OpenAloneFightCharacterSelect(NSingleplayerSubmenu submenu)
    {
        var stackField = AccessTools.Field(typeof(NSubmenu), "_stack");
        if (stackField?.GetValue(submenu) is not NSubmenuStack stack) return;

        var getMethod = stack.GetType().GetMethods()
            .First(m => m.Name == "GetSubmenuType" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
            .MakeGenericMethod(typeof(NCharacterSelectScreen));
        var screen = (NCharacterSelectScreen)getMethod.Invoke(stack, null)!;

        screen.InitializeSingleplayer();
        AloneFightManager.BeginPartySelection();
        stack.Push(screen);
    }

    // ════════════════════════════════════════════════════════════════
    // 2. 选人界面 —— 多选 2~4 名英雄并启动 AloneFight 跑团
    // ════════════════════════════════════════════════════════════════

    private static readonly AccessTools.FieldRef<NCharacterSelectScreen, Control> CharContainerRef =
        AccessTools.FieldRefAccess<NCharacterSelectScreen, Control>("_charButtonContainer");
    private static readonly AccessTools.FieldRef<NCharacterSelectScreen, NConfirmButton> EmbarkButtonRef =
        AccessTools.FieldRefAccess<NCharacterSelectScreen, NConfirmButton>("_embarkButton");
    private static readonly AccessTools.FieldRef<NCharacterSelectScreen, NAscensionPanel> AscensionPanelRef =
        AccessTools.FieldRefAccess<NCharacterSelectScreen, NAscensionPanel>("_ascensionPanel");

    private static readonly AccessTools.FieldRef<NCharacterSelectButton, bool> ButtonSelectedRef =
        AccessTools.FieldRefAccess<NCharacterSelectButton, bool>("_isSelected");
    private static readonly MethodInfo ButtonRefreshState =
        AccessTools.Method(typeof(NCharacterSelectButton), "RefreshState");
    private static readonly MethodInfo CleanUpLobbyMethod =
        AccessTools.Method(typeof(NCharacterSelectScreen), "CleanUpLobby");

    private static NCharacterSelectScreen? _activeSelectScreen;

    // 选择数量只以 MousePressed 信号为准（左键 +1、右键 -1）。
    // 原版 SelectCharacter 路径保持不动，供键盘/手柄使用；这里只拦截鼠标点击。
    private const string WiredMeta = "alonefight_wired";

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NCharacterSelectScreen), "InitCharacterButtons")]
    static void PostfixInitCharacterButtons(NCharacterSelectScreen __instance)
    {
        foreach (var button in CharContainerRef(__instance).GetChildren().OfType<NCharacterSelectButton>())
        {
            // 每次 Init 都会重建按钮，这里仍加标记防止重复接线
            if (button.HasMeta(WiredMeta)) continue;
            button.SetMeta(WiredMeta, true);
            // 左键：增加一份；右键：减少一份
            button.Connect(NClickableControl.SignalName.MousePressed,
                Callable.From<InputEvent>(e => OnCharacterMousePressed(button, e)));
            EnsureCountBadge(button);
        }
    }

    /// <summary>
    /// 角色按钮右上角金色“×N”选择数量角标。使用游戏自带的 MegaLabel，
    /// 以保证项目文本系统能正常渲染。选人界面会被缓存并反复推入，
    /// 因此角标按名称懒创建、可复用。
    /// </summary>
    private static MegaLabel EnsureCountBadge(NCharacterSelectButton button)
    {
        var existing = button.GetNodeOrNull<MegaLabel>("AloneFightCountBadge");
        if (existing != null) return existing;

        var badge = new MegaLabel
        {
            Name = "AloneFightCountBadge",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ZIndex = 100,
            GrowHorizontal = Control.GrowDirection.Begin
        };

        // MegaLabel 没有显式字体主题覆盖时会直接抛异常
        // （MegaLabelHelper.AssertThemeFontOverride 的引擎防护）。
        // 优先借用按钮上已有标签的字体/字号，取不到则回退到控件主题默认值。
        Font? font = null;
        int? fontSize = null;
        foreach (var child in button.FindChildren("*", nameof(MegaLabel), true, false))
        {
            if (child is MegaLabel ml && ml.HasThemeFontOverride("font"))
            {
                font = ml.GetThemeFont("font");
                if (ml.HasThemeFontSizeOverride("font_size"))
                    fontSize = ml.GetThemeFontSize("font_size");
                break;
            }
        }
        font ??= button.GetThemeDefaultFont();
        if (font != null)
            badge.AddThemeFontOverride("font", font);
        badge.AddThemeFontSizeOverride("font_size", fontSize ?? button.GetThemeDefaultFontSize());

        // 固定尺寸的右上角角标框，文字由 MegaLabel 自动适配
        badge.AnchorLeft = badge.AnchorRight = 1f;
        badge.AnchorTop = badge.AnchorBottom = 0f;
        badge.OffsetLeft = -78f;
        badge.OffsetRight = -12f;
        badge.OffsetTop = 10f;
        badge.OffsetBottom = 54f;
        badge.AddThemeColorOverride("font_color", Colors.Gold);
        badge.AddThemeColorOverride("font_outline_color", Colors.Black);
        badge.AddThemeConstantOverride("outline_size", 8);
        button.AddChild(badge);
        return badge;
    }

    private static void OnCharacterMousePressed(NCharacterSelectButton button, InputEvent inputEvent)
    {
        if (!AloneFightManager.IsSelectingParty || _activeSelectScreen == null) return;
        if (inputEvent is not InputEventMouseButton { Pressed: true } mouse) return;
        if (button.Character == null || button.IsLocked || button.IsRandom) return;

        bool right = mouse.ButtonIndex == MouseButton.Right;
        if (!right && mouse.ButtonIndex != MouseButton.Left) return;

        var chosen = AloneFightManager.PendingParty;
        if (right)
            chosen.Remove(button.Character); // 减少一份
        else if (chosen.Count < AloneFightManager.MaxPartySize)
            chosen.Add(button.Character);

        SyncSelectionState(_activeSelectScreen);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NCharacterSelectScreen), "OnSubmenuOpened")]
    static void PostfixCharSelectOpened(NCharacterSelectScreen __instance)
    {
        if (!AloneFightManager.IsSelectingParty) return;
        _activeSelectScreen = __instance;
        SyncSelectionState(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NCharacterSelectScreen), "OnSubmenuClosed")]
    static void PostfixCharSelectClosed()
    {
        _activeSelectScreen = null;
        if (AloneFightManager.IsSelectingParty)
            AloneFightManager.EndPartySelection();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NCharacterSelectScreen), "OnEmbarkPressed")]
    static bool PrefixEmbarkPressed(NCharacterSelectScreen __instance)
    {
        if (!AloneFightManager.IsSelectingParty) return true;
        if (AloneFightManager.PendingParty.Count < AloneFightManager.MinPartySize) return false;

        TaskHelper.RunSafely(StartPartyRunFromSelect(__instance));
        return false;
    }

    private static async Task StartPartyRunFromSelect(NCharacterSelectScreen screen)
    {
        var characters = AloneFightManager.PendingParty.ToList();
        int ascension = AscensionPanelRef(screen).Ascension;

        string seed = SeedHelper.GetRandomSeed();
        var unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();
        var rng = new Rng((uint)StringHelper.GetDeterministicHashCode(seed));
        List<ActModel> acts = ActModel.GetRandomList(rng, unlockState, isMultiplayer: false).ToList();

        EmbarkButtonRef(screen).Disable();
        NAudioManager.Instance?.StopMusic();
        SfxCmd.Play(characters[0].CharacterTransitionSfx);
        if (NGame.Instance is not { } game) return;
        await game.Transition.FadeOut(0.8f, characters[0].CharacterSelectTransitionPath);

        await AloneFightManager.StartPartyRun(characters, ascension, seed, acts);

        AloneFightManager.EndPartySelection();
        // CleanUpLobby(bool disconnectSession, NetError error = Quit)：
        // 反射 Invoke 不会自动填充可选参数，必须按参数个数显式补齐默认值。
        if (CleanUpLobbyMethod != null)
        {
            var ps = CleanUpLobbyMethod.GetParameters();
            var args = new object[ps.Length];
            args[0] = false;
            for (int i = 1; i < ps.Length; i++)
                args[i] = ps[i].DefaultValue ?? Activator.CreateInstance(ps[i].ParameterType)!;
            try { CleanUpLobbyMethod.Invoke(screen, args); }
            catch (System.Exception e) { AloneFightLog.Error("[AloneFight] CleanUpLobby 调用失败", e); }
        }
    }

    private static void SyncSelectionState(NCharacterSelectScreen screen)
    {
        var chosen = AloneFightManager.PendingParty!;
        foreach (var button in CharContainerRef(screen).GetChildren().OfType<NCharacterSelectButton>())
        {
            int count = button.Character != null ? chosen.Count(c => c == button.Character) : 0;

            if (count > 0)
            {
                // 先刷新出“已选中”外观……
                ButtonSelectedRef(button) = true;
                ButtonRefreshState.Invoke(button, null);
                // ……再释放私有标记，让 Select() 的 `!_isSelected` 守卫允许
                // 下一次点击/按键继续追加一份。缓存的视觉状态仍保持选中。
                ButtonSelectedRef(button) = false;
            }
            else if (button.IsSelected)
            {
                button.Deselect();
            }

            var badge = EnsureCountBadge(button);
            badge.SetTextAutoSize($"×{count}");
            badge.Visible = count >= 2;
        }

        if (chosen.Count >= AloneFightManager.MinPartySize)
            EmbarkButtonRef(screen).Enable();
        else
            EmbarkButtonRef(screen).Disable();
    }

    // ════════════════════════════════════════════════════════════════
    // 3. 跑团生命周期 —— 激活 / 销毁队伍状态
    //
    // RunManager.Launch 是游戏侧唯一写 LocalContext.NetId 的地方；其后置补丁
    // 在检测到跑团包含多于 1 名英雄时接管。CleanUp 时恢复原 NetId 并清空全部
    // AloneFight 状态（同时重置奖励轮换流程）。
    // ════════════════════════════════════════════════════════════════

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
    static void PostfixLaunch(RunState __result)
    {
        AloneFightManager.TryInitializeFromRun(__result);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
    static void PrefixCleanUp()
    {
        _rewardFlowActive = false;
        _rewardSeats.Clear();
        AloneFightManager.CleanUp();
    }

    // ════════════════════════════════════════════════════════════════
    // 已停用
    // 4. 怪物数值 —— 锁定 1 人难度，怪物血量按1人进入血量。
    //    （2~4）走游戏官方多人缩放公式。如需恢复 1 人难度，取消下方注释即可。
    // ════════════════════════════════════════════════════════════════

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Creature), nameof(Creature.ScaleMonsterHpForMultiplayer))]
    static void PrefixScaleMonsterHp(ref int playerCount)
    {
        if (AloneFightManager.IsActive)
        {
            playerCount = 1;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // 5. 战斗回合 —— 点一次结束回合即代表所有英雄就绪
    // ════════════════════════════════════════════════════════════════

    private static readonly AccessTools.FieldRef<CombatManager, HashSet<Player>> ReadyToEndTurnRef =
        AccessTools.FieldRefAccess<CombatManager, HashSet<Player>>("_playersReadyToEndTurn");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetReadyToEndTurn))]
    static void PrefixSetReadyToEndTurn(CombatManager __instance, Player player)
    {
        if (!AloneFightManager.IsActive || AloneFightManager.PartyPlayers == null)
            return;

        // 提前把其他队员全部塞进就绪集合。原方法随后会把当前英雄也加进去，
        // 并触发 AllPlayersReadyToEndTurn（集合满员后经单人快捷分支返回 true）。
        var readyList = ReadyToEndTurnRef(__instance);
        foreach (var p in AloneFightManager.PartyPlayers)
        {
            if (p != player && !p.Creature.IsDead && !readyList.Contains(p))
            {
                readyList.Add(p);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    // 6. 座位切换 —— Tab 循环、头像点击直达
    //
    // 两种输入最终走同一套切换后刷新：身份切换由 AloneFightManager 完成后，
    // 房间专属界面需要跟随重绑（篝火选项、事件页/对话、商店货架）。宝箱房和
    // 普通地图无需额外处理（身份随切换本身已经就位）。战后奖励阶段，两种输入
    // 改为在各自独立的奖励界面之间轮换/直达。
    // ════════════════════════════════════════════════════════════════

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
    static void PostfixGameInput(InputEvent inputEvent)
    {
        if (!AloneFightManager.IsActive)
            return;

        if (inputEvent is not InputEventKey key || !key.Pressed || key.Echo)
            return;

        if (key.Keycode != Key.Tab)
            return;

        // 战后奖励轮换：Tab 关闭当前英雄的奖励界面，打开下一名英雄自己的
        // 独立奖励，不领取也不跳过任何东西，随时可以切回来。当奖励界面上方
        // 还压着子界面（选卡、遗物奖励等）时忽略。
        if (_rewardFlowActive)
        {
            if (_rewardSeats.Count > 1 && _currentRewardScreen != null
                && NOverlayStack.Instance is { } stack && stack.Peek() == _currentRewardScreen)
            {
                stack.Remove(_currentRewardScreen);
                _rewardViewIndex = (_rewardViewIndex + 1) % _rewardSeats.Count;
                OpenViewingRewardScreen();
            }
            return;
        }

        if (!CanSwitchInCurrentCombat())
            return;

        AloneFightManager.RotateToNext();
        RefreshSeatRoomUi();
    }

    /// <summary>战斗中敌方行动等锁定时段切座会导致动画错乱，此时禁止切换。</summary>
    private static bool CanSwitchInCurrentCombat()
    {
        var combat = CombatManager.Instance;
        return combat == null || !combat.IsInProgress || !combat.PlayerActionsDisabled;
    }

    /// <summary>
    /// 切换操控英雄后按所在房间重绑对应界面。Tab 循环与头像点击共用。
    /// 宝箱房和地图无需额外处理（身份已随切换就位）。
    /// </summary>
    private static void RefreshSeatRoomUi()
    {
        if (NRestSiteRoom.Instance != null)
        {
            AloneFightManager.RefreshRestSiteUi();
            return;
        }
        if (NEventRoom.Instance != null)
        {
            AloneFightManager.RefreshEventUi();
            return;
        }
        if (NRun.Instance?.MerchantRoom != null)
        {
            AloneFightManager.RefreshMerchantUi();
        }
    }

    /// <summary>
    /// 直接切换到左上角被点击头像对应的英雄（头像左键点击的入口）。
    /// </summary>
    public static void SwitchSeatToPlayer(Player target)
    {
        if (!AloneFightManager.IsActive) return;

        // 奖励轮换阶段：直接跳到该英雄自己的奖励界面
        if (_rewardFlowActive && _rewardSeats.Count > 1 && _currentRewardScreen != null
            && NOverlayStack.Instance is { } stack && stack.Peek() == _currentRewardScreen)
        {
            int idx = _rewardSeats.FindIndex(s => s.Player == target);
            if (idx >= 0 && idx != _rewardViewIndex)
            {
                stack.Remove(_currentRewardScreen);
                _rewardViewIndex = idx;
                OpenViewingRewardScreen();
            }
            return;
        }

        if (!CanSwitchInCurrentCombat()) return;
        if (target.Creature.IsDead || target == AloneFightManager.CurrentControlPlayer) return;

        AloneFightManager.SwitchTo(target);
        RefreshSeatRoomUi();
    }

    // 战斗开始后延迟刷新明暗，确保所有玩家单位节点都已生成
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
    static void PostfixSetUpCombat()
    {
        if (!AloneFightManager.IsActive) return;
        TaskHelper.RunSafely(RefreshForegroundAfterCombatSetup());
    }

    private static async Task RefreshForegroundAfterCombatSetup()
    {
        await Task.Delay(300);
        AloneFightManager.BringCurrentHeroToFront();
    }

    // ════════════════════════════════════════════════════════════════
    // 7. 战后奖励 —— 一次一屏、自由轮换（Tab / 头像点击）
    //
    // 一名玩家操控整支队伍（不是人人必须确认的热座模式）。战斗结束后每名
    // 存活英雄都有自己独立的一份 RewardsSet（按槽位顺序滚动，与原版一致），
    // 注册在各自的同步器栈上。任意时刻只存在一个 NRewardsScreen：Tab 关闭
    // 当前英雄的界面（不领取、不跳过），打开下一名英雄的同一套奖励流程；
    // 随时可以切回。一次“继续”代表全员结算 —— 未领取的奖励视为放弃
    // （等同于各自跳过），随后打开地图。
    // ════════════════════════════════════════════════════════════════

    private sealed class RewardSeat
    {
        public Player Player = null!;
        public RewardsSet Set = null!;
    }

    private static readonly List<RewardSeat> _rewardSeats = new();
    private static int _rewardViewIndex;
    private static NRewardsScreen? _currentRewardScreen;
    private static bool _rewardFlowActive;

    private static readonly AccessTools.FieldRef<RewardsSetSynchronizer, ulong> SyncLocalPlayerIdRef =
        AccessTools.FieldRefAccess<RewardsSetSynchronizer, ulong>("_localPlayerId");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CombatRoom), nameof(CombatRoom.OfferRoomEndRewards))]
    static bool PrefixOfferRoomEndRewards(CombatRoom __instance, ref Task __result)
    {
        if (!AloneFightManager.IsActive || AloneFightManager.PartyPlayers == null)
            return true;

        __result = ShowRewardCycle(__instance);
        return false;
    }

    private static async Task ShowRewardCycle(CombatRoom room)
    {
        var players = AloneFightManager.PartyPlayers!;
        var sync = RunManager.Instance.RewardsSetSynchronizer;

        // 第一阶段：按槽位顺序预先滚动每名英雄的奖励（与原版 RNG 顺序一致）
        var sets = new List<RewardsSet>(players.Count);
        foreach (var p in players)
            sets.Add(await RewardsCmd.GenerateForRoomEnd(p, room));

        // 第二阶段：把每名存活英雄的奖励集注册到同步器，但不通过 Offer()
        // 弹任何界面 —— 界面完全由我们自己驱动。
        _rewardSeats.Clear();
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (p.Creature.IsDead) continue;

            LocalContext.NetId = p.NetId;
            SyncLocalPlayerIdRef(sync) = p.NetId;
            _ = sync.BeginRewardsSet(sets[i]);
            _rewardSeats.Add(new RewardSeat { Player = p, Set = sets[i] });
        }

        if (_rewardSeats.Count == 0)
        {
            AloneFightLog.Info("[AloneFight] 战后奖励：无存活英雄，跳过奖励界面。");
            return;
        }

        _rewardViewIndex = 0;
        _rewardFlowActive = true;
        AloneFightLog.Info($"[AloneFight] 战后奖励轮换开始，参与英雄 {_rewardSeats.Count} 名。");
        OpenViewingRewardScreen();
    }

    /// <summary>为当前查看的英雄构建一个全新的 NRewardsScreen。</summary>
    private static void OpenViewingRewardScreen()
    {
        var seat = _rewardSeats[_rewardViewIndex];
        var p = seat.Player;

        // 再次切回某英雄时，移除他已经领过的奖励 —— 同步器里仍保留已选标记，
        // 但重建的界面不能再把这些奖励的按钮显示出来。
        seat.Set.Rewards.RemoveAll(r => r.SuccessfullySelected);

        SeatRewardIdentity(p);
        _currentRewardScreen = NRewardsScreen.ShowScreen(seat.Set, isTerminal: true, p.RunState);
    }

    /// <summary>把两个本地身份都指向当前查看的英雄并刷新 HUD。</summary>
    private static void SeatRewardIdentity(Player p)
    {
        LocalContext.NetId = p.NetId;
        SyncLocalPlayerIdRef(RunManager.Instance.RewardsSetSynchronizer) = p.NetId;
        AloneFightManager.FollowRewardHero(p);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NRewardsScreen), "OnProceedButtonPressed")]
    static bool PrefixRewardsProceedButton(NRewardsScreen __instance)
    {
        if (!_rewardFlowActive || __instance != _currentRewardScreen)
            return true; // 嵌套的奖励子界面（遗物获得等）仍走原版

        // 与原版 NRewardsScreen 对齐：Boss/胜利终端奖励界面不调用
        // ProceedFromTerminalRewardsScreen，而是由每名玩家投换幕就绪票
        // （特殊的“第二个 Boss”节点除外）。
        var runState = _rewardSeats[0].Player.RunState;
        var currentRoom = runState.CurrentRoom;
        bool votesForActChange = currentRoom != null
            && (currentRoom.RoomType == RoomType.Boss || currentRoom.IsVictoryRoom)
            && !(runState.Map.SecondBossMapPoint != null
                 && runState.CurrentMapCoord == runState.Map.BossMapPoint.coord);

        var seats = _rewardSeats.ToList();
        var screen = _currentRewardScreen;
        _rewardFlowActive = false;
        _currentRewardScreen = null;
        _rewardSeats.Clear();
        NOverlayStack.Instance?.Remove(screen);

        AloneFightLog.Info(votesForActChange
            ? "[AloneFight] 全员确认终端奖励：投换幕票进入下一层。"
            : $"[AloneFight] 全员确认终端奖励：结算 {seats.Count} 个奖励集并推进房间。");

        if (votesForActChange)
        {
            // 每个队伍槽位都必须投票，包括没有奖励席位的阵亡英雄 ——
            // _readyPlayers 要求每个玩家槽位都有一个 true。
            // 期间抑制换幕跟随的后置补丁：这里入队的投票动作执行后自然齐票，
            // MoveToNextAct 只会触发一次。
            _drivingActVotes = true;
            try
            {
                foreach (var p in AloneFightManager.PartyPlayers!)
                {
                    LocalContext.NetId = p.NetId;
                    RunManager.Instance.ActChangeSynchronizer.SetLocalPlayerReady();
                }
            }
            finally
            {
                _drivingActVotes = false;
            }
        }
        else
        {
            var sync = RunManager.Instance.RewardsSetSynchronizer;

            // 一次确认结算所有英雄 —— 未领的奖励视为放弃，与逐个跳过等价
            foreach (var seat in seats)
            {
                if (sync.IsRewardsSetCompleted(seat.Set)) continue;
                LocalContext.NetId = seat.Player.NetId;
                SyncLocalPlayerIdRef(sync) = seat.Player.NetId;
                sync.SkipLocalRewardsSet();
            }
            TaskHelper.RunSafely(RunManager.Instance.ProceedFromTerminalRewardsScreen());
        }

        AloneFightManager.ResetControlToLeader();
        return false;
    }

    // ── 药水奖励图标：切座重建界面后缓存节点已释放的问题 ────────────
    //
    // PotionReward.CreateIcon 把 NPotion 图标节点缓存在 _icon 中只创建一次。
    // 原版一个奖励界面只构建一次所以无碍；但我们切换查看英雄时会销毁旧的
    // NRewardsScreen（图标节点随之被释放）再重建，第二次 Reload 取到的是已释放
    // 的缓存节点，AddChildSafely 抛 ObjectDisposedException，Reload 在设置文本
    // 之前中断 —— 按钮只剩场景自带的英文占位文本，表现为“药水图标不显示、
    // 选项变成英文”。此处检测到缓存节点已失效时重建并写回缓存。
    private static readonly AccessTools.FieldRef<PotionReward, Control?> PotionRewardIconRef =
        AccessTools.FieldRefAccess<PotionReward, Control?>("_icon");

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PotionReward), nameof(PotionReward.CreateIcon))]
    static void PostfixPotionRewardCreateIcon(PotionReward __instance, ref Control? __result)
    {
        if (__result == null || GodotObject.IsInstanceValid(__result))
            return;

        var fresh = NPotion.Create(__instance.Potion!);
        PotionRewardIconRef(__instance) = fresh;
        __result = fresh;
    }

    // ════════════════════════════════════════════════════════════════
    // 8. 队伍栏 —— 当前英雄高亮 + 头像点击切换座位
    //
    // 左上角队伍栏在整局游戏中全局可见（仅打开自身详情面板或设置时滑出），
    // 因此头像点击在所有房间都可用：地图、战斗、事件、篝火、商店、宝箱、奖励。
    //
    //   • 左键点击其他英雄   → 直接切换为操控该英雄
    //   • 左键点击当前/阵亡英雄 → 打开原版详情查看
    //   • 右键点击任意英雄   → 打开原版详情查看（血量/牌组/药水等）
    //   • 键盘聚焦后确认     → 保持原版详情查看，不受影响
    // ════════════════════════════════════════════════════════════════

    private const string PortraitWiredMeta = "alonefight_portrait_wired";

    // 记录每个头像最近一次“左键按下”的帧号，用于在 OnRelease 补丁中
    // 区分鼠标点击与键盘激活。
    private static readonly Dictionary<NMultiplayerPlayerState, ulong> _portraitLeftPressFrame = new();
    private const ulong PortraitClickMaxAgeFrames = 30;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NMultiplayerPlayerStateContainer), nameof(NMultiplayerPlayerStateContainer.Initialize))]
    static void PostfixPlayerStateContainerInit(NMultiplayerPlayerStateContainer __instance)
    {
        foreach (var state in __instance.GetChildren().OfType<NMultiplayerPlayerState>())
        {
            if (state.HasMeta(PortraitWiredMeta)) continue;
            state.SetMeta(PortraitWiredMeta, true);
            var captured = state;
            state.Hitbox.Connect(NClickableControl.SignalName.MousePressed,
                Callable.From<InputEvent>(e => OnPortraitMousePressed(captured, e)));
        }
        AloneFightManager.RefreshPartyHighlight();
    }

    private static void OnPortraitMousePressed(NMultiplayerPlayerState state, InputEvent inputEvent)
    {
        if (!AloneFightManager.IsActive || inputEvent is not InputEventMouseButton mb || !mb.Pressed)
            return;

        if (mb.ButtonIndex == MouseButton.Left)
        {
            _portraitLeftPressFrame[state] = Engine.GetProcessFrames();
            return;
        }
        if (mb.ButtonIndex == MouseButton.Right)
        {
            OpenPlayerInspection(state.Player);
        }
    }

    // 原版 OnRelease 会打开详情面板。这里拦截对“其他存活英雄”的左键点击，
    // 改为切换控制权。
    [HarmonyPrefix]
    [HarmonyPatch(typeof(NMultiplayerPlayerState), "OnRelease")]
    static bool PrefixPlayerStateRelease(NMultiplayerPlayerState __instance)
    {
        if (!AloneFightManager.IsActive)
            return true;

        // 卡牌/药水选目标进行中绝不干预……
        var targeting = NTargetManager.Instance;
        if (targeting.IsInSelection || targeting.LastTargetingFinishedFrame == __instance.GetTree().GetFrame())
            return true;

        // ……键盘激活也不干预（该头像近期没有左键按下记录）
        if (!_portraitLeftPressFrame.TryGetValue(__instance, out var pressFrame)
            || Engine.GetProcessFrames() - pressFrame > PortraitClickMaxAgeFrames)
            return true;

        var target = __instance.Player;
        if (target.Creature.IsDead || target == AloneFightManager.CurrentControlPlayer)
            return true; // 自己 / 阵亡英雄：打开原版详情

        SwitchSeatToPlayer(target);
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NMultiplayerPlayerState), "OnRelease")]
    static void PostfixPlayerStateRelease(NMultiplayerPlayerState __instance)
    {
        _portraitLeftPressFrame.Remove(__instance);
    }

    /// <summary>打开与原版点击完全相同的玩家详情面板。</summary>
    private static void OpenPlayerInspection(Player player)
    {
        var targeting = NTargetManager.Instance;
        if (targeting.IsInSelection
            || targeting.LastTargetingFinishedFrame == NGame.Instance?.GetTree().GetFrame())
            return;
        NCapstoneContainer.Instance?.Open(NMultiplayerPlayerExpandedState.Create(player));
    }

    // ════════════════════════════════════════════════════════════════
    // 9. 篝火房 —— 重写 LocalPlayer 实现逐人选择 + 选完自动轮换
    // ════════════════════════════════════════════════════════════════

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RestSiteSynchronizer), "LocalPlayer", MethodType.Getter)]
    static void PostfixRestSiteLocalPlayer(ref Player __result)
    {
        if (AloneFightManager.IsActive && AloneFightManager.CurrentControlPlayer != null)
            __result = AloneFightManager.CurrentControlPlayer;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NRestSiteRoom), "OnAfterPlayerSelectedRestSiteOption")]
    static void PostfixAfterRestSiteChoice(bool success)
    {
        if (!AloneFightManager.IsActive || !success)
            return;
        // 自动切到下一名还有未选篝火选项的英雄
        AloneFightManager.RotateToNextWithRestSiteOptions();
    }

    // ════════════════════════════════════════════════════════════════
    // 10. 事件房 —— 重写 LocalPlayer 实现逐人选择
    //
    // 每名英雄持有独立的 EventModel（EventSynchronizer.GetEventForPlayer）；
    // 该取值补丁保证选项点击作用于当前座位英雄。事件页/对话/选项的实际重建
    // 在 AloneFightManager.RefreshEventUi 中完成。
    // ════════════════════════════════════════════════════════════════

    [HarmonyPostfix]
    [HarmonyPatch(typeof(EventSynchronizer), "LocalPlayer", MethodType.Getter)]
    static void PostfixEventLocalPlayer(ref Player __result)
    {
        if (AloneFightManager.IsActive && AloneFightManager.CurrentControlPlayer != null)
            __result = AloneFightManager.CurrentControlPlayer;
    }

    // ════════════════════════════════════════════════════════════════
    // 11. 地图投票 —— 当前英雄投地图节点后，全队跟随投同一节点
    // ════════════════════════════════════════════════════════════════

    private static int _mapVoteDepth;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MapSelectionSynchronizer), nameof(MapSelectionSynchronizer.PlayerVotedForMapCoord))]
    static void PostfixPlayerVotedForMapCoord(
        MapSelectionSynchronizer __instance, Player player, MapLocation source, MapVote? destination)
    {
        if (!AloneFightManager.IsActive || AloneFightManager.PartyPlayers == null)
            return;
        if (!destination.HasValue)
            return;
        if (_mapVoteDepth > 0)
            return; // 防止跟随投票再次触发自身造成递归

        _mapVoteDepth++;
        try
        {
            foreach (var p in AloneFightManager.PartyPlayers)
            {
                if (p != player && !p.Creature.IsDead)
                {
                    __instance.PlayerVotedForMapCoord(p, source, destination);
                }
            }
        }
        finally
        {
            _mapVoteDepth--;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // 12. 换幕投票 —— 一票代表全队就绪
    //
    // SetLocalPlayerReady 只会把当前英雄的 VoteToMoveToNextActAction 入队。
    // 这里直接把其他英雄标记为就绪；当前英雄的投票动作随后执行时自然齐票，
    // MoveToNextAct 只触发一次。Boss 奖励界面的前缀补丁会自行逐槽位入队投票，
    // 此时通过 _drivingActVotes 抑制本跟随逻辑。
    // ════════════════════════════════════════════════════════════════

    private static bool _drivingActVotes;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ActChangeSynchronizer), "SetLocalPlayerReady")]
    static void PostfixSetLocalPlayerReady()
    {
        if (_drivingActVotes || !AloneFightManager.IsActive || AloneFightManager.PartyPlayers == null)
            return;

        var sync = RunManager.Instance.ActChangeSynchronizer;
        ulong? me = LocalContext.NetId;
        foreach (var p in AloneFightManager.PartyPlayers)
        {
            if (p.NetId != me)
                sync.OnPlayerReady(p);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // 13. 宝箱房 —— 每名英雄亲自投票选择遗物
    //
    // 假多人模式下 BeginRelicPicking 会替所有非本地玩家随机预投一票，导致
    // 第一名英雄一选就结束，其他人没有选择机会。这里重置全部投票：存活英雄
    // 通过切座亲自投票（其 _localPlayerId 已随 RotateToNext 就位）；没有可
    // 操控座位的阵亡英雄直接记为弃权票，保证投票集合可以收齐。
    // ════════════════════════════════════════════════════════════════

    private static readonly FieldInfo? TreasureVotesField =
        AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_votes");

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), "BeginRelicPicking")]
    static void PostfixBeginRelicPicking(TreasureRoomRelicSynchronizer __instance)
    {
        if (!AloneFightManager.IsActive || AloneFightManager.PartyPlayers == null) return;
        if (TreasureVotesField?.GetValue(__instance) is not IList votes) return;

        var players = AloneFightManager.PartyPlayers;
        var voteType = typeof(TreasureRoomRelicSynchronizer.PlayerVote);
        var indexField = voteType.GetField(nameof(TreasureRoomRelicSynchronizer.PlayerVote.index));
        var receivedField = voteType.GetField(nameof(TreasureRoomRelicSynchronizer.PlayerVote.voteReceived));

        for (int i = 0; i < votes.Count && i < players.Count; i++)
        {
            var vote = votes[i];
            bool dead = players[i].Creature.IsDead;
            indexField?.SetValue(vote, null);                 // 未选择任何遗物
            receivedField?.SetValue(vote, dead);             // 阵亡英雄按弃权计
        }

        int deadCount = players.Count(p => p.Creature.IsDead);
        AloneFightLog.Info($"[AloneFight] 宝箱遗物投票已重置（{players.Count - deadCount} 名英雄待选，{deadCount} 名阵亡弃权）。");
    }

    // ════════════════════════════════════════════════════════════════
    // 14. 宝箱金币 —— 开箱金币发放给全队
    //
    // DoLocalTreasureRoomRewards 只给本地英雄发金币；真多人通过网络消息把金币
    // 复制给队友，而空网络服务不会投递该消息。这里逐名英雄反射调用私有的
    // DoTreasureRoomRewards，把其他英雄的金币补发到位。
    // ════════════════════════════════════════════════════════════════

    private static readonly MethodInfo? DoTreasureRoomRewardsMethod =
        AccessTools.Method(typeof(OneOffSynchronizer), "DoTreasureRoomRewards");

    [HarmonyPostfix]
    [HarmonyPatch(typeof(OneOffSynchronizer), "DoLocalTreasureRoomRewards")]
    static void PostfixTreasureRoomRewards(OneOffSynchronizer __instance)
    {
        if (!AloneFightManager.IsActive || AloneFightManager.PartyPlayers == null
            || DoTreasureRoomRewardsMethod == null) return;

        ulong? me = LocalContext.NetId;
        foreach (var p in AloneFightManager.PartyPlayers)
        {
            if (p.NetId == me) continue;
            if (DoTreasureRoomRewardsMethod.Invoke(__instance, new object[] { p }) is Task task)
                TaskHelper.RunSafely(task);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // 15. 通关进度 —— 让多英雄跑团的通关聚合进度正常写入
    //
    // ProgressSaveManager.UpdateWithRunData 在玩家数 >1 时会用
    // PlatformUtil.GetLocalPlayerId（Steam 下为 17 位 SteamID64）匹配
    // SerializablePlayer.NetId；我们的英雄 NetId 是 1..4，匹配失败直接 return，
    // 整局的通关进度聚合被跳过。这里在调用期间临时把队长的序列化身份替换为
    // 平台 id，调用结束（含抛异常）立即还原：
    //
    //   1. SerializablePlayer.NetId；
    //   2. 每个地图点历史 PlayerMapPointHistoryEntry.PlayerId —— 方法内部会用
    //      NetId 调 MapPointHistoryEntry.GetEntry，只改玩家 NetId 而不改历史键
    //      会抛 “Player with ID ... not found in player stats”，实测会导致在
    //      商店界面放弃跑团时流程在 GuaranteeKillAllPlayers 中崩溃、结算界面
    //      不出现。
    //
    // 还原放在 Finalizer 而非 Postfix：原方法抛异常时 Postfix 不会执行。
    // 通关历史存档在该方法返回之后才落盘，因此落盘数据仍是原始的 1..4。
    // ════════════════════════════════════════════════════════════════

    private static ulong? _progressLeaderOriginalNetId;
    private static readonly List<(PlayerMapPointHistoryEntry entry, ulong originalId)>
        _progressRewrittenHistory = new();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ProgressSaveManager), "UpdateWithRunData")]
    static void PrefixUpdateWithRunData(SerializableRun serializableRun)
    {
        _progressLeaderOriginalNetId = null;
        _progressRewrittenHistory.Clear();
        if (!AloneFightManager.IsActive || serializableRun.Players.Count <= 1) return;

        ulong localId = PlatformUtil.GetLocalPlayerId(serializableRun.PlatformType);
        if (serializableRun.Players.Any(p => p.NetId == localId)) return;

        var leader = serializableRun.Players[0];
        _progressLeaderOriginalNetId = leader.NetId;
        leader.NetId = localId;

        // 同步改写历史中队长的玩家 id 键，保证 GetEntry(localId) 能找到条目
        foreach (var act in serializableRun.MapPointHistory)
        foreach (var point in act)
        {
            var stats = point.PlayerStats.FirstOrDefault(s => s.PlayerId == _progressLeaderOriginalNetId);
            if (stats == null) continue;
            _progressRewrittenHistory.Add((stats, stats.PlayerId));
            stats.PlayerId = localId;
        }
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(ProgressSaveManager), "UpdateWithRunData")]
    static Exception? FinalizerUpdateWithRunData(SerializableRun serializableRun, Exception? __exception)
    {
        if (_progressLeaderOriginalNetId is { } original)
        {
            if (serializableRun.Players.Count > 0)
                serializableRun.Players[0].NetId = original;
            foreach (var (entry, originalId) in _progressRewrittenHistory)
                entry.PlayerId = originalId;
            _progressRewrittenHistory.Clear();
            _progressLeaderOriginalNetId = null;
        }
        return __exception; // 原样抛出，不吞掉游戏自身的异常
    }
}
