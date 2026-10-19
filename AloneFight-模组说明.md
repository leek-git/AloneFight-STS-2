# AloneFight 模组说明

> 《杀戮尖塔 2》（Slay the Spire 2）本地单人多英雄模组。
> 一名玩家在本地操控 2\~4 名英雄共同跑团：
> 全程由一人操作整支小队，Tab 或点击头像即可在英雄之间切换。

- 当前版本：**0.2.0**
- 适用游戏版本：**0.107.1 及以上**（见 `AloneFight.json` 的 `min_game_version`）
- 运行环境：Godot 4.5.1 Mono + C# .NET 9
- Harmony ID：`sts2.alonefight`

***

## 目录

1. [安装与卸载](#一安装与卸载)
2. [游玩介绍](#二游玩介绍)
3. [二次开发与调用示例](#三二次开发与调用示例)
4. [日志与问题反馈](#四日志与问题反馈)
5. [已知现象（不是 Bug）](#五已知现象不是-bug)

***

## 一、安装与卸载

### 安装

只需把以下 **2 个文件**放到游戏 mods 目录的同名文件夹中：

```
C:\Program Files\Steam\steamapps\common\Slay the Spire 2\mods\AloneFight\
├── AloneFight.dll    ← 编译产物（全部代码）
└── AloneFight.json   ← 模组清单（id / 版本 / has_dll 等）
```

- 无需修改任何游戏配置，游戏启动时自动扫描 `mods\` 目录并按 json 清单加载。
- 无需附带 Harmony/Godot 依赖 DLL——游戏 `data_sts2_windows_x86_64\` 已自带。
- 本模组没有 Godot 资源包（json 中 `has_pack: false`），纯 C# 代码。

### 更新

1. **完全关闭游戏**（运行中 DLL 被锁，覆盖会失败）；
2. 覆盖上述 2 个文件；
3. 经 Steam 启动游戏。

### 卸载

删除整个 `mods\AloneFight\` 文件夹即可，不改动任何存档。

### 启动红线

**必须经 Steam 启动游戏**。直接运行游戏 exe 会因缺少 Steam AppID 而退出。

***

## 二、游玩介绍

### 1. 开团

1. 主菜单 → 单人模式 → 子菜单中选择 **「独自多人战斗」**；
2. 进入选人界面后组建 2\~4 人的小队：
   - **左键点击英雄 +1**，**右键点击 -1**；
   - 同一英雄可以重复选择（例如 2 名铁甲战士）；
   - 被选中的英雄右上角显示金色 **×N** 角标；
3. 确认开局后，所有人共用一次操作推动游戏（怪物数量/血量按 **1 人难度**锁定）。

### 2. 切换控制英雄（切座）

| 操作                  | 行为                |
| ------------------- | ----------------- |
| **Tab**             | 按队伍顺序循环切换到下一名存活英雄 |
| **鼠标左键点左上角头像**      | 直接切到该英雄           |
| 点当前英雄头像 / **右键点头像** | 打开原版英雄详情（不切座）     |

切换后界面会整体跟随：手牌、能量、金币、血量、牌库、药水栏、遗物栏、商店货架、
事件/篝火选项都会重绑到当前英雄。阵亡英雄会被自动跳过。

以下时机**禁止切座**：战斗锁定中、选择怪物目标中、奖励结算流程中。

### 3. 各场景行为

| 场景            | 行为                                                       |
| ------------- | -------------------------------------------------------- |
| **战斗**        | 所有英雄共享同一回合节奏；点一次「结束回合」即代表全队存活英雄同时就绪                      |
| **战后奖励**      | 单屏轮换制：每次只显示一名英雄的卡牌/金币/药水奖励，可自由切座挑选；全员都过一遍后点「继续」统一结算并推进房间 |
| **Boss/胜利奖励** | 最后一名英雄确认后，全队自动投换幕票进入下一层                                  |
| **篝火**        | 每名英雄独立选择休息/锻造等选项，选完自动轮换到下一人                              |
| **事件房**       | 每名英雄独立做事件选择（含捏奥等古代事件）；切座时自动收起地图、重建对话                     |
| **宝箱遗物**      | 每名英雄**亲自**猜拳选择遗物（游戏原本的随机预投票已清除）；阵亡英雄按弃权处理                |
| **宝箱金币**      | 开箱金币全员都有                                                 |
| **商店**        | 每名英雄有独立金币与独立滚动货架；切座即切换货架，已购/删卡位状态各自独立                    |
| **地图**        | 任一英雄选择路线节点，全队自动跟票                                        |
| **换幕**        | 任一英雄投就绪票，全队同时就绪进入下一层                                     |
| **放弃/阵亡**     | 正常进入死亡结算界面；角色统计、连胜、Epoch 等进度正常写入存档                       |

### 4. 通关进度说明

游戏原版的通关聚合逻辑按平台 ID（SteamID64）匹配玩家，而本模组英雄的 NetId
是 1\~4。模组在 `ProgressSaveManager.UpdateWithRunData` 调用期间**临时**把队长
身份伪装为本地平台 ID，调用结束（即使抛异常）立即还原，因此：

- 连胜、角色统计、徽章、飞升进度均可正常解锁与写入；
- 历史存档中落盘的仍是原始 1\~4 NetId，不污染存档结构。

***

## 三、二次开发与调用示例

### 1. 工程结构（5 个文件）

| 文件                                      | 职责                                                                      |
| --------------------------------------- | ----------------------------------------------------------------------- |
| `Entry.cs`                              | 模组入口：`[ModInitializer]`、`Harmony.PatchAll`、`ScriptManagerBridge`、注册异常钩子 |
| `AloneFightManager.cs`                  | 核心状态与各房间界面重绑（开团、切座、身份同步、HUD/商店重绑、奖励跟随）                                  |
| `AloneFightPatches.cs`                  | 全部 Harmony 拦截点（文件顶部有完整中文目录注释，共 15 个模块）                                  |
| `AloneFightLog.cs`                      | 按局文件日志器，镜像游戏 Log，挂三道全局异常钩子                                              |
| `AloneFight.csproj` / `AloneFight.json` | 工程文件与模组清单；PostBuild 自动拷贝部署                                              |

### 2. 构建与部署

```powershell
# 工作目录：工程根目录；构建前必须先关闭游戏
dotnet build AloneFight.csproj -c Release
```

成功标志：。
PostBuild 目标会自动把 `AloneFight.dll` 与 `AloneFight.json`
拷贝到游戏 `mods\AloneFight\` 目录，也可以手动添加文件至目录中。

游戏运行中 DLL 被锁时会报 MSB3027/MSB3021——关游戏后重新构建即可。

### 3. 入口结构

```csharp
[ModInitializer(nameof(Init))]
public static class Entry
{
    public static void Init()
    {
        var harmony = new Harmony("sts2.alonefight");
        harmony.PatchAll(typeof(Entry).Assembly);

        // 让游戏发现本程序集中继承 Godot 类型的脚本类
        ScriptManagerBridge.LookupScriptsInAssembly(typeof(Entry).Assembly);

        // 注册全局异常捕获 → 写入按局日志
        AloneFightLog.RegisterExceptionHooks();
    }
}
```

### 4. 日志 API

```csharp
AloneFightLog.Info("普通信息");     // 同时写入模组日志和 godot.log
AloneFightLog.Debug("调试细节");    // 仅写入模组日志文件
AloneFightLog.Error("出错了");
AloneFightLog.Error("带异常", ex);  // 附完整堆栈

// 一局跑团开始/结束时由 Manager 自动调用，一般无需手动调
AloneFightLog.BeginRun(runState);
AloneFightLog.EndRun();
```

### 5. Harmony 补丁常用写法（均取自本模组实际代码）

#### 示例 A：Prefix 修改原方法参数（怪物血量锁 1 人难度）

```csharp
[HarmonyPrefix]
[HarmonyPatch(typeof(Creature), nameof(Creature.ScaleMonsterHpForMultiplayer))]
static void PrefixScaleMonsterHp(ref int playerCount)
{
    if (AloneFightManager.IsActive)
        playerCount = 1;
}
```

#### 示例 B：反射写入私有字段（一次结束回合 = 全队就绪）

```csharp
// initonly/普通私有字段用 FieldRef 缓存，避免每次反射查找
private static readonly AccessTools.FieldRef<CombatManager, HashSet<Player>> ReadyToEndTurnRef =
    AccessTools.FieldRefAccess<CombatManager, HashSet<Player>>("_playersReadyToEndTurn");

[HarmonyPrefix]
[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetReadyToEndTurn))]
static void PrefixSetReadyToEndTurn(CombatManager __instance, Player player)
{
    if (!AloneFightManager.IsActive || AloneFightManager.PartyPlayers == null) return;

    var readyList = ReadyToEndTurnRef(__instance);
    foreach (var p in AloneFightManager.PartyPlayers)
    {
        if (p != player && !p.Creature.IsDead && !readyList.Contains(p))
            readyList.Add(p);
    }
}
```

#### 示例 C：Postfix 反射操作嵌套私有集合（清除宝箱随机预投票）

```csharp
private static readonly FieldInfo? TreasureVotesField =
    AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_votes");

[HarmonyPostfix]
[HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), "BeginRelicPicking")]
static void PostfixBeginRelicPicking(TreasureRoomRelicSynchronizer __instance)
{
    if (!AloneFightManager.IsActive || AloneFightManager.PartyPlayers == null) return;
    if (TreasureVotesField?.GetValue(__instance) is not IList votes) return;

    var voteType = typeof(TreasureRoomRelicSynchronizer.PlayerVote);
    var indexField = voteType.GetField(nameof(TreasureRoomRelicSynchronizer.PlayerVote.index));
    var receivedField = voteType.GetField(nameof(TreasureRoomRelicSynchronizer.PlayerVote.voteReceived));

    var players = AloneFightManager.PartyPlayers;
    for (int i = 0; i < votes.Count && i < players.Count; i++)
    {
        indexField?.SetValue(votes[i], null);                  // 未选择任何遗物
        receivedField?.SetValue(votes[i], players[i].Creature.IsDead); // 阵亡者按弃权
    }
}
```

#### 示例 D：Prefix 临时改身份 + Finalizer 保证还原（通关进度写入）

```csharp
[HarmonyPrefix]
[HarmonyPatch(typeof(ProgressSaveManager), "UpdateWithRunData")]
static void PrefixUpdateWithRunData(SerializableRun serializableRun)
{
    // ...临时改写队长 NetId 与地图历史键，缓存原值...
}

// 还原必须放在 Finalizer：原方法抛异常时 Postfix 不会执行
[HarmonyFinalizer]
[HarmonyPatch(typeof(ProgressSaveManager), "UpdateWithRunData")]
static Exception? FinalizerUpdateWithRunData(Exception? __exception)
{
    // ...还原所有临时改写...
    return __exception; // 原样抛出，不吞掉游戏自身异常
}
```

### 6. Manager 常用公共方法

| 方法                                                    | 用途                                                    |
| ----------------------------------------------------- | ----------------------------------------------------- |
| `TryInitializeFromRun(runState)`                      | 跑团启动时建立队伍状态                                           |
| `CleanUp()`                                           | 跑团结束清理全部状态                                            |
| `SwitchTo(player)`                                    | 切座到指定英雄（身份同步 + 界面重绑）                                  |
| `RotateToNext()`                                      | 轮换到下一名存活英雄（阵亡自动跳过）                                    |
| `SeatAllIdentities(player)`                           | 同步改写 `LocalContext.NetId` + 六个房间同步器的 `_localPlayerId` |
| `RebindCombatUi()` / `RebindTopBar(p)`                | 重绑战斗手牌/能量与顶栏（金币/血/牌库/药水/遗物）                           |
| `RefreshMerchantUi()`                                 | 切座后换绑商店货架 Inventory 并重建槽位                             |
| `RefreshEventUi()` / `RefreshRestSiteUi()`            | 重绑事件/篝火界面                                             |
| `FollowRewardHero(player)` / `ResetControlToLeader()` | 奖励焦点跟随 / 奖励结束回到队长                                     |
| `IsActive` / `PartyPlayers` / `CurrentControlPlayer`  | 模组是否激活、队伍列表、当前操控英雄                                    |

### 7. 开发注意事项

- Harmony Patch 类必须是**无参构造 + 静态补丁方法**。
- 游戏大版本更新后，反射用的私有字段/方法名（如 `_localPlayerId`、`_votes`、
  `_playersReadyToEndTurn`）可能失效——升级游戏后优先复查这些字符串。
- Godot SDK 全局 using 下 `Environment`/`FileAccess`/`FileMode` 与 System 类型冲突，
  用全限定名：`System.Environment`、`System.IO.FileAccess`。
- 一次鼠标点击会同时触发获焦与 Released，计数类逻辑只能走单一 MousePressed 通道。

***

## 四、日志与问题反馈

### 1. 模组按局日志（反馈问题时主要提供这个）

```
%APPDATA%\SlayTheSpire2\AloneFight\logs\run_yyyyMMdd_HHmmss.log
```

资源管理器地址栏直接粘贴 `%APPDATA%\SlayTheSpire2\AloneFight\logs` 即可打开。

特性：

- **每开始一局生成一个日志文件**，跑团结束自动写入收尾行；
- 自动保留**最近 2 局**，更早的自动删除；
- 0 字节文件（开了立刻关游戏）自动清理，不占保留名额；
- INFO 及以上同时镜像到游戏 godot.log，DEBUG 仅写模组文件。

日志内容：

- 文件头：模组版本、Godot/系统版本、飞升等级、英雄阵容（NetId/角色）；
- 切座记录、事件/篝火/商店重绑记录；
- 战后奖励轮换（标注「推进房间」或「换幕票」）；
- 宝箱遗物投票重置；
- `ERROR` 全堆栈；
- `FIRST-CHANCE`：仅模组相关堆栈，按「异常类型+消息」每局去重；
- `UNOBSERVED-TASK`：async Task 未观察异常；
- `FATAL`：致命未处理异常。

### 2. 游戏日志（辅助）

```
%APPDATA%\SlayTheSpire2\logs\godot.log
```

游戏自身与所有模组的混合日志。模组日志路径本身也会在开局时打印到这里。

### 3. 反馈问题时请提供

1. `%APPDATA%\SlayTheSpire2\AloneFight\logs\` 内的 **1\~2 个** **`run_*.log`**；
2. 问题发生的**大致时间点**（例如「13:09 在宝箱房切座后」）；
3. 问题现象描述（预期行为 vs 实际行为）；
4. 如模组日志中线索不足，再附上同时段的 `godot.log`。

***

## 五、已知现象（不是 Bug）

以下为游戏原版噪音或无功能影响的现象，无需反馈：

- `Tried to add hand for player 2 twice!`：首次进宝箱房时游戏对手输入状态
  惰性初始化导致的自检报错，每名英雄的手仍恰好一只，投票/猜拳正常，
  再次进入宝箱房不再出现。
- `Unable to extract RunState from creatures list`：原版纯怪物列表兜底警告。
- `Unspecified Rarity: Ancient`：原版数据警告。
- 退出游戏时的 RID / shader leaked：Godot 引擎退出清理噪音。
- 开局动画过渡的几秒内，选人 ×N 角标可能短暂可见后消失，不影响计数。
- 注意事件场或结算奖励需要全员选完再进行下一阶段，不做未选提示打断流程。

