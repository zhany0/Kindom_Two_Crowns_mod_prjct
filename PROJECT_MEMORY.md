# BetterKingdom 项目记忆（AI 交接文档）

## 1. 项目背景

- 目标：为《王国：两位君主》(Kingdom Two Crowns) 编写 BepInEx 6 IL2CPP + Harmony 功能增强模组。
- 游戏安装目录：`D:\steam\steamapps\common\Kingdom Two Crowns`（脚本里常写作 `D:\Steam\...`，大小写不敏感）。
- 工程目录：`d:\KingdomMod\code2git`，输出 `BetterKingdom.dll`，构建脚本会自动部署到
  `D:\steam\steamapps\common\Kingdom Two Crowns\BepInEx\plugins\BetterKingdom`。
- 构建链：.NET SDK 6（C# 10 上限），**不要使用主构造函数、集合表达式等 C# 12 语法**。
- 游戏 Unity 版本 `6000.0.66f2`，Cpp2IL 自动识别为 X86InstructionSet。
- 存档（gzip JSON）：`C:\Users\<用户名>\AppData\LocalLow\noio\KingdomTwoCrowns\Release\global-v35`。
- 运行日志：`C:\Users\<用户名>\AppData\LocalLow\noio\KingdomTwoCrowns\Player.log`。

## 2. 文件职责边界（硬约束）

| 文件                       | 职责                                                                             |
| ------------------------ | ------------------------------------------------------------------------------ |
| `Plugin.cs`              | 注册常驻组件、集中应用 Harmony 补丁、窗口输入拦截、光标补写、IMGUI 中文字体修复                                |
| `MapOverlayBehaviour.cs` | 所有显示逻辑：地图覆盖层、标记、时间/季节推算、血条/作物条、悬停动画                                            |
| `FeatureBehaviour.cs`    | 所有业务功能与对应补丁（钱袋/国库联动、守墙修正、摇铃上船）                                                         |
| `HotkeyBindings.cs`      | 快捷键解析、组合键捕获、配置读写、Rewired 运行时绑定                                                 |
| `ModWindowBehaviour.cs`  | 设置窗口 UI 与开关状态，**不含具体业务逻辑**；开关变化后调用 `MapOverlayBehaviour.RequestHeavyRefresh()` |

新增功能流程：设置窗口加开关（静态字段 + `ConfigEntry` + 行号/内容高度）→ 显示或功能文件读该静态字段。

## 3. 已确认的游戏接口与字段

> 以下字段/属性均已在 Il2CppInterop 代理程序集 `BepInEx\interop\Assembly-CSharp.dll` 中确认存在（见第 5 节查法）。

### 3.1 管理器与核心

| 类型         | 成员                                                                                 | 说明                                                       |
| ---------- | ---------------------------------------------------------------------------------- | -------------------------------------------------------- |
| `Managers` | `SingletonMonoBehaviour<Managers>.Inst`、`.director`、`.world`、`.kingdom`、`.enemies` | 全局入口                                                     |
| `Director` | `currentTime` (float, 0\~24 循环)                                                    | 一天内时刻，`secondsPerInGameHour=10`                          |
| <br />     | `dayStart`≈5.0、`dayEnd`≈21.5                                                       | **白天区间**（`IsDaytime` = `dayStart < t < dayEnd`），清晨换日以此为准 |
| <br />     | `TotalDaysInReign`                                                                 | 本岛统治累计天数；`ResetDayCount` 置 0/1                           |
| <br />     | `CurrentDaysSinceFirstLandingThisReign` = TotalDaysInReign - 登陆日                   | 换岛不重置的累计天数                                               |
| <br />     | `CurrentSeason`、`CurrentSeasonDay`、`startGameInAutumn`                             | 季节；`CurrentSeasonDay` 实际等于 TotalDaysInReign（不是季节内天数）     |
| <br />     | `CurrentTimesOfDay`                                                                | 当前时段配置（见 3.3）                                            |
| <br />     | `OnDayFlip`、`OnDayPhaseChange`、`SendDayPhaseChangeEvent`                           | 换日/时段切换事件                                                |
| `World`    | `worldBounds`、`worldBoundsStrict`（`Sided<float>`）                                  | 地图左右边界，仅在 `OnLevelLoaded` 由 `Ground` 碰撞体计算一次，之后不变        |
| <br />     | `teleporters`（`List<PayableTeleporter>`）、`teleporterRifts`                         | 自建传送门 / 传送裂缝                                             |
| <br />     | `IsWithinBounds`、`IsWithinStrictBounds`                                            | 见第 4 节                                                   |
| `Kingdom`  | `playerOne`、`GetPlayer(i)`、`castle`、`banker`、`AllPortals`、`Walls`、`GetWalls...`    | 城堡/玩家/怪物传送门/城墙                                           |
| `Sided<T>` | `.left`、`.right`                                                                   | 左右值对                                                     |

