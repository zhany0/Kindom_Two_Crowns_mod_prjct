using System;
using System.Collections.Generic;
using Coatsink.Common;
using HarmonyLib;
using UnityEngine;

namespace BetterKingdom
{
    /// <summary>
    /// 功能实现：钱袋/国库联动、墙体内部守墙修正、摇铃立即上船。
    /// 设置窗口只负责开关状态，本文件负责这些功能的运行时逻辑与 Harmony 补丁。
    /// </summary>
    public class FeatureBehaviour : MonoBehaviour
    {
        // 注：构建链为 .NET SDK 6（最高 C# 10），请勿使用主构造函数/集合表达式等 C# 12 语法
        public FeatureBehaviour(IntPtr ptr) : base(ptr) { }

        private const float CacheInterval = 0.25f;   // 玩家/城堡/国库缓存刷新间隔
        private const float HeavyInterval = 2f;      // 墙体修正间隔
        private const float WallQueueClampInterval = 0.25f;  // 禁用排队时复钳间隔（游戏可能在分配补丁之外重写排队值）
        private const float AssaultCheckInterval = 0.2f;    // 墙外进攻怪物检测间隔
        private const int PurseMaxCoins = 38;        // 钱袋容量上限（钻石占 3 金币位，有效金币上限 = 38 - 3×钻石数）
        private const float PayHoldThreshold = 0.4f; // 长按判定阈值（秒），短点击不触发取款
        private const float SafeDistance = 20f;         // 避险安全距离：工人位置距该侧城墙外沿（无墙时距主城）超过此值且该侧有怪物出击，就跑回城墙
        private const float PlayerRunSpeedCap = 5.8f;   // 玩家奔跑最高速度：原版约 4.29，这里提升到 5.8
        private const float PlayerRunSpeedGuard = 4f;   // 判定“正在奔跑”的下限

        private Player _cachedPlayer;
        private Player _cachedPlayer2;
        private Castle _cachedCastle;
        private Banker _cachedBanker;
        private bool _isMenuOpen;
        private float _payHoldStartP1 = -1f, _payHoldStartP2 = -1f;
        private float _nextCacheRefresh, _nextHeavyWork, _nextWallQueueClamp;
        private static int _cachedKingdomId = -1;             // 用于检测换岛（Kingdom 实例变化）
        private static float _nextTimePauseCheck;             // 时间停滞检测节流时间
        private static float _lastDirectorTime = -1f;         // 上次采样到的游戏时刻（判断时钟是否真的不走）
        private static float _timeStuckSince = -1f;           // 时钟连续停滞的起点（-1 表示时钟正常）
        private static int _timeStuckRepairs;                 // 停滞修补轮次：0=先恢复时钟与状态，1=仍不动则清理残留时间停止者

        // 城墙边界缓存（供弓箭手守墙bug修正使用）
        private struct WallBound { public float X; public float HalfW; }
        private static readonly List<WallBound> _wallBounds = new();

        // 禁用排队机制：记录钳制前的原始排队值（键为对象实例ID），关闭开关时按原值立即还原
        private static readonly Dictionary<int, int> _savedGuardDepth = new();
        private static readonly Dictionary<int, int> _savedKnightRank = new();
        private bool _lastDisableWallQueue;
        private static float _nextAssaultCheck;       // 下一次夜间进攻怪物检测时刻
        private static bool _attackOnLeft, _attackOnRight;   // 夜晚该侧城墙外是否有进攻中的怪物
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Enemy> _assaultEnemyBuffer;
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Worker> _suspendWorkerBuffer;
        // 该侧出现进攻怪物时被暂时撤下的“排队工作”（还没开始做）：怪物归零后立即还给原工人。
        // 只撤排队工作、不撤 _currentWork——正在干的活按原生逻辑干完再避险。
        private struct SuspendedJob { public Worker Worker; public Workable Job; }
        private static readonly List<SuspendedJob> _suspendedJobs = new List<SuspendedJob>();
        // 周期性遍历复用缓冲：修正流程每 2 秒执行一次，反复分配 IL2CPP 数组会造成 GC 尖峰与掉帧
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Archer> _archerBuffer;
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Wall> _wallBuffer;
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GuardSlot> _guardSlotBuffer;
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Knight> _knightBuffer;
        // 攻门（付费冲锋）小队优先级：PayableBorder.Pay 遍历骑士时只放行选中的那支小队
        private static int _chargeBestId = -1;          // 选中小队的实例 ID
        private static bool _chargePickActive;          // 处于 PayableBorder.Pay 执行期间
        private static bool _chargePicking;             // 正在计算最佳小队（绕过 CanCharge 过滤）

        // 全局重型刷新槽位：本组件与地图覆盖层的周期刷新共用，同一帧只允许一个重型任务执行，
        // 其余顺延到后续帧，避免多个全场景扫描/遍历叠在同一帧形成帧尖峰
        private static int _heavySlotFrame = -1;

        // 摇铃立即上船的待处理队列：延后一帧再 Embark，避开读档恢复乘客时游戏自身紧接着的 Embark。
        // 同一单位连续两次 Embark 会各占一个座位（第二次占座不释放），下船时重复处理导致整船人卡死。
        internal static readonly List<Embarkee> _pendingInstantBoards = new List<Embarkee>();

        /// <summary>
        /// 申请本帧唯一的重型刷新槽位。返回 false 表示本帧已有其它重型任务在执行，
        /// 调用方不要重置自己的计时器，顺延到下一帧即可（各任务周期远大于一帧，不会饿死）。
        /// </summary>
        public static bool TryReserveHeavySlot()
        {
            int frame = Time.frameCount;
            if (frame == _heavySlotFrame) return false;
            _heavySlotFrame = frame;
            return true;
        }

        private void Awake()
        {
            _lastDisableWallQueue = ModWindowBehaviour.DisableWallQueue;
            // 错峰初始化各周期任务：首帧若全部到期会连续多帧排队，这里按不同相位散开
            float now = Time.unscaledTime;
            _nextAssaultCheck = now;
            _nextCacheRefresh = now + 0.1f;
            _nextTimePauseCheck = now + 0.5f;
            _nextWallQueueClamp = now + 0.6f;
            _nextHeavyWork = now + 0.7f;
        }

        private void Update()
        {
            if (!IsOverlayVisible())
            {
                // 读档/加载期间游戏会自己完成 Embark，本帧入队的请求已无意义，直接丢弃
                _pendingInstantBoards.Clear();
                return;
            }
            ProcessPendingInstantBoards();
            _isMenuOpen = IsMenuOpen();
            SyncWallQueueSwitch();

            float now = Time.unscaledTime;

            // 墙外有进攻怪物时，工人停止接活/撤销无法安全撤离的墙外工作（不区分昼夜：
            // 传送门离城墙较远时游戏会在天黑前提前放怪）
            // 安全判定优先占用重型槽位，其余周期任务都可以顺延一帧，威胁检测不能漏
            if (now >= _nextAssaultCheck && TryReserveHeavySlot())
            {
                _nextAssaultCheck = now + AssaultCheckInterval;
                RefreshSideAssault();
            }

            if (now >= _nextCacheRefresh && TryReserveHeavySlot())
            {
                _nextCacheRefresh = now + CacheInterval;
                RefreshCaches();
            }

            // 血月结束后游戏可能把时钟倍率留在 0，导致时间永久停住（只有重进游戏才恢复）
            if (now >= _nextTimePauseCheck)
            {
                _nextTimePauseCheck = now + 1f;
                EnsureTimeNotStuck(now);
            }

            // 禁用排队时高频复钳：游戏会在夜袭等时机经其它分配路径重新写入排队深度，
            // 只靠分配补丁与 2 秒重活会让弓箭手先短暂走回队列站位
            if (ModWindowBehaviour.DisableWallQueue && now >= _nextWallQueueClamp && TryReserveHeavySlot())
            {
                _nextWallQueueClamp = now + WallQueueClampInterval;
                ClampWallQueueing();
            }

            // 钱袋国库联动（每帧：溢出即时存入，长按取款需帧级按键追踪）
            UpdatePurseTreasuryLink(now);

            if (now >= _nextHeavyWork && TryReserveHeavySlot())
            {
                _nextHeavyWork = now + HeavyInterval;
                FixWallDefensePositions();
            }
        }