### 3.2 传送门相关

| 类型                          | 成员                                           | 说明                                                    |
| --------------------------- | -------------------------------------------- | ----------------------------------------------------- |
| `PayableTeleporter`         | `linkedTo`                                   | **为空 = 普通传送门**（可任意地点传送）；**非空 = 双向传送门**（与另一端互指，只能定点穿梭） |
| <br />                      | `createdByTeleportation`                     | true = 由传送后生成的裂缝建造（即“新传送门”）；false = 建在怪物传送门残骸上        |
| <br />                      | `Pay()`、`Link()`、`OnUpgradeFrom()`、`Place()` | 花费/建链/升级等行为                                           |
| `TeleporterRift`            | `timeToLive`、`Place()`                       | 传送后目的地生成的“临时摧毁传送门”，可花金币建造                             |
| `Portal` / `BombablePortal` | 怪物传送门 / 可炸洞窟                                 | 地图上原有红色“传送门”标记与“洞窟”标记来源                               |

### 3.3 时段配置 `TimesOfDay`

字段声明顺序：`dawnStart, dawnEnd, dayStart, dayEnd, eveningStart, eveningEnd, nightStart, nightEnd, orbitScale, orbitCenter`。
默认资源值（`resources.assets` 的 `DefaultTimesOfDay`）：`4.5, 7.5, 8.5, 10.5, 16.0, 19.0, 21.0, 22.0`。
`GetDayPhase(time)` 判定：`[dawnStart, dawnEnd)` → Dawn。

### 3.4 敌人

`EnemyManager.AllEnemies`（`ICollection<Enemy>`）→ 用 `CopyTo(Il2CppReferenceArray<Enemy>)` 后按下标访问。
`Enemy.waveID`（同一波怪物共享）、`Enemy.IsDespawning`、`Enemy.damageable`、`Damageable.hitPoints/initialHitPoints/HealthPercentage/isDead`。

### 3.5 存档结构（`CampaignSaveData`）

- `CampaignSaveData.current.TotalGameDays`：战役累计天数，在换日时由 `Director` 写入（值 = 当天 - 1）。
- 每岛 `DirectorData`：`daysInReign`、`islandDays`、`landedOnIslandDay`、`currentTime`、`startGameInAutumn`。
- `_levelEdges` 为固定地图边界（island0 -284/166、island1 -204/291、island2 -250/283、island3 -350/210），
  运行时对应 `CampaignSaveData.current.CurrentIsland.LevelEdges`（`IntRange`，字段 `min`/`max`）。
- 存档 `_islands[i].objects` 保存该岛全部持久对象（`name` / `localPosition` / `localScale` / `parentObject`），
  可用来核对任意标记的真实世界坐标；`Ground` 对象的 `localPosition.x` 正好等于 `_levelEdges` 的中点。
- `PayableTeleporter` 存档中成对出现，`linkedTo` 互指；`TeleporterRift` 的 `timeToLive` 常为负值（不过期）。

## 4. 已验证的游戏机制结论

1. **世界边界是静态的**：`World.OnLevelLoaded` 用 `Ground.transform.position.x ± Ground.size.x * 0.5f` 写入 `worldBoundsStrict`，再各内缩 `WorldBoundsMargin` 写入 `worldBounds`。正常游戏不会变化。
   - 运行时更可靠的取法是 `World.GroundCollider`（**静态属性**，返回 `BoxCollider2D`），用 `ground.bounds.min.x / max.x` 得到关卡左右边界。
   - `world.worldBounds` 在部分场景可能读不到（开关地图标记时曾出现整图比例变化，即退化成了按标记估算），因此只作为第二来源。
   - **地图范围绝不能包含任何可开关的地图标记**（传送门/营地/宝箱等），否则开关选项会改变整图比例。
   - **洞窟侧地面会延伸进悬崖内部**（洞窟 `BombablePortal` 距离该侧原始边界 64~100 单位，另一侧则几乎贴边，如岛3 右边界 210 而灯塔在 209.25），
     所以显示范围要在该侧按洞窟位置裁掉，否则那一侧留空明显。裁剪只做一次并缓存（洞窟被炸后不回弹）。
2. **换日在午夜 0 点**：`Director.Update` 中当 `currentTime >= 24` 时 `TotalDaysInReign++`、`currentTime -= 24`，同时写 `CurrentSeason` 与 `CampaignSaveData.TotalGameDays`。
   → 需求“季节日改为早上更新”只能在显示层做偏移：`currentTime < 白天起点(dayStart≈5)` 时显示前一天。
3. **季节是数据驱动的，默认每季 16 天**：资源 `SeasonData_Default` 每季 `TotalNumDays = 14 regular + 1 boss(血月) + 1 recovery = 16`，一年 4 季共 64 天。
   - 季节日历的日序号是 **0 基**：游戏用 `Director.TotalDaysInReign` 调 `LandCycleData.GetSeasonForDay(day, ref startDay)`，季节起始日为 `0,16,32,...`，
     季内天数 = `day - startDay + 1`（1..16）。模组曾用 `(day-1)%16+1`，整体少算一天：血月日显示成第 14 天、新季首日显示成第 16 天。
   - 血月由 `LandData.GetDayTypeForDay(day)` 按 `cycle.numRegularDays` 判定（cycle 内第 15 天是 boss 日、第 16 天是 recovery 日），与季节轮换公式无关。
   - `Director.CurrentSeasonDay` 返回的是 `TotalDaysInReign`，**不是**季内天数；取季内天数要用 `CurrentSeasonStartDay` 或 `GetSeasonForDay`。
   - “季节日改为早上更新”仍只在显示层做偏移：`currentTime < dayStart≈5` 时把日序号减 1，再重新取季节（保证季节名与天数一致）。
4. **传送门**：`PayableTeleporter.linkedTo` 是普通/双向的唯一可靠判据；`World.teleporters` 才是枚举入口，不要全场景 `FindObjectsByType`。
5. **守墙排队机制**：`Kingdom.DistributeFreeArchers` 按顺序给同侧守墙弓箭手分配递增的 `Archer._guardDepth`（排队序号）；
   `Archer.GetWallTargetPos` 站位 = 墙位 ∓（最小间距 `_minDistanceFromWall`1.75 + 深度×`_unitSpacingAtWall`0.11 + `_guardRandomOffset`0.25），
   `Side.Left=-1 / Side.Right=1`（正负号即方向）。深度越大离墙越远，超出 `shootRange`（默认 6.5）后无法攻击墙外怪物。
   功能开关“禁用排队机制”的做法：周期性把已分配守墙侧（`_guardSide` 为 ±1）弓箭手的 `_guardDepth` 归零，统一站到墙后最近射程内；
   钳制时按 `GetInstanceID()` 记录原始深度，关闭开关后立刻写回还原（`RestoreWallQueueing`），弓箭手/骑士会自动走回排队站位。
   骑士是另一套机制：`Kingdom.RankKnights` 给同侧骑士依次写 `Knight.rank = 1..N`，`Knight.GetTargetPos` 站位 = 墙位 ∓ `rank`×`_distanceFromWall`（rank 从 1 起算，无单独最小间距），同一开关下把 `rank > 1` 的骑士归 1；骑士跟随士兵随骑士整体前移，无需单独处理。
   `Knight.ShouldGoToWall` 会比较当前位置与 `GetTargetPos()`，因此写回 `rank` 后骑士会自行重新走位，无需调用 `RankKnights()`。
   `Pikeman` 有独立的三格墙位（`Pikeman.Assign(Wall, readiness)`，站位含 `3-readiness`），不是长队；`Berserker` 只有固定 `distanceFromWall`，无排队序号。