        /// <summary>
        /// 处理“摇铃立即上船”的待登船单位：延后一帧，让读档恢复乘客时游戏自身的 Embark 先执行。
        /// 若单位已被游戏登船、目标已失效或已不是船，就跳过，避免重复 Embark 造成重复占座。
        /// </summary>
        private static void ProcessPendingInstantBoards()
        {
            if (_pendingInstantBoards.Count == 0) return;
            if (!ModWindowBehaviour.BellInstantBoard)
            {
                _pendingInstantBoards.Clear();
                return;
            }
            for (int i = _pendingInstantBoards.Count - 1; i >= 0; i--)
            {
                Embarkee embarkee = _pendingInstantBoards[i];
                _pendingInstantBoards.RemoveAt(i);
                if (embarkee == null) continue;
                try
                {
                    if (embarkee.IsEmbarked) continue;
                    Embarkable target = embarkee.EmbarkableTarget;
                    if (target == null || target._owner == null || target._owner.TryCast<Boat>() == null) continue;
                    embarkee.Embark();
                }
                catch { }
            }
        }

        /// <summary>
        /// 检测换岛（Kingdom 实例变化）：旧岛的墙体缓存、已取消工作全部作废，
        /// 避免拿旧岛数据判断新岛。
        /// </summary>
        private void TrackKingdomChange(Kingdom kingdom)
        {
            try
            {
                int kid = kingdom.GetInstanceID();
                if (kid == _cachedKingdomId) return;
                _cachedKingdomId = kid;
                _wallBounds.Clear();
                _suspendedJobs.Clear();
                _pendingInstantBoards.Clear();
                float now = Time.unscaledTime;
                _nextHeavyWork = now + 0.7f;     // 换岛后错峰，避免与其它重型任务同帧执行
            }
            catch { }
        }

        private void RefreshCaches()
        {
            _cachedPlayer = null;
            _cachedPlayer2 = null;
            _cachedCastle = null;
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                if (managers == null) return;
                Kingdom kingdom = managers.kingdom;
                if (kingdom == null) return;

                Player player = kingdom.playerOne ?? kingdom.GetPlayer(0);
                _cachedPlayer = player;
                // 分屏玩家2（单人游戏 GetPlayer(1) 返回空或玩家1本身，后者需剔除）
                try
                {
                    _cachedPlayer2 = kingdom.GetPlayer(1);
                    if (_cachedPlayer2 != null && player != null && _cachedPlayer2.playerId == player.playerId)
                        _cachedPlayer2 = null;
                }
                catch { _cachedPlayer2 = null; }

                _cachedCastle = kingdom.castle;
                TrackKingdomChange(kingdom);

                // 玩家最高移速提升（功能开关）：开启时抬速并记录原始值，关闭时还原
                SyncPlayerSpeedBoost(player, _cachedPlayer2);
            }
            catch { }

            // 银行家（税官）缓存：FindObjectsByType(含隐藏对象) 是全场景遍历，
            // 只有缓存失效（税官被销毁）或从未找到时才重新查找
            try
            {
                if (_cachedBanker != null)
                {
                    try { if (_cachedBanker.gameObject == null) _cachedBanker = null; } catch { _cachedBanker = null; }
                }
                if (_cachedBanker == null)
                {
                    Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                    Kingdom kingdom = managers != null ? managers.kingdom : null;
                    if (kingdom != null)
                    {
                        _cachedBanker = kingdom.banker;
                        if (_cachedBanker == null)
                        {
                            try
                            {
                                var bankers = UnityEngine.Object.FindObjectsByType<Banker>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                                if (bankers.Length > 0) _cachedBanker = bankers[0];
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }

        // 开启移速提升前各坐骑的原始奔跑速度（关闭开关时按原值还原）
        private struct BoostedSteed { public int Id; public Steed Steed; public float BaseRunSpeed; }
        private static readonly List<BoostedSteed> _boostedSteeds = new List<BoostedSteed>();

        /// <summary>
        /// 按“玩家移动速度提升”开关同步移速：开启时把奔跑最高速度抬到 5.8（原版约 4.29），
        /// 关闭时把之前改过的坐骑全部还原，保证开关即时生效。
        /// </summary>
        private static void SyncPlayerSpeedBoost(Player p1, Player p2)
        {
            if (ModWindowBehaviour.BoostPlayerSpeed)
            {
                BoostPlayerRunSpeed(p1);
                BoostPlayerRunSpeed(p2);
                return;
            }
            if (_boostedSteeds.Count == 0) return;

            // 正在奔跑的目标速度也退回原值（只认我们写入的精确值，避免误伤冲刺等更高速度）
            RestorePlayerMoverSpeed(p1);
            RestorePlayerMoverSpeed(p2);
            for (int i = 0; i < _boostedSteeds.Count; i++)
            {
                try { if (_boostedSteeds[i].Steed != null) _boostedSteeds[i].Steed.runSpeed = _boostedSteeds[i].BaseRunSpeed; } catch { }
            }
            _boostedSteeds.Clear();
        }

        /// <summary>
        /// 把玩家的奔跑最高速度提升到 PlayerRunSpeedCap：
        /// 直接改坐骑的 runSpeed，让游戏下一次进入奔跑时按新速度走；
        /// 同时把当前已生效的移动目标速度抬到目标值，避免正在奔跑的这一段仍停留在旧速度。
        /// 只处理 [Guard, Cap) 区间——行走速度远低于 4，冲刺/特殊加速高于 Cap 的一律不动。
        /// </summary>
        private static void BoostPlayerRunSpeed(Player player)
        {
            if (player == null) return;
            try
            {
                Steed steed = player.steed;
                if (steed != null)
                {
                    float speed = steed.runSpeed;
                    if (speed < PlayerRunSpeedCap)
                    {
                        RememberSteedBaseSpeed(steed, speed);   // 只记录第一次的原值
                        steed.runSpeed = PlayerRunSpeedCap;
                    }
                }
            }
            catch { }
            try
            {
                Mover mover = player.mover;
                if (mover != null)
                {
                    float speed = mover._moveSpeed;
                    if (speed >= PlayerRunSpeedGuard && speed < PlayerRunSpeedCap)
                        mover._moveSpeed = PlayerRunSpeedCap;
                }
            }
            catch { }
        }

        /// <summary>记录坐骑被改动前的原始奔跑速度（同一坐骑只记一次）。</summary>
        private static void RememberSteedBaseSpeed(Steed steed, float baseSpeed)
        {
            try
            {
                int id = steed.GetInstanceID();
                for (int i = 0; i < _boostedSteeds.Count; i++)
                    if (_boostedSteeds[i].Id == id) return;
                if (_boostedSteeds.Count >= 8) return;   // 正常只会接触几匹马，上限防止列表无限增长
                _boostedSteeds.Add(new BoostedSteed { Id = id, Steed = steed, BaseRunSpeed = baseSpeed });
            }
            catch { }
        }

        /// <summary>关闭开关时把玩家当前奔跑目标速度退回该坐骑的原始值。</summary>
        private static void RestorePlayerMoverSpeed(Player player)
        {
            if (player == null) return;
            try
            {
                Steed steed = player.steed;
                Mover mover = player.mover;
                if (steed == null || mover == null) return;
                if (mover._moveSpeed < PlayerRunSpeedCap - 0.05f) return;   // 不是我们写入的高值
                int id = steed.GetInstanceID();
                for (int i = 0; i < _boostedSteeds.Count; i++)
                {
                    if (_boostedSteeds[i].Id != id) continue;
                    if (mover._moveSpeed <= PlayerRunSpeedCap + 0.05f)
                        mover._moveSpeed = _boostedSteeds[i].BaseRunSpeed;
                    return;
                }
            }
            catch { }
        }

        /// <summary>钱袋国库联动（每帧调用）：有效金币上限随钻石数下降，超出部分自动存入国库；空袋长按支付键取款填满。</summary>
        private void UpdatePurseTreasuryLink(float now)
        {
            if (!ModWindowBehaviour.LinkPurseTreasury) return;
            Castle castle = _cachedCastle;
            if (castle == null) return;   // 尚无城堡时不做任何转移
            // 国库未解锁（还没有银行家）时没有真实账本：
            // Castle._currentCoinCount 只是城堡金币堆的显示镜像（由 SendStashCount RPC 同步），不参与存档，
            // 写进去金币会从钱袋消失却不落账，读出来还会凭空造币。因此没有银行家时整个联动不生效
            Banker banker = _cachedBanker;
            if (banker == null) return;
            HandlePurseLink(_cachedPlayer, 0, castle, banker, now, ref _payHoldStartP1);
            HandlePurseLink(_cachedPlayer2, 1, castle, banker, now, ref _payHoldStartP2);
        }

        /// <summary>读国库账本：优先税官账本（Banker._stashedCoins），无税官时回退城堡显示字段。</summary>
        private static int ReadTreasury(Banker banker, Castle castle)
        {
            if (banker != null) { try { return banker._stashedCoins; } catch { } }
            try { return castle._currentCoinCount; } catch { return 0; }
        }

        /// <summary>写国库账本：税官账本与城堡显示镜像同步写，避免税官下次存钱时用旧账本覆盖显示造成"回弹"。</summary>
        private static void WriteTreasury(Banker banker, Castle castle, int value)
        {
            if (banker != null) { try { banker._stashedCoins = value; } catch { } }
            try { castle._currentCoinCount = value; } catch { }
            try { castle.SendStashCount(); } catch { }
        }

        /// <summary>单个玩家的钱袋联动：先处理溢出存入，再处理空袋长按取款。</summary>
        private void HandlePurseLink(Player p, int playerIndex, Castle castle, Banker banker, float now, ref float holdStart)
        {
            if (p == null) { holdStart = -1f; return; }
            int coins, gems;
            Wallet wallet;
            try { coins = p.coins; gems = p.gems; wallet = p.wallet; } catch { holdStart = -1f; return; }
            // 钻石占钱袋容量（1 颗钻石 = 3 金币位），有效金币上限 = 38 - 3×钻石数，下限 0
            int maxCoins = Math.Max(0, PurseMaxCoins - 3 * gems);

            // 溢出存入：钱袋金币超过有效上限时把多余部分装入国库
            if (coins > maxCoins)
            {
                int excess = coins - maxCoins;
                try
                {
                    int stash = ReadTreasury(banker, castle);
                    wallet.RemoveCurrency(CurrencyType.Coins, excess);
                    WriteTreasury(banker, castle, stash + excess);
                    coins = maxCoins;
                }
                catch { }
            }

            // 空袋取款：菜单/MOD 窗口打开时游戏输入被屏蔽，不判定按键；
            // 松开按键或袋中有钱时重置计时，保证每次取款前都有一段完整的长按
            bool held = !_isMenuOpen && !ModWindowBehaviour.IsOpen && SafePayHeld(playerIndex);
            if (!held || coins > 0) { holdStart = -1f; return; }
            if (holdStart < 0f) { holdStart = now; return; }        // 开始长按计时
            if (now - holdStart < PayHoldThreshold) return;         // 未达阈值（短点击不触发）
            holdStart = -1f;                                        // 本次长按只触发一次
            try
            {
                int stash = ReadTreasury(banker, castle);
                if (stash <= 0) return;
                // 取出量 = 填满有效上限所需金币数（受钻石占容量影响），国库不足则全取
                int take = Math.Min(maxCoins, stash);
                if (take <= 0) return;
                WriteTreasury(banker, castle, stash - take);
                wallet.AddCurrency(CurrencyType.Coins, take);
            }
            catch { }
        }

        /// <summary>安全读取指定玩家的支付键按住状态（互操作/配置异常时视为未按）。</summary>
        private static bool SafePayHeld(int playerIndex)
        {
            try { return HotkeyBindings.IsPayKeyHeld(playerIndex); } catch { return false; }
        }

        // ==================== 墙体内部守墙修正 ====================

        /// <summary>
        /// 游戏原生bug修正入口：弓箭手守墙待机位有时会位于墙体内部。每隔一段时间重新收集墙体并修正。
        /// </summary>
        private void FixWallDefensePositions()
        {
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return;

                // 禁用守墙排队（不依赖墙体缓存，先执行）
                DisableWallQueueing(kingdom);

                CollectWallBounds(kingdom);
                if (_wallBounds.Count == 0) return;
                // 需要城堡位置判断"墙后"是哪一侧
                if (!TryGetX(kingdom.castle, out float castleX)) return;

                FixGuardSlotsInsideWalls(kingdom, castleX);
                FixArchersInsideWalls(kingdom, castleX);
            }
            catch { }
        }

        /// <summary>
        /// 复仇血月/黑暗收尾兜底：攻破传送门后游戏会暂停时钟并切到红月/黑暗画面，
        /// 正常由 EnemyManager.CheckDanger 在“无怪物攻城 + 无待出波次 + 王国处于非安全”时执行
        /// Director.ResumeTime() 与 ClearRedMoon()/ClearDarkness() 收尾。
        /// 一旦该分支被跳过（例如王国被提前转成安全态），时钟与画面就会永久卡在血月进攻状态，
        /// 只有重进游戏才恢复。这里检测“红月/黑暗标记仍在 + 游戏自身已无危险 + 时钟连续数秒不动”，
        /// 主动补齐收尾流程；若恢复倍率后时钟仍不走，再清理残留的时间停止者。
        /// </summary>
        private static void EnsureTimeNotStuck(float now)
        {
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                if (managers == null) return;
                Director director = managers.director;
                if (director == null) return;
                EnemyManager enemies = managers.enemies;

                float time;
                try { time = director.currentTime; } catch { return; }
                if (_lastDirectorTime < 0f) { _lastDirectorTime = time; _timeStuckSince = -1f; return; }
                bool moving = Mathf.Abs(time - _lastDirectorTime) > 0.0001f;
                _lastDirectorTime = time;

                bool redMoon = false, darkness = false, dangerous = true, playing = true;
                try
                {
                    if (enemies != null)
                    {
                        redMoon = enemies.isRedMoon;
                        darkness = enemies.isDarkness;
                        dangerous = enemies.IsDangerous;
                    }
                }
                catch { dangerous = true; }
                try { playing = director.playing; } catch { }

                // 时钟正常推进，或游戏本身处于暂停（菜单/演出/网络同步）时不做任何事
                if (moving || !playing || Time.timeScale <= 0f)
                {
                    _timeStuckSince = -1f;
                    _timeStuckRepairs = 0;
                    return;
                }
                if (!redMoon && !darkness) { _timeStuckSince = -1f; _timeStuckRepairs = 0; return; }
                // 还有怪物攻城或有待出的复波次时属于正常血月战斗（游戏会主动暂停时钟），不干预
                if (dangerous) { _timeStuckSince = -1f; return; }

                if (_timeStuckSince < 0f) { _timeStuckSince = now; return; }
                // 连续 10 秒无危险且时钟不动才介入：留出正常波次间隔与收尾演出时间
                if (now - _timeStuckSince < 10f) return;

                if (_timeStuckRepairs == 0)
                {
                    // 第一轮：补齐原生收尾（恢复时钟倍率 + 清除红月/黑暗状态，含天空与材质）
                    try { director.ResumeTime(); } catch { }
                    try { enemies.ClearRedMoon(); } catch { }
                    try { enemies.ClearDarkness(); } catch { }
                    try
                    {
                        Kingdom kingdom = managers.kingdom;
                        if (kingdom != null) kingdom.CheckBecomeSafe();
                    }
                    catch { }
                    _timeStuckRepairs = 1;   // 若 10 秒后时钟仍不动，下一轮再清理残留的时间停止者
                }
                else
                {
                    // 第二轮：恢复倍率后时钟依旧不走，说明导演的时间停止者列表里还留着 TimeStopper
                    // （它会让 Director.Update 整段提前返回）。只从列表注销，不销毁/禁用视觉对象。
                    try
                    {
                        var stoppers = director._timeStoppers;
                        if (stoppers != null && stoppers.Count > 0)
                        {
                            for (int i = stoppers.Count - 1; i >= 0; i--)
                            {
                                TimeStopper stopper = stoppers[i];
                                if (stopper == null) continue;
                                try { director.RemoveTimeStopper(stopper); } catch { }
                            }
                        }
                    }
                    catch { }
                    _timeStuckRepairs = 0;
                }
                _timeStuckSince = -1f;
            }
            catch { }
        }

        // ==================== 守墙排队禁用 ====================

        /// <summary>开关翻转检测：开启时立即钳制排队站位，关闭时立即还原游戏原生排队。</summary>
        private void SyncWallQueueSwitch()
        {
            bool disable = ModWindowBehaviour.DisableWallQueue;
            if (disable == _lastDisableWallQueue) return;
            _lastDisableWallQueue = disable;
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return;
                if (disable) DisableWallQueueing(kingdom);
                else RestoreWallQueueing(kingdom);
            }
            catch { }
        }

        /// <summary>取当前王国并复钳排队值（棋盘尚未创建时跳过）。</summary>
        private static void ClampWallQueueing()
        {
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom != null) DisableWallQueueing(kingdom);
            }
            catch { }
        }