6. **工人墙外避险**：不区分昼夜，某侧存在"进攻状态"怪物且工人自身位置在该侧安全距离之外时，工人跑回城墙驻守，直到该侧进攻怪物数量为 0。
   进攻判据：`!Enemy.IsDespawning && !Enemy.shouldRetreat && !damageable.isDead && gameObject.activeSelf && Enemy.loot == null`（`loot` 非空＝已拖着金币/战利品撤回传送门）；`Enemy` 无侧别字段，用 x 与 `kingdom.castle` 的 x 比较分侧，检测结果缓存 0.2s（`RefreshSideAssault`）。
   安全距离：墙内始终安全；墙外要求距该侧城墙外沿 ≤ `SafeDistance`（20）；该侧还没有城墙时从主城（`kingdom.castle`）位置起算（`TryGetOuterWallX` 里已处理）。
   实现只用 `Worker.ShouldShelter` 的 Postfix 强制返回真：原生实现首行是 `if (kingdom.isSafe) return false`，白天会被游戏的安全态按掉，所以才需要补丁。判据 `ShouldFleeToWall` 只看工人自身位置。
   **统一判据（按工作点，不按工人所在侧）**：`IsWorkSpotSafe(kingdom, x)` 先用 x 与 `kingdom.castle` 的 x 分侧（与怪物分侧口径一致，取不到城堡时退回露营火堆位置），再走 `IsPositionSafe`。该侧有怪且工作点在安全距离外即为不安全，**任何人都不许接**。
   ①`Worker.AssignJob(Workable)` Prefix 一律拒绝不安全工作点（`WorkerAssignSafeJobPatch`）——左侧出怪时，右侧工人也接不到左侧的危险活；②`SuspendUnsafeQueuedWork` 遍历 `kingdom.Workers` 撤下所有**已分配但还没开始**的不安全 `_queuedWork`（不再按侧分流，与 Prefix 同一判据，工作往往在怪物出现前就已分配好，Prefix 管不到）；③**不撤 `_currentWork`**：正在干的活按原生优先级（Work > Shelter）干完再回城（`CancelWork`/`ResetWorkState` 打断工作会引发恢复难题）。
   **绕过 AssignJob 的付费接活路径（补丁 ④⑤，2026-11-02 补）**：`Worker` 实现 `IUnitControllable.UpdatePayState`（interop 名 `IUnitControllable_UpdatePayState`，IL 中 `[this+200]=workable; [this+386]=1`）——玩家长按支付时把 `_maxWorkDistance` 内**任意** `Kingdom.GetTotalWork()` 工作直接写进 `_queuedWork` 并置 `__forceWork`（属性名 `_forceWork`），完全跳过 `AssignJob`；`Worker.Behaviour` 协程随后按 `_queuedWork` 的坐标 `Mover.SetGoal` 走过去，再在 IL 745 调 `SetCurrentWork(_queuedWork)` 转成当前工作（这是所有排队工作转当前工作的唯一漏斗）。
   ④`WorkerPayWorkSafePatch`：`IUnitControllable_UpdatePayState` Postfix 调 `FeatureBehaviour.CancelUnsafeQueuedWork`（撤销不安全 `_queuedWork`、`CancelWork` 释放工作位、清 `_forceWork`），分配动作结束的同一帧就撤掉；⑤`WorkerSetCurrentWorkSafePatch`：`Worker.SetCurrentWork` Prefix 用 `CanStartWork` 拒绝在危险工作点启动（null 放行），是覆盖所有路径的最后防线。`CanStartWork` / `CancelUnsafeQueuedWork` 与 `AssignJob` 拦截共用 `IsWorkSpotSafe` 判据。
   **让工人回来干活**：`ResumeSuspendedWork` 在工作点重新变安全（该侧进攻怪归零）后立刻 `AssignJob` 还回被撤下的工作；`JobAvailable()` 为假时保留记录下次重试，工人已有活则丢弃记录；`SuspendedJob` 不记录侧别，恢复时全部按工作点重算。
   光还回记录还不够：游戏原生只在 `Director.IsDaytime` 时由 `Worker+<Behaviour>d__111::MoveNext` 调 `Kingdom.ReassignWork`（IL 356-371：`Kingdom.WorkAvailable() && Director.get_IsDaytime()`），**夜间根本不分配新工作**。所以在 `RefreshSideAssault` 里检测 `_attackOnLeft/_attackOnRight` 由真变假的那一帧，主动调用一次 `kingdom.ReassignWork()`，让夜间怪物被清掉后工人立即出门；`Kingdom.ReassignWork` 本身不检查昼夜，`Worker.ResetWorkState` 夜间也会调用。
   判据：`IsAssaultingEnemy` = `!IsDespawning && !shouldRetreat && !damageable.isDead && gameObject.activeSelf && loot == null`；`loot` 非空＝已拖着金币/战利品回传送门，不算进攻怪物（否则逃跑的怪会让工人一直不敢出门）。
   该补丁不碰 `isSafe` 与 `Director` 时钟，避免复发“复仇血月结束后时间卡死”的问题（该问题根因是白天提前 `BecomeSafe` 跳过了原生收尾分支）。