        /// <summary>
        /// 禁用游戏自带的守墙排队机制：
        /// 1) 弓箭手：Kingdom.DistributeFreeArchers 按顺序给同侧弓箭手分配递增的 _guardDepth，
        ///    Archer.GetWallTargetPos 按"墙位 ∓（最小间距 + 深度×单位间距 + 随机偏移）"计算站位，
        ///    深度越大离墙越远；深度归零后统一站到墙后最近的可用距离（射程内）。
        /// 2) 骑士小队：Kingdom.RankKnights 给同侧骑士依次写入 rank = 1..N，
        ///    Knight.GetTargetPos 按"墙位 ∓ rank×_distanceFromWall"计算站位，
        ///    rank 归 1 后所有骑士（及其跟随的士兵）都站在墙后最近的一排。
        /// </summary>
        internal static void DisableWallQueueing(Kingdom kingdom)
        {
            if (!ModWindowBehaviour.DisableWallQueue) return;
            try
            {
                Il2CppSystem.Collections.Generic.ICollection<Archer> archers = kingdom.Archers;
                if (archers != null)
                {
                    int count = archers.Count;
                    if (count > 0)
                    {
                        if (_archerBuffer == null || _archerBuffer.Length < count)
                            _archerBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Archer>(Math.Max(count, 64));
                        var arr = _archerBuffer;
                        archers.CopyTo(arr, 0);
                        for (int i = 0; i < count; i++)
                        {
                            Archer a = arr[i];
                            if (a == null) continue;
                            try
                            {
                                // 只处理已分配守墙侧的弓箭手（未分配时 Side 为 0）
                                Side guardSide = a._guardSide;
                                if (guardSide != Side.Left && guardSide != Side.Right) continue;
                                int depth = a._guardDepth;
                                if (depth != 0)
                                {
                                    // 记录钳制前的真实排队深度（重新分配时覆盖为最新值），关闭开关后按此还原
                                    _savedGuardDepth[a.GetInstanceID()] = depth;
                                    a._guardDepth = 0;
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            try
            {
                Il2CppSystem.Collections.Generic.ICollection<Knight> knights = kingdom.Knights;
                if (knights == null) return;
                int count = knights.Count;
                if (count <= 0) return;
                if (_knightBuffer == null || _knightBuffer.Length < count)
                    _knightBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Knight>(Math.Max(count, 16));
                var arr = _knightBuffer;
                knights.CopyTo(arr, 0);
                for (int i = 0; i < count; i++)
                {
                    Knight k = arr[i];
                    if (k == null) continue;
                    try
                    {
                        // rank 从 1 起算，1 = 墙后最近的一排；归 1 即取消骑士序列
                        int rank = k.rank;
                        if (rank > 1)
                        {
                            _savedKnightRank[k.GetInstanceID()] = rank;
                            k.rank = 1;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// 关闭“禁用排队机制”后立即恢复游戏原生排队：
        /// 把钳制前记录的原始 _guardDepth / rank 写回，弓箭手与骑士的目标位置由这两个值推导，
        /// 写回后它们会自行走回排队站位（无需重新触发游戏的分配流程）。
        /// </summary>
        internal static void RestoreWallQueueing(Kingdom kingdom)
        {
            try
            {
                Il2CppSystem.Collections.Generic.ICollection<Archer> archers = kingdom.Archers;
                if (archers != null)
                {
                    int count = archers.Count;
                    if (count > 0)
                    {
                        if (_archerBuffer == null || _archerBuffer.Length < count)
                            _archerBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Archer>(Math.Max(count, 64));
                        var arr = _archerBuffer;
                        archers.CopyTo(arr, 0);
                        for (int i = 0; i < count; i++)
                        {
                            Archer a = arr[i];
                            if (a == null) continue;
                            try
                            {
                                if (_savedGuardDepth.TryGetValue(a.GetInstanceID(), out int depth) && depth > 0)
                                    a._guardDepth = depth;
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            _savedGuardDepth.Clear();

            try
            {
                Il2CppSystem.Collections.Generic.ICollection<Knight> knights = kingdom.Knights;
                if (knights != null)
                {
                    int count = knights.Count;
                    if (count > 0)
                    {
                        if (_knightBuffer == null || _knightBuffer.Length < count)
                            _knightBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Knight>(Math.Max(count, 16));
                        var arr = _knightBuffer;
                        knights.CopyTo(arr, 0);
                        for (int i = 0; i < count; i++)
                        {
                            Knight k = arr[i];
                            if (k == null) continue;
                            try
                            {
                                if (_savedKnightRank.TryGetValue(k.GetInstanceID(), out int rank) && rank > 1)
                                    k.rank = rank;
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            _savedKnightRank.Clear();
        }

        /// <summary>收集真实墙体的水平边界（中心X与半宽），供弓箭手守墙bug修正使用。</summary>
        private void CollectWallBounds(Kingdom kingdom)
        {
            _wallBounds.Clear();
            try
            {
                Il2CppSystem.Collections.Generic.ICollection<Wall> walls = kingdom.Walls;
                if (walls == null) return;
                int count = walls.Count;
                if (count <= 0) return;
                if (_wallBuffer == null || _wallBuffer.Length < count)
                    _wallBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Wall>(Math.Max(count, 16));
                var arr = _wallBuffer;
                walls.CopyTo(arr, 0);
                for (int i = 0; i < count; i++)
                {
                    Wall wall = arr[i];
                    if (!TryGetX(wall, out float x)) continue;
                    // 墙体半宽：优先取碰撞体世界边界，失败用经验值 1（在建城墙通常还没有碰撞体，会走经验值）
                    float halfW = 1f;
                    try
                    {
                        Collider2D col = wall.GetComponentInChildren<Collider2D>();
                        if (col != null) halfW = Math.Max(0.3f, col.bounds.extents.x);
                    }
                    catch { }
                    _wallBounds.Add(new WallBound { X = x, HalfW = halfW });
                }
            }
            catch { }
        }

        /// <summary>若 worldX 位于某墙体内部，计算修正位置（墙后城堡一侧，与墙保持 1 格间距）。</summary>
        private bool TryFixInsideWall(float worldX, float castleX, out float newX, out float wallX)
        {
            for (int i = 0; i < _wallBounds.Count; i++)
            {
                WallBound wb = _wallBounds[i];
                if (Math.Abs(worldX - wb.X) < wb.HalfW + 0.2f)
                {
                    wallX = wb.X;
                    float dir = castleX >= wb.X ? 1f : -1f;
                    newX = wb.X + dir * (wb.HalfW + 1f);
                    return true;
                }
            }
            newX = 0f;
            wallX = 0f;
            return false;
        }

        /// <summary>修正位于墙体内部的守卫槽（GuardSlot），弓箭手会自行走到修正后的位置。</summary>
        private void FixGuardSlotsInsideWalls(Kingdom kingdom, float castleX)
        {
            try
            {
                Il2CppSystem.Collections.Generic.HashSet<GuardSlot> slots = kingdom._guardSlots;
                if (slots == null || slots.Count <= 0) return;
                int slotCount = slots.Count;
                if (_guardSlotBuffer == null || _guardSlotBuffer.Length < slotCount)
                    _guardSlotBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GuardSlot>(Math.Max(slotCount, 16));
                var slotArr = _guardSlotBuffer;
                slots.CopyTo(slotArr, 0);
                for (int i = 0; i < slotCount; i++)
                {
                    GuardSlot slot = slotArr[i];
                    if (slot == null) continue;
                    Vector3 pos;
                    try { pos = slot.transform.position; } catch { continue; }

                    if (TryFixInsideWall(pos.x, castleX, out float newX, out float wallX))
                    {
                        try
                        {
                            slot.transform.position = new Vector3(newX, pos.y, pos.z);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 修正位于墙体内部的弓箭手本体：仅处理守墙状态（ShouldGoToWall 或已在守卫槽）的弓箭手。
        /// 根因：游戏原生bug导致 _guardDepth 守卫深度漂移为负值且不再恢复，
        /// 目标站位 = 墙x ± (最小间距 + 深度×单位间距 + 随机偏移)，负深度会吃掉与墙间距，使弓箭手站进墙体内部。
        /// 治本方案：负深度钳制回 0，再把墙内弓箭手移出。
        /// </summary>
        private void FixArchersInsideWalls(Kingdom kingdom, float castleX)
        {
            try
            {
                Il2CppSystem.Collections.Generic.ICollection<Archer> archers = kingdom.Archers;
                if (archers == null) return;
                int count = archers.Count;
                if (count <= 0) return;
                if (_archerBuffer == null || _archerBuffer.Length < count)
                    _archerBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Archer>(Math.Max(count, 64));
                var arr = _archerBuffer;
                archers.CopyTo(arr, 0);
                for (int i = 0; i < count; i++)
                {
                    Archer a = arr[i];
                    if (a == null) continue;

                    // 只处理守墙状态的弓箭手
                    bool defending = false;
                    try { defending = a.ShouldGoToWall() || a.inGuardSlot; } catch { }
                    if (!defending) continue;

                    Vector3 pos;
                    try { pos = a.transform.position; } catch { continue; }

                    // 治本：守卫深度为负时钳制回 0，目标站位即恢复到墙后正常距离
                    try
                    {
                        int depth = a._guardDepth;
                        if (depth < 0) a._guardDepth = 0;
                    }
                    catch { }

                    // 治标：已站在墙体内的弓箭手立即移出到墙后
                    if (TryFixInsideWall(pos.x, castleX, out float newX, out float wallX))
                    {
                        try
                        {
                            a.transform.position = new Vector3(newX, pos.y, pos.z);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static bool TryGetX<T>(T component, out float x) where T : Component
        {
            x = 0f;
            try { if (component == null) return false; x = component.transform.position.x; return true; }
            catch { return false; }
        }

        // ==================== 工人避险（墙外有进攻怪物时撤回，不区分昼夜） ====================

        /// <summary>
        /// 检测两侧是否有"进攻状态"的怪物（存活、未撤退、未携带战利品逃走）。
        /// 结果供 Worker.ShouldShelter 补丁使用：该侧有怪物且工人自身在安全距离外时跑回城墙，
        /// 直到该侧进攻怪物归零。正在工作的工人不打断（Work 状态优先级高于 Shelter，
        /// 原生行为就是干完手上的活再避险）。
        /// </summary>
        private void RefreshSideAssault()
        {
            bool wasLeft = _attackOnLeft, wasRight = _attackOnRight;
            _attackOnLeft = _attackOnRight = false;
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                // 不按昼夜判断：传送门离城墙较远时游戏会在天黑前提前放怪，
                // 只在夜间检测会把这些白天出击的怪物完全忽略
                if (kingdom != null)
                {
                    if (!TryGetX(kingdom.castle, out float castleX)) { }   // 无城堡时无法判断侧别
                    else
                    {
                        EnemyManager em = managers.enemies;
                        var all = em != null ? em.AllEnemies : null;
                        if (all != null && all.Count > 0)
                        {
                            if (_assaultEnemyBuffer == null || _assaultEnemyBuffer.Length < all.Count)
                                _assaultEnemyBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Enemy>(Math.Max(all.Count, 16));
                            all.CopyTo(_assaultEnemyBuffer, 0);

                            for (int i = 0; i < all.Count; i++)
                            {
                                Enemy e = _assaultEnemyBuffer[i];
                                if (!IsAssaultingEnemy(e)) continue;
                                if (!TryGetX(e, out float x)) continue;
                                if (x < castleX) _attackOnLeft = true; else _attackOnRight = true;
                            }
                        }
                    }
                }
            }
            catch { }

            // 无论是否检测到怪物都要走到这里：怪物刚出时就撤下还没开始的排队工作（不再被派出去），
            // 怪物归零后立即把工作还回去（游戏夜间不会重分配，只能靠这里）
            SuspendUnsafeQueuedWork();
            ResumeSuspendedWork();

            // 某侧进攻怪物刚归零：立即让游戏重新分配工作。
            // 游戏原生的 Kingdom.ReassignWork 只在 Director.IsDaytime 时由 Worker.Behaviour 调用，
            // 夜里收工后不分配，工人要等到天亮才会再出门；这里在威胁解除的瞬间补一次分配
            if ((wasLeft && !_attackOnLeft) || (wasRight && !_attackOnRight))
            {
                try
                {
                    Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                    Kingdom kingdom = managers != null ? managers.kingdom : null;
                    if (kingdom != null) kingdom.ReassignWork();
                }
                catch { }
            }
        }

        /// <summary>
        /// 撤下工人“已经分配但还没开始”的不安全墙外工作（_queuedWork）。
        /// 这是工人仍会跑去工作的根源：工作往往在怪物出现之前就已分配好，AssignJob 拦截管不到，
        /// 而 Work 状态优先级高于 Shelter，工人会一路走到危险区。
        /// 判定只看工作点本身（工作点所属侧是否被进攻、工作点是否在安全距离外），
        /// 与接活工人此时在哪一侧无关：左侧出怪时，右侧工人手里那份左侧危险工作同样会被撤下。
        /// 正在做的工作（_currentWork）不动：按原生逻辑干完手上的活，之后再进入避险回城。
        /// </summary>
        private static void SuspendUnsafeQueuedWork()
        {
            if (!_attackOnLeft && !_attackOnRight) return;
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return;
                var workers = kingdom.Workers;
                if (workers == null || workers.Count <= 0) return;

                if (_suspendWorkerBuffer == null || _suspendWorkerBuffer.Length < workers.Count)
                    _suspendWorkerBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Worker>(Math.Max(workers.Count, 16));
                workers.CopyTo(_suspendWorkerBuffer, 0);

                int count = workers.Count;
                for (int i = 0; i < count; i++)
                {
                    Worker w = _suspendWorkerBuffer[i];
                    if (w == null) continue;
                    Workable job;
                    try { job = w._queuedWork; } catch { continue; }
                    if (job == null) continue;
                    float x;
                    if (!TryGetX(job, out x)) continue;
                    if (IsWorkSpotSafe(kingdom, x)) continue;   // 工作点在安全范围内，照常去干

                    // 走游戏自己的取消流程释放工作位占用，否则之后同类工作不会再被分配
                    try { job.CancelWork(w); } catch { }
                    try { if (w._queuedWork == job) w._queuedWork = null; } catch { }
                    if (_suspendedJobs.Count < 64)
                        _suspendedJobs.Add(new SuspendedJob { Worker = w, Job = job });
                }
            }
            catch { }
        }

        /// <summary>
        /// 撤销某个工人手上“已分配但还没开始”的不安全工作，并清掉强制工作标记。
        /// 游戏还有绕过 AssignJob 的分配路径（例如付费接活：IUnitControllable.UpdatePayState 直接写 _queuedWork），
        /// 补丁在那些动作结束后立刻调用本方法，保证进攻期间危险工作点不会被任何人接下。
        /// </summary>
        public static bool CancelUnsafeQueuedWork(Worker worker)
        {
            if (worker == null) return false;
            try
            {
                Workable job = worker._queuedWork;
                if (job == null) return false;
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return false;
                float x;
                if (!TryGetX(job, out x)) return false;
                if (IsWorkSpotSafe(kingdom, x)) return false;   // 工作点在安全范围内，照常去干

                try { job.CancelWork(worker); } catch { }
                try { if (worker._queuedWork == job) worker._queuedWork = null; } catch { }
                try { worker._forceWork = false; } catch { }   // 付费接活会置强制工作标志，撤销时一并清掉
                return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 某份工作此刻能不能被工人真正开始干：进攻怪物在该工作点所属侧且超出安全距离时一律不行。
        /// 供 Worker.SetCurrentWork 补丁作为最后一道防线（所有把排队工作转成当前工作的路径都经过它）。
        /// </summary>
        public static bool CanStartWork(Workable job)
        {
            if (job == null) return true;
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return true;
                float x;
                if (!TryGetX(job, out x)) return true;
                return IsWorkSpotSafe(kingdom, x);
            }
            catch { }
            return true;
        }

        /// <summary>
        /// 该侧进攻怪物归零后立刻把撤下的排队工作还回去（不等天亮，游戏夜间不会重新分配工作）。
        /// 工作位暂时不可用或被别处占用时保留记录，下次刷新再试。
        /// </summary>
        private static void ResumeSuspendedWork()
        {
            if (_suspendedJobs.Count == 0) return;
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return;

                for (int i = _suspendedJobs.Count - 1; i >= 0; i--)
                {
                    SuspendedJob c = _suspendedJobs[i];
                    if (c.Worker == null || c.Job == null) { _suspendedJobs.RemoveAt(i); continue; }

                    float x;
                    if (!TryGetX(c.Job, out x)) { _suspendedJobs.RemoveAt(i); continue; }
                    if (!IsWorkSpotSafe(kingdom, x)) continue;   // 工作点所属侧仍有进攻怪物或在安全距离外，继续等待

                    bool busy;
                    try { busy = c.Worker._queuedWork != null || c.Worker._currentWork != null; }
                    catch { busy = true; }
                    if (busy) { _suspendedJobs.RemoveAt(i); continue; }   // 游戏已经重新派了活

                    bool available;
                    try { available = c.Job.JobAvailable(); } catch { available = false; }
                    if (!available) continue;   // 工作位临时被占用，下次再试

                    _suspendedJobs.RemoveAt(i);
                    try { c.Worker.AssignJob(c.Job); } catch { }
                }
            }
            catch { }
        }

        // ==================== 攻门（付费冲锋）小队优先级 ====================

        /// <summary>
        /// 付费攻门时挑选出动小队：先比人数（满员优先），人数相同优先高级骑士，再比距离（更近优先）。
        /// 原版在 Kingdom.Knights 哈希集上遍历、只按弓箭手人数取最大值，人数相同则"后遍历者"胜出，
        /// 顺序随机且不看骑士等级。
        /// </summary>
        public static void PrepareChargePick(PayableBorder border)
        {
            _chargeBestId = -1;
            _chargePickActive = true;
            _chargePicking = true;
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null || border == null) return;

                var knights = kingdom.Knights;
                if (knights == null || knights.Count <= 0) return;

                float borderX;
                if (!TryGetX(border, out borderX)) return;
                float range = border.knightRange;

                var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Knight>(knights.Count);
                knights.CopyTo(arr, 0);

                int bestCount = -1;
                bool bestPromoted = false;
                float bestDist = float.MaxValue;

                for (int i = 0; i < arr.Length; i++)
                {
                    Knight k = arr[i];
                    if (k == null) continue;
                    if (!k.CanCharge()) continue;   // 状态/队形/弓箭手/传送门条件
                    if (!TryGetX(k, out float kx)) continue;
                    float dist = Mathf.Abs(kx - borderX);
                    if (dist > range) continue;

                    int count;
                    try { count = k.numArchers; } catch { count = 0; }
                    if (count <= 0) continue;
                    bool promoted;
                    try { promoted = k._wasPromoted; } catch { promoted = false; }

                    bool better;
                    if (count != bestCount) better = count > bestCount;
                    else if (promoted != bestPromoted) better = promoted;
                    else better = dist < bestDist;
                    if (!better) continue;

                    bestCount = count;
                    bestPromoted = promoted;
                    bestDist = dist;
                    _chargeBestId = k.GetInstanceID();
                }
            }
            catch { }
            finally { _chargePicking = false; }
        }

        /// <summary>PayableBorder.Pay 结束后清除攻门选人上下文。</summary>
        public static void FinishChargePick()
        {
            _chargePickActive = false;
            _chargeBestId = -1;
        }

        /// <summary>PayableBorder.Pay 遍历骑士期间，只让选中的小队通过 CanCharge。</summary>
        public static bool AllowChargeCandidate(Knight k)
        {
            if (!_chargePickActive || _chargePicking) return true;
            if (_chargeBestId < 0) return true;   // 没有可冲锋小队时保持原版行为
            try { return k.GetInstanceID() == _chargeBestId; } catch { }
            return true;
        }

        /// <summary>怪物是否处于进攻状态：在场、存活、未决定撤退、未携带战利品离开。
        /// 携带战利品（捡到金币/王冠拖回传送门）或已开始消失的怪物都不算进攻怪物，
        /// 否则它们会在回程途中被误判成威胁，导致工人迟迟不敢出门工作。</summary>
        private static bool IsAssaultingEnemy(Enemy e)
        {
            try
            {
                if (e == null || e.IsDespawning) return false;
                if (e.shouldRetreat) return false;   // 已决定撤回传送门
                Damageable d = e.damageable;
                if (d != null && d.isDead) return false;
                if (!e.gameObject.activeSelf) return false;
                return e.loot == null;   // 捡到金币/战利品逃走的怪物不再算进攻
            }
            catch { }
            return false;
        }

        /// <summary>该侧城墙外是否有进攻中的怪物（含天黑前提前出击的怪物）。</summary>
        public static bool IsSideUnderAttack(Side side)
        {
            if (side == Side.Left) return _attackOnLeft;
            if (side == Side.Right) return _attackOnRight;
            return false;
        }

        /// <summary>
        /// 该工人此刻是否应该跑回城墙避险：它所在的一侧有进攻中的怪物，且它自身位置在安全距离之外。
        /// 供 Worker.ShouldShelter 补丁使用——把结果强制为真后，游戏自己的 Shelter 状态会把工人带回城墙驻守。
        /// 不干预工作：Work 状态优先级高于 Shelter，正在干活/已排活的工会先把活干完再避险（与游戏原生一致）。
        /// </summary>
        public static bool ShouldFleeToWall(Worker w)
        {
            if (!_attackOnLeft && !_attackOnRight) return false;
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return false;
                float x;
                if (!TryGetX(w, out x)) return false;
                return !IsPositionSafe(kingdom, x, AttackSideFromX(kingdom, x));
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 判断某个位置在该侧是否处于安全距离内。
        /// 该侧没有出击怪物时任何位置都安全；墙内始终安全；墙外则要求距该侧城墙外沿不超过 SafeDistance，
        /// 该侧还没有城墙时以主城（城堡）位置为参照起算。
        /// </summary>
        public static bool IsPositionSafe(Kingdom kingdom, float x, Side side)
        {
            try
            {
                if (!IsSideUnderAttack(side)) return true;   // 该侧没有出击的怪物：任何位置都安全

                bool within;
                try { within = kingdom.IsWithinWalls(x); } catch { within = true; }
                if (within) return true;                    // 墙内始终安全

                float outerX;
                if (!TryGetOuterWallX(kingdom, side, out outerX)) return false;   // 连城堡位置都取不到时按不安全处理

                return Mathf.Abs(x - outerX) <= SafeDistance;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 统一的工作点安全判定：以“工作点所属侧”为准，判断该工作点此刻能不能被任何人接受。
        /// 只要该侧有进攻怪物、工作点在墙外且超出安全距离，就返回 false——与接活工人当前在哪一侧无关。
        /// AssignJob 拦截、排队工作撤下/还回全部走这一条判据，避免两侧口径不一致。
        /// </summary>
        public static bool IsWorkSpotSafe(Kingdom kingdom, float x)
        {
            if (kingdom == null) return true;
            return IsPositionSafe(kingdom, x, AttackSideFromX(kingdom, x));
        }

        /// <summary>
        /// 按主城（城堡）位置把坐标分到左右侧——与怪物分侧口径完全一致。
        /// 以前工作点用露营火堆分侧、怪物用城堡分侧，两者位置略有差异时边界附近的工作会被判到另一侧。
        /// 取不到城堡位置时退回露营火堆位置兜底。
        /// </summary>
        private static Side AttackSideFromX(Kingdom kingdom, float x)
        {
            float divider;
            if (TryGetX(kingdom != null ? kingdom.castle : null, out divider))
                return x < divider ? Side.Left : Side.Right;
            return SideFromX(kingdom, x);
        }

        /// <summary>
        /// 取该侧“安全距离”边界的世界 X（供小地图红线使用）：
        /// 左侧为城墙外沿 - SafeDistance，右侧为外沿 + SafeDistance；该侧还没有城墙时以主城位置为参照。
        /// </summary>
        public static bool TryGetSafeEdge(Side side, out float x)
        {
            x = 0f;
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return false;
                float outerX;
                if (!TryGetOuterWallX(kingdom, side, out outerX)) return false;
                x = side == Side.Left ? outerX - SafeDistance : outerX + SafeDistance;
                return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 取该侧城墙外沿 X：只统计城堡该侧（左侧 X &lt; 城堡X、右侧 X &gt; 城堡X）的墙，
        /// 左侧取最左墙的左边缘、右侧取最右墙的右边缘；该侧还没有城墙时退回城堡位置（主城旁），
        /// 避免拿另一侧的城墙当参照。
        /// </summary>
        private static bool TryGetOuterWallX(Kingdom kingdom, Side side, out float x)
        {
            x = 0f;
            float castleX;
            bool hasCastle = TryGetX(kingdom != null ? kingdom.castle : null, out castleX);
            int n = _wallBounds.Count;
            bool found = false;
            float best = 0f;
            for (int i = 0; i < n; i++)
            {
                WallBound b = _wallBounds[i];
                // 只统计城堡这一侧的墙：对侧的墙不能作为本侧的安全距离参照
                if (hasCastle)
                {
                    if (side == Side.Left && b.X > castleX) continue;
                    if (side == Side.Right && b.X < castleX) continue;
                }
                float edge = side == Side.Left ? b.X - b.HalfW : b.X + b.HalfW;
                if (!found || (side == Side.Left ? edge < best : edge > best))
                {
                    best = edge;
                    found = true;
                }
            }
            if (found)
            {
                x = best;
                return true;
            }
            // 该侧没有城墙（或尚未建成）：以城堡（营地）位置为参照，
            // 否则会因为没有墙而把所有墙外工作都判成危险，连第一道墙都建不起来
            if (hasCastle)
            {
                x = castleX;
                return true;
            }
            return false;
        }

        /// <summary>按露营火堆位置判断世界坐标位于地图哪一侧。</summary>
        internal static Side SideFromX(Kingdom kingdom, float x)
        {
            float divider = 0f;
            try { divider = kingdom.CampfireWorldPosition; } catch { }
            return x < divider ? Side.Left : Side.Right;
        }

        /// <summary>与地图覆盖层一致的可运行状态：读档/开场/退出之外均执行（含菜单，此时仅溢出存入照常生效）。</summary>
        private static bool IsOverlayVisible()
        {
            try
            {
                Managers m = SingletonMonoBehaviour<Managers>.Inst;
                if (m == null) return false;
                Game g = m.game;
                if (g == null) return false;
                var s = g.state;
                return s != Game.State.Loading && s != Game.State.Intro && s != Game.State.Quitting;
            }
            catch { return false; }
        }

        private static bool IsMenuOpen()
        {
            try
            {
                Managers m = SingletonMonoBehaviour<Managers>.Inst;
                if (m == null) return false;
                Game g = m.game;
                return g != null && (g.state & Game.State.Menu) != 0;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 摇铃后 NPC 立即上船：游戏给单位指派上船目标（SetEmbarkableTarget）是摇铃召唤的唯一途径，
    /// 此处若目标是船且单位为工人/弓箭手/骑士（摇铃只召唤这三类），把单位加入待登船队列，
    /// 由本组件的 Update 在下一帧执行 Embark，省去跑动。
    /// 玩家无这三类组件，天然不受影响；随从弓箭手由骑士上船时指派，同样被本补丁传送。
    /// 不能在这里直接 Embark：读档恢复乘客时游戏紧跟着自己也会调用 Embark，重复调用会重复占座，
    /// 下船时同一单位被处理两次，导致整船人卡在码头。延后一帧后若已登船就跳过。
    /// 开关关闭时补丁直接放行。
    /// </summary>
    [HarmonyPatch(typeof(Embarkee), nameof(Embarkee.SetEmbarkableTarget))]
    public static class EmbarkeeInstantBoardPatch
    {
        public static void Postfix(Embarkee __instance, Embarkable embarkable)
        {
            if (!ModWindowBehaviour.BellInstantBoard) return;
            if (__instance == null || embarkable == null) return;
            try
            {
                // 仅响应以"船"为目标的指派（排除舰队小艇等其他 Embarkable）
                if (embarkable._owner == null || embarkable._owner.TryCast<Boat>() == null) return;
                // 玩家与其他单位无这三类组件，不会被误传送
                if (__instance.GetComponent<Worker>() == null
                    && __instance.GetComponent<Archer>() == null
                    && __instance.GetComponent<Knight>() == null) return;
                if (__instance.IsEmbarked) return;
                FeatureBehaviour._pendingInstantBoards.Add(__instance);
            }
            catch { }
        }
    }

    /// <summary>游戏重新分配守墙弓箭手后立即取消排队，避免出现短暂的远距离站位。</summary>
    [HarmonyPatch(typeof(Kingdom), nameof(Kingdom.DistributeArchers))]
    public static class WallQueueArcherDistributionPatch
    {
        public static void Postfix(Kingdom __instance)
        {
            FeatureBehaviour.DisableWallQueueing(__instance);
        }
    }

    /// <summary>游戏重排骑士 rank 后立即取消骑士排队，保证骑士小队也站在墙后最近的一排。</summary>
    [HarmonyPatch(typeof(Kingdom), nameof(Kingdom.RankKnights))]
    public static class WallQueueKnightRankPatch
    {
        public static void Postfix(Kingdom __instance)
        {
            FeatureBehaviour.DisableWallQueueing(__instance);
        }
    }

    /// <summary>
    /// 游戏重新分配空闲弓箭手（守墙排队的主要路径，夜袭开始时触发）后立即取消排队。
    /// DistributeArchers 只处理部分情形，实际分配守墙深度的是 DistributeFreeArchers。
    /// </summary>
    [HarmonyPatch(typeof(Kingdom), nameof(Kingdom.DistributeFreeArchers))]
    public static class WallQueueFreeArcherDistributionPatch
    {
        public static void Postfix(Kingdom __instance)
        {
            FeatureBehaviour.DisableWallQueueing(__instance);
        }
    }

    /// <summary>
    /// 拒绝不安全的墙外工作：该工作点所属侧有怪物出击、且工作点超出安全距离时，不论哪个工人来接手一律拒绝，
    /// 直到该侧进攻怪物归零。判定只看工作点，不看工人当前在哪一侧——左侧出怪时右侧工人也不能接左侧危险活。
    /// 已经在做的活不受影响（Work 优先级高于 Shelter，干完后由 ShouldShelter 补丁带回城墙），
    /// 与“不干预工作行为”一致；该侧无怪物、工作点墙内或安全距离内一律放行。
    /// </summary>
    [HarmonyPatch(typeof(Worker), nameof(Worker.AssignJob))]
    public static class WorkerAssignSafeJobPatch
    {
        public static bool Prefix(Workable job)
        {
            return FeatureBehaviour.CanStartWork(job);
        }
    }

    /// <summary>
    /// 最后一道防线：任何把排队工作转成“当前工作”的路径（包括绕过 AssignJob 的付费接活）都会被拦下。
    /// Worker.Behaviour 协程里 SetCurrentWork(_queuedWork) 是唯一的转换漏斗，工作点在进攻侧安全距离外时
    /// 拒绝启动并释放工作位；传 null 的调用（清空工作）一律放行。
    /// </summary>
    [HarmonyPatch(typeof(Worker), nameof(Worker.SetCurrentWork))]
    public static class WorkerSetCurrentWorkSafePatch
    {
        public static bool Prefix(Worker __instance, Workable __0)
        {
            if (__0 == null) return true;
            if (FeatureBehaviour.CanStartWork(__0)) return true;
            try { __0.CancelWork(__instance); } catch { }   // 释放工作位，避免之后同类工作再也分配不出去
            try { if (__instance != null && __instance._queuedWork == __0) __instance._queuedWork = null; } catch { }
            return false;
        }
    }

    /// <summary>
    /// 付费接活路径：Worker 实现 IUnitControllable 的 UpdatePayState 在玩家长按支付时，
    /// 会把付费范围内的 Workable 直接写进 _queuedWork 并置强制工作标志，完全绕过 AssignJob。
    /// 这里在方法结束后立刻复查，工作点在进攻侧安全距离外时立即撤销，避免工人被派去危险区域。
    /// 注意 interop 里显式接口方法名为下划线形式（IUnitControllable_UpdatePayState）。
    /// </summary>
    [HarmonyPatch(typeof(Worker), nameof(Worker.IUnitControllable_UpdatePayState))]
    public static class WorkerPayWorkSafePatch
    {
        public static void Postfix(Worker __instance)
        {
            FeatureBehaviour.CancelUnsafeQueuedWork(__instance);
        }
    }

    /// <summary>
    /// 工人避险回城：游戏原生的 ShouldShelter 只在 Kingdom.isSafe 为假时才会返回真，
    /// 白天（或换岛后王国被兜底转成安全态）即使本侧有怪物出击，工人也不会退回城墙。
    /// 这里在“该侧有怪物出击 + 工人自身已在安全距离之外”时强制结果取真，
    /// 让游戏自己的 Shelter 状态把工人带回城墙驻守，直到该侧进攻怪物归零。
    /// 不干预工作：Work 状态优先级高于 Shelter，正在干活的工会先干完再撤退（与游戏原生一致）。
    /// 注意：只改状态选择结果，全程不碰 Kingdom.isSafe 与 Director 时钟，
    /// 因此不会重新引发“复仇血月结束后时间卡住”的问题。
    /// </summary>
    [HarmonyPatch(typeof(Worker), nameof(Worker.ShouldShelter))]
    public static class WorkerRecallShelterPatch
    {
        public static void Postfix(Worker __instance, ref bool __result)
        {
            if (__result || __instance == null) return;
            if (FeatureBehaviour.ShouldFleeToWall(__instance)) __result = true;
        }
    }

    /// <summary>
    /// 付费攻门前先挑出本次出动的骑士小队，供 CanCharge 过滤使用。
    /// 原版按哈希集遍历、只看弓箭手人数，顺序随机且不区分骑士等级。
    /// </summary>
    [HarmonyPatch(typeof(PayableBorder), nameof(PayableBorder.Pay))]
    public static class ChargeSquadPriorityPayPatch
    {
        public static void Prefix(PayableBorder __instance)
        {
            FeatureBehaviour.PrepareChargePick(__instance);
        }

        public static void Postfix()
        {
            FeatureBehaviour.FinishChargePick();
        }
    }

    /// <summary>
    /// PayableBorder.Pay 遍历骑士期间只放行选中的小队，
    /// 从而让原版循环选中"满员 → 高级骑士 → 更近"的那一支。
    /// </summary>
    [HarmonyPatch(typeof(Knight), nameof(Knight.CanCharge))]
    public static class ChargeSquadPriorityCanChargePatch
    {
        public static void Postfix(Knight __instance, ref bool __result)
        {
            if (!__result || __instance == null) return;
            if (!FeatureBehaviour.AllowChargeCandidate(__instance)) __result = false;
        }
    }
}