7. **钱袋与国库共通**：真实国库账本是 `Banker._stashedCoins`（存档走 `BankerData.stashedCoins`）；
   `Castle._currentCoinCount` 只是城堡金币堆的显示镜像（`SendStashCount`/`RecvStashCount` RPC 同步，`Castle.stash` + `stashHeight` 按数量画高度），**不参与存档**。
   因此国库未解锁（场景里没有 `Banker`）时整个联动必须关闭，否则溢出金币写进显示字段会丢失、取款还会凭空造币。判定入口：`_cachedBanker == null` 直接 return。
8. **付费攻门小队优先级**：入口是 `PayableBorder.Pay()`（玩家付费后由 `Payable.PerformPay()` 虚调用），原版在 `Kingdom.Knights` 哈希集上遍历，只按 `Knight.numArchers` 取最大，人数相同则"后遍历者"胜出（顺序随机），不看 `_wasPromoted`。
   模组做法：`PayableBorder.Pay` Prefix 里按 `CanCharge()` + `|kx-border.x| <= knightRange` 筛出候选，排序键为 人数 > `_wasPromoted` > 距离更近，记下实例 ID；同期间 `Knight.CanCharge` Postfix 只放行该 ID，使原版循环必然选中它；Postfix 清理上下文。计算候选时用 `_chargePicking` 标志绕过 `CanCharge` 自身过滤，避免递归。
   小队人数 = `Knight._archers` 的 `Count`（`numArchers`），上限 `_maxArchers`（prefab 值 4）；`_numAdditionalChargeFollowers` 是冲锋时招募狂战士的参数，不是人数依据。
9. **周期刷新不能同帧叠加**：`FeatureBehaviour` 与 `MapOverlayBehaviour` 的各周期任务（进攻检测、缓存、墙体修正、洞窟/油桶/农田全场景扫描、中/重刷新）
    共用同一个“本帧唯一重型槽位” `FeatureBehaviour.TryReserveHeavySlot()`（按 `Time.frameCount` 记账）：抢不到槽位的任务**不重置计时器**，顺延到后续帧。
    各任务周期（0.2s~10s）都远大于一帧，因此不会饿死；安全检测放在 Update 最前，优先拿槽位。
    `Awake` 里按不同相位初始化计时器（0/0.1/0.5/0.6/0.7s），避免首帧全部到期后连续多帧排队。每帧仍照常执行的任务只有 `UpdatePurseTreasuryLink`、`SyncWallQueueSwitch`、`EnsureTimeNotStuck`（都很轻且需要帧级/秒级精度）。
10. **玩家最高移速 5.8（功能设置页开关「玩家移动速度提升」）**：原版奔跑约 4.29（实测 4.28~4.30 浮动）。开关 `ModWindowBehaviour.BoostPlayerSpeed`（ConfigEntry `_cfgPlayerSpeed`，默认关）由 `FeatureBehaviour.SyncPlayerSpeedBoost` 在 `RefreshCaches`（4Hz）里执行：
    ①`player.steed.runSpeed` 不足 `PlayerRunSpeedCap`(5.8) 时写 5.8，并用 `_boostedSteeds` 记录每个坐骑改动前的原值（同一坐骑只记一次）；
    ②`player.mover._moveSpeed` 落在 [4,5.8) 时抬到 5.8（让当前正在跑的这一段立刻生效）。
    关闭开关时按记录把 `runSpeed` 还原、并把精确等于 5.8 的 `_moveSpeed` 退回原值，无需重启游戏。
    只用 [4,5.8) 区间，行走速度（远低于 4）与冲刺/特殊加速（高于 5.8）都不动。如需核对实际速度，临时用 `Player.currentSpeed` 观察，用完删除。
11. **摇铃立即上船必须延后一帧**（`EmbarkeeInstantBoardPatch`）：不能在 `Embarkee.SetEmbarkableTarget` 的 Postfix 里直接 `Embark()`。
    读档/回岛时 `Boat.ApplyData` / `LoadLegacyEmbarkees` 的恢复顺序是 `StopInert → SetEmbarkableTarget → 游戏自己 Embark()`，Postfix 抢先调用会让同一单位 `Embark` 两次；`Embark()` 先置 `IsEmbarked=true` 再申请座位，重复调用会重复占座，下船时同一单位被处理两次，导致整船 4 弓箭手 + 3 工人卡在码头原地动画，只有重开游戏才恢复。
    正确做法：Postfix 只把单位加入 `FeatureBehaviour._pendingInstantBoards`，Update 下一帧检查 `!IsEmbarked` 且目标仍是船后才 `Embark()`；加载期间（`IsOverlayVisible()==false`）直接清空队列。

## 5. 未知字段/方法怎么查（逆向工具链）

> 原则：先查代理程序集拿字段名，再查 ISIL 看逻辑，最后用存档 JSON 验证。三项结果互相印证后才写进代码。

### 5.1 查字段名/属性名（Mono.Cecil，最快）

代理程序集：`D:\steam\steamapps\common\Kingdom Two Crowns\BepInEx\interop\Assembly-CSharp.dll`
（`BepInEx\core` 下是 BepInEx 自身与 Il2CppInterop 运行时，**没有游戏类型定义**，不要在那里找字段。）

临时探针（用完删除）：

```powershell
mkdir $env:TEMP\cecildump; cd $env:TEMP\cecildump
dotnet new console -o . -f net6.0   # 先备份自写的 Program.cs，或用 Write 手写 csproj 避免被覆盖
dotnet add package Mono.Cecil --version 0.11.5
```

```csharp
var mod = ModuleDefinition.ReadModule(@"...\BepInEx\interop\Assembly-CSharp.dll");
var t = mod.Types.First(x => x.Name == "World");
foreach (var f in t.Fields) Console.WriteLine(f.Name + " : " + f.FieldType.Name);
foreach (var p in t.Properties) Console.WriteLine(p.Name + " : " + p.PropertyType.Name);
// 枚举值：t.Fields.Where(x => x.HasConstant) → x.Name / x.Constant
```

注意：`[System.Reflection.Assembly]::ReflectionOnlyLoadFrom` 会因依赖解析失败而无输出，不要用。

### 5.2 查方法体逻辑（Cpp2IL）

游戏 IL2CPP 方法体只能用 Cpp2IL 反汇编（Cecil 里方法体是空桩）。临时探针要点：

```csharp
Cpp2IlApi.Init(pluginsDir);
var uv = Cpp2IlApi.DetermineUnityVersion(exePath, dataDir);
Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(gameDir, "GameAssembly.dll"),
    Path.Combine(dataDir, "il2cpp_data", "Metadata", "global-metadata.dat"), uv, false);
var ctx = Cpp2IlApi.CurrentAppContext;
ctx.InstructionSet ??= new X86InstructionSet();
// ctx.AllTypes.First(x => x.FullName == "Director").Methods → m.Analyze() → m.ConvertedIsil
```

csproj 需引用：`Cpp2IL.Core`、`LibCpp2IL`、`AssetRipper.Primitives`、`AssetRipper.CIL`、`AsmResolver*`、`Iced`、
`Gee.External.Capstone`、`Disarm`、`SemanticVersioning`、`Microsoft.Extensions.Logging*`、`StableNameDotNet`、`WasmDisassembler`。

注意：`ConvertedIsil` 对 `setcc` 等指令会输出 `NotImplemented`，此时分支语义不可信，**以字段/结构体偏移和实际资源数据为准**。
分支目标 `{n}` 是方法内字节偏移，不是指令序号。把输出重定向到文件再分段读，避免管道截断（`Select-Object -First` 会导致退出码 -1）。

### 5.3 查资源默认值与序列化数据（UnityPy 1.25.4 / Python 3.13）

- 资源：`KingdomTwoCrowns_Data\level0`\~`level5`、`resources.assets`、`sharedassets*.assets`、`globalgamemanagers.assets`。
- MonoBehaviour 无 TypeTree（IL2CPP 剥离）：`obj.serialized_type.node` 为 None，不能 `read_name()`。
  需手动解析原始字节：`PPtr(12B) + enabled(1B) + 对齐 + m_Script PPtr(12B) + m_Name(string)`，
  并据 `m_Script.m_FileID` 去 `SerializedFile.externals` 找 `MonoScript`，读 `m_ClassName` 映射类型名。
- 遍历目录用 `os.scandir`；中文路径用 `np.fromfile` + `cv2.imdecode` / `cv2.imencode` + 文件写入。

### 5.4 查存档验证

`global-v35` 是 gzip JSON，可解压到临时目录后直接读；用于确认对象数量、位置、`linkedTo` 关系、天数等。

### 5.5 其他线索

- `Player.log`：查异常、加载流程、BepInEx 日志；不含自动存档记录。
- 事件调度：`SpawnWave / Dawn / Day / Evening / Night` 的具体时刻可从存档调度数据读出（例：`Dawn 4.5`、`Day 8.5`）。

## 6. 地图标记配色约定（不要再用相近颜色）

| 标记          | 颜色 (RGB)               |
| ----------- | ---------------------- |
| 地图标线        | 黄 `(1,0.9,0.15)`       |
| 玩家          | 绿 `(0.25,1,0.35)`      |
| 主城          | 白 `(1,1,1)`            |
| 怪物传送门 / 洞窟  | 红 `(1,0.25,0.2)`       |
| 城墙          | 灰 `(0.62,0.62,0.66)`   |
| 箭塔          | 木色 `(0.76,0.55,0.32)`  |
| 营地          | 橙 `(0.95,0.55,0.15)`   |
| 雕像          | 灰绿 `(0.55,0.72,0.6)`   |
| 坐骑出生点       | 深绿 `(0,0.5,0.25)`      |
| 宝箱          | 金 `(1,0.78,0.1)`       |
| 科技解锁点       | 蓝 `(0.3,0.55,1)`       |
| 怪物竖线        | 深紫 `(0.55,0.15,0.75)`  |
| **自建传送门**   | 偏蓝灰蓝 `(0.42,0.6,0.95)` |
| **双向传送门连线** | 偏蓝偏亮 `(0.5,0.68,1)`   |

双向传送门样式：在端点标记上方 8px 处画两端竖线段 + 顶部横线（2px 粗），先画线再画点。

## 7. 显示刷新节奏（`MapOverlayBehaviour.Update`）

- 轻刷新 50Hz：玩家位置、怪物竖线/血条、作物条。
- 中刷新 4Hz：时间/季节、金币、职业统计。
- 重刷新 2s（或开关变化触发）：地图标记列表、世界边界、悬停动画清理。
- 世界边界与标记列表只在重刷新里更新；`_hoverAnim` **不要每次重刷新清空**（否则悬停标记会周期性缩回再放大），仅在数量 > 512 时清理。
- 怪物竖线：按 `waveID` 用 `Dictionary<uint, WaveGroup>` 跨帧保留平滑位置与侧别；每帧重算 Min/Max，侧别带 2 只滞回，`Shown` 以 50 世界单位/秒 `MoveTowards` 目标。

## 8. 项目工作习惯（必读）

1. **调试完成、问题解决后，删除所有调试代码与冗余代码**（临时日志、验证分支、未使用字段）。
2. **删除临时搭建的逆向工具**：`%TEMP%\cpp2ilprobe`、`%TEMP%\cecildump`、`%TEMP%\probe*.txt`、`%TEMP%\cecil_out.txt` 等，不在工程内留反编译工具或产物。
3. 设置窗口只做 UI 与开关状态；业务逻辑放对应功能文件，显示逻辑放 `MapOverlayBehaviour.cs`。
4. 编译后必须确认已部署到游戏插件目录，再请用户进游戏验证并核对 `Player.log`。

