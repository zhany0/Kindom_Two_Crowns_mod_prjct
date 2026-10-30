using System;
using System.Collections.Generic;
using Coatsink.Common;
using HarmonyLib;
using UnityEngine;

namespace BetterKingdom
{
    /// <summary>
    /// 功能实现：钱袋/国库联动、墙体内部守墙修正、摇铃立即上船、传送门摧毁后的工人分配。
    /// 设置窗口只负责开关状态，本文件负责这些功能的运行时逻辑与 Harmony 补丁。
    /// </summary>
    public class FeatureBehaviour : MonoBehaviour
    {
        // 注：构建链为 .NET SDK 6（最高 C# 10），请勿使用主构造函数/集合表达式等 C# 12 语法
        public FeatureBehaviour(IntPtr ptr) : base(ptr) { }

        private const float CacheInterval = 0.25f;   // 玩家/城堡/国库缓存刷新间隔
        private const float HeavyInterval = 2f;      // 墙体修正与传送门工人检查间隔
        private const float WallQueueClampInterval = 0.25f;  // 禁用排队时复钳间隔（游戏可能在分配补丁之外重写排队值）
        private const float AssaultCheckInterval = 0.2f;    // 夜间墙外进攻怪物检测间隔
        private const int PurseMaxCoins = 38;        // 钱袋容量上限（钻石占 3 金币位，有效金币上限 = 38 - 3×钻石数）
        private const float PayHoldThreshold = 0.4f; // 长按判定阈值（秒），短点击不触发取款
        private const int PortalSideWorkerLimit = 2; // 传送门被摧毁后该侧保留的工人数

        private Player _cachedPlayer;
        private Player _cachedPlayer2;
        private Castle _cachedCastle;
        private Banker _cachedBanker;
        private bool _isMenuOpen;
        private float _payHoldStartP1 = -1f, _payHoldStartP2 = -1f;
        private float _nextCacheRefresh, _nextHeavyWork, _nextPortalCheck, _nextWallQueueClamp;
        private static float _nextCaveScan;  // 兜底场景搜索洞窟的节流时间

        // 城墙边界缓存（供弓箭手守墙bug修正使用）
        private struct WallBound { public float X; public float HalfW; }
        private readonly List<WallBound> _wallBounds = new();

        // 禁用排队机制：记录钳制前的原始排队值（键为对象实例ID），关闭开关时按原值立即还原
        private static readonly Dictionary<int, int> _savedGuardDepth = new();
        private static readonly Dictionary<int, int> _savedKnightRank = new();
        private bool _lastDisableWallQueue;
        private static bool _portalCheckRequested;   // 下一帧执行一次传送门工人检查
        private static float _nextAssaultCheck;       // 下一次夜间进攻怪物检测时刻
        private static bool _attackOnLeft, _attackOnRight;   // 夜晚该侧城墙外是否有进攻中的怪物
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Enemy> _assaultEnemyBuffer;
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Worker> _assaultWorkerBuffer;
        // 夜间避险时被取消的墙外工作：威胁解除后还给原工人，避免工人整夜闲置（游戏夜间不会重新分配工作）
        private struct CancelledJob { public Worker Worker; public Workable Job; public Side Side; }
        private static readonly List<CancelledJob> _cancelledJobs = new List<CancelledJob>();
        // 攻门（付费冲锋）小队优先级：PayableBorder.Pay 遍历骑士时只放行选中的那支小队
        private static int _chargeBestId = -1;          // 选中小队的实例 ID
        private static bool _chargePickActive;          // 处于 PayableBorder.Pay 执行期间
        private static bool _chargePicking;             // 正在计算最佳小队（绕过 CanCharge 过滤）

        /// <summary>当前实例（供 Harmony 补丁在传送门摧毁时立即触发检查）。</summary>
        public static FeatureBehaviour Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            _lastDisableWallQueue = ModWindowBehaviour.DisableWallQueue;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!IsOverlayVisible()) return;
            _isMenuOpen = IsMenuOpen();
            SyncWallQueueSwitch();

            float now = Time.unscaledTime;

            // 夜晚墙外有进攻怪物时，工人停止接活/撤销未开始的墙外工作
            if (now >= _nextAssaultCheck)
            {
                _nextAssaultCheck = now + AssaultCheckInterval;
                RefreshSideAssault();
            }

            if (now >= _nextCacheRefresh)
            {
                _nextCacheRefresh = now + CacheInterval;
                RefreshCaches();
            }

            // 禁用排队时高频复钳：游戏会在夜袭等时机经其它分配路径重新写入排队深度，
            // 只靠分配补丁与 2 秒重活会让弓箭手先短暂走回队列站位
            if (ModWindowBehaviour.DisableWallQueue && now >= _nextWallQueueClamp)
            {
                _nextWallQueueClamp = now + WallQueueClampInterval;
                ClampWallQueueing();
            }

            // 钱袋国库联动（每帧：溢出即时存入，长按取款需帧级按键追踪）
            UpdatePurseTreasuryLink(now);

            if (now >= _nextHeavyWork)
            {
                _nextHeavyWork = now + HeavyInterval;
                FixWallDefensePositions();
            }

            if (_portalCheckRequested || now >= _nextPortalCheck)
            {
                _portalCheckRequested = false;
                _nextPortalCheck = now + HeavyInterval;
                EnforcePortalWorkerLimit();
            }
        }

        /// <summary>
        /// 请求执行一次传送门工人检查（传送门摧毁或游戏重新分配工人时调用）。
        /// 只置标志、下一帧 Update 里执行：若在 DistributeWorkers 的 Postfix 里直接改派，
        /// 会打断游戏正在进行的工人分配，影响投石车等岗位的人手。
        /// </summary>
        public static void RequestPortalWorkerCheck()
        {
            _portalCheckRequested = true;
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
                        var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Archer>(count);
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
                var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Knight>(count);
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
                        var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Archer>(count);
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
                        var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Knight>(count);
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
                var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Wall>(count);
                walls.CopyTo(arr, 0);
                for (int i = 0; i < count; i++)
                {
                    Wall wall = arr[i];
                    if (!TryGetX(wall, out float x)) continue;
                    // 墙体半宽：优先取碰撞体世界边界，失败用经验值 1
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
                var slotArr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GuardSlot>(slotCount);
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
                var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Archer>(count);
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

        // ==================== 传送门摧毁后的工人分配 ====================

        /// <summary>
        /// 传送门（非洞窟）被摧毁后，该侧工人只保留 2 名，其余全部改派到洞窟一侧。
        /// 游戏原生 DistributeWorkers 会在两侧各保留 4 名工人，这里在其执行之后再做一次上限收敛。
        /// </summary>
        private void EnforcePortalWorkerLimit()
        {
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom != null) EnforcePortalWorkerLimit(kingdom);
            }
            catch { }
        }

        private static void EnforcePortalWorkerLimit(Kingdom kingdom)
        {
            try
            {
                // 传送门集合为空通常意味着读档早期尚未注册，此时两侧都没有传送门，
                // 若直接判断会把正常一侧误判为"传送门已摧毁"
                int portalCount = 0;
                try { Il2CppSystem.Collections.Generic.HashSet<Portal> all = kingdom.AllPortals; if (all != null) portalCount = all.Count; } catch { }
                if (portalCount == 0) return;

                Side caveSide;
                if (!TryGetCaveSide(kingdom, out caveSide)) return;   // 没有洞窟时不做调整

                // 两侧分别检查：只处理没有可用传送门的一侧，且该侧不是洞窟一侧
                CheckPortalSide(kingdom, Side.Left, caveSide);
                CheckPortalSide(kingdom, Side.Right, caveSide);
            }
            catch { }
        }

        /// <summary>指定一侧如果已经没有可用传送门，则把该侧工人收敛到 2 名，多余者改派洞窟一侧。</summary>
        private static void CheckPortalSide(Kingdom kingdom, Side portalSide, Side caveSide)
        {
            if (portalSide == caveSide) return;
            if (HasUsablePortalOn(kingdom, portalSide)) return;
            CapWorkersOnSide(kingdom, portalSide, caveSide);
        }

        /// <summary>判断该侧是否存在仍可用（非摧毁状态）的普通或码头传送门。</summary>
        private static bool HasUsablePortalOn(Kingdom kingdom, Side side)
        {
            try
            {
                foreach (Portal portal in kingdom.AllPortals)
                {
                    if (portal == null) continue;
                    try
                    {
                        if (portal.type == Portal.Type.Cliff) continue;   // 洞窟不是传送门
                    }
                    catch { continue; }

                    if (!TryGetX(portal, out float x)) continue;
                    if (SideFromX(kingdom, x) != side) continue;

                    Portal.State state;
                    try { state = portal.state; } catch { continue; }
                    if (state != Portal.State.Crumbled && state != Portal.State.Rebuilding
                        && state != Portal.State.Destroyed && state != Portal.State.Passable)
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>把该侧工人保留 2 名，其余空闲者改派洞窟一侧（有工作的工人不动，下一轮空闲时再改派）。</summary>
        private static void CapWorkersOnSide(Kingdom kingdom, Side portalSide, Side caveSide)
        {
            try
            {
                Il2CppSystem.Collections.Generic.ICollection<Worker> workers = kingdom.Workers;
                if (workers == null) return;
                int count = workers.Count;
                if (count <= 0) return;

                var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Worker>(count);
                workers.CopyTo(arr, 0);

                int kept = 0;
                for (int i = 0; i < count; i++)
                {
                    Worker worker = arr[i];
                    if (worker == null) continue;

                    Side targetSide;
                    try { targetSide = worker._targetSide; } catch { continue; }
                    if (targetSide != portalSide) continue;

                    if (kept < PortalSideWorkerLimit) { kept++; continue; }

                    // 只改派空闲工人：正在施工、推油桶（投石车装弹）等有工作的工人绝不打断，
                    // 否则会抢走投石车操作员，或因反复清空工作让工人永远走不到新一侧
                    bool busy;
                    try { busy = worker._currentWork != null || worker._queuedWork != null; } catch { busy = true; }
                    if (busy) continue;

                    try { worker.SetWorkerSide(caveSide); } catch { }
                    try { worker._targetSide = caveSide; } catch { }
                }
            }
            catch { }
        }

        // ==================== 夜晚工人避险（墙外有进攻怪物时不工作） ====================

        /// <summary>
        /// 夜晚检测两侧城墙外是否有"进攻状态"的怪物（存活、未撤退、未携带战利品逃走）。
        /// 墙外有待执行工作时工人会优先去干活而不是退回城墙，速度又不及怪物，容易被攻击；
        /// 这里在检测结果由"无"变"有"时撤销该侧工人已排队但未开始的墙外工作，
        /// 新工作由 AssignJob 的 Prefix 拦截，避免每帧反复改动游戏状态。
        /// </summary>
        private void RefreshSideAssault()
        {
            bool wasLeft = _attackOnLeft, wasRight = _attackOnRight;
            _attackOnLeft = _attackOnRight = false;
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Director director = managers != null ? managers.director : null;
                if (director == null || director.IsDaytime) return;   // 白天不处理

                Kingdom kingdom = managers.kingdom;
                if (kingdom == null) return;
                if (!TryGetX(kingdom.castle, out float castleX)) return;   // 无城堡时无法判断侧别

                EnemyManager em = managers.enemies;
                var all = em != null ? em.AllEnemies : null;
                if (all == null || all.Count <= 0) return;

                if (_assaultEnemyBuffer == null || _assaultEnemyBuffer.Length < all.Count)
                    _assaultEnemyBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Enemy>(Math.Max(all.Count, 16));
                all.CopyTo(_assaultEnemyBuffer, 0);

                for (int i = 0; i < all.Count; i++)
                {
                    Enemy e = _assaultEnemyBuffer[i];
                    if (!IsAssaultingEnemy(e)) continue;
                    if (!TryGetX(e, out float x)) continue;
                    bool within;
                    try { within = kingdom.IsWithinWalls(x); } catch { within = true; }
                    if (within) continue;   // 城墙内的怪物不算威胁
                    if (x < castleX) _attackOnLeft = true; else _attackOnRight = true;
                }
            }
            catch { }

            if (_attackOnLeft && !wasLeft) CancelOutsideNightWork(Side.Left);
            if (_attackOnRight && !wasRight) CancelOutsideNightWork(Side.Right);
            RestoreCancelledJobs();
        }

        /// <summary>
        /// 把之前为避险取消的墙外工作还回去：该侧威胁解除（或天亮了）时重新给原工人排上。
        /// 游戏在夜间不会主动分配新工作，若不还回去，工人会一直闲置到第二天才被重新派活。
        /// </summary>
        private static void RestoreCancelledJobs()
        {
            if (_cancelledJobs.Count == 0) return;
            Managers managers = SingletonMonoBehaviour<Managers>.Inst;
            Kingdom kingdom = managers != null ? managers.kingdom : null;
            if (kingdom == null) return;

            for (int i = _cancelledJobs.Count - 1; i >= 0; i--)
            {
                CancelledJob c = _cancelledJobs[i];
                if (IsSideUnderAttack(c.Side)) continue;   // 该侧仍有威胁，保持取消

                _cancelledJobs.RemoveAt(i);
                try
                {
                    if (c.Worker == null || c.Job == null) continue;
                    if (!TryGetX(c.Job, out float x)) continue;
                    bool within;
                    try { within = kingdom.IsWithinWalls(x); } catch { within = true; }
                    if (within) continue;   // 工作已经被改到墙内或失效，不再恢复
                    if (c.Worker._queuedWork != null || c.Worker._currentWork != null) continue;   // 工人已有活干
                    if (!c.Job.JobAvailable()) continue;   // 工作位已被别人占了或已完成
                    c.Worker.AssignJob(c.Job);
                }
                catch { }
            }
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

        /// <summary>怪物是否处于进攻状态：存活、未决定撤退、未携带战利品离开。</summary>
        private static bool IsAssaultingEnemy(Enemy e)
        {
            try
            {
                if (e == null || e.IsDespawning) return false;
                if (e.shouldRetreat) return false;   // 已决定撤回传送门
                Damageable d = e.damageable;
                if (d != null && d.isDead) return false;
                return e.loot == null;   // 捡到金币/战利品逃走的怪物不再算进攻
            }
            catch { }
            return false;
        }

        /// <summary>夜晚该侧城墙外是否有进攻中的怪物（供 AssignJob 拦截新工作）。</summary>
        public static bool IsSideUnderAttack(Side side)
        {
            if (side == Side.Left) return _attackOnLeft;
            if (side == Side.Right) return _attackOnRight;
            return false;
        }

        /// <summary>撤销该侧工人已排队但尚未开始的墙外工作（墙内工作与守城岗位不动）。</summary>
        private static void CancelOutsideNightWork(Side side)
        {
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return;
                var workers = kingdom.Workers;
                if (workers == null || workers.Count <= 0) return;

                if (_assaultWorkerBuffer == null || _assaultWorkerBuffer.Length < workers.Count)
                    _assaultWorkerBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Worker>(Math.Max(workers.Count, 16));
                workers.CopyTo(_assaultWorkerBuffer, 0);

                int count = workers.Count;
                for (int i = 0; i < count; i++)
                {
                    Worker w = _assaultWorkerBuffer[i];
                    if (w == null) continue;
                    Side targetSide;
                    try { targetSide = w._targetSide; } catch { continue; }
                    if (targetSide != side) continue;
                    Workable queued;
                    try { queued = w._queuedWork; } catch { continue; }
                    if (queued == null) continue;
                    if (!TryGetX(queued, out float x)) continue;
                    bool within;
                    try { within = kingdom.IsWithinWalls(x); } catch { within = true; }
                    if (within) continue;
                    // 必须走游戏自己的取消流程：CancelWork 会释放该工作位的占用并回调 OnJobCancel。
                    // 直接置空 _queuedWork 会留下没人释放的工作位，之后同类工作不再被分配，
                    // 表现为"本侧没怪了也只剩新任务能触发工人出门"
                    try { queued.CancelWork(w); } catch { }
                    try { w._queuedWork = null; } catch { }
                    // 记下来，威胁解除后还给原工人（游戏夜间不会再分配工作）
                    if (_cancelledJobs.Count < 64) _cancelledJobs.Add(new CancelledJob { Worker = w, Job = queued, Side = side });
                }
            }
            catch { }
        }

        /// <summary>
        /// 取洞窟所在侧：洞窟本身是 Cliff 类型的 Portal，优先遍历 AllPortals 取坐标；
        /// 找不到时再低频兜底搜索场景中的 BombablePortal（洞窟入口组件）。
        /// </summary>
        private static bool TryGetCaveSide(Kingdom kingdom, out Side side)
        {
            side = Side.Left;
            try
            {
                foreach (Portal portal in kingdom.AllPortals)
                {
                    if (portal == null) continue;
                    Portal.Type type;
                    try { type = portal.type; } catch { continue; }
                    if (type != Portal.Type.Cliff) continue;
                    if (TryGetX(portal, out float x))
                    {
                        side = SideFromX(kingdom, x);
                        return true;
                    }
                }
            }
            catch { }

            // 兜底：全场景搜索开销大，最多每 10 秒执行一次
            float now = Time.unscaledTime;
            if (now < _nextCaveScan) return false;
            _nextCaveScan = now + 10f;
            try
            {
                var caves = UnityEngine.Object.FindObjectsByType<BombablePortal>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (caves == null || caves.Length == 0) return false;
                for (int i = 0; i < caves.Length; i++)
                {
                    BombablePortal cave = caves[i];
                    if (cave == null) continue;
                    if (TryGetX(cave, out float x))
                    {
                        side = SideFromX(kingdom, x);
                        return true;
                    }
                    try { side = cave.side; return true; } catch { }
                }
            }
            catch { }
            return false;
        }

        /// <summary>按露营火堆位置判断世界坐标位于地图哪一侧。</summary>
        private static Side SideFromX(Kingdom kingdom, float x)
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
    /// 此处若目标是船且单位为工人/弓箭手/骑士（摇铃只召唤这三类），直接 Embark 传送登船，省去跑动。
    /// 玩家无这三类组件，天然不受影响；随从弓箭手由骑士上船时指派，同样被本补丁传送；
    /// 读档恢复乘船单位时也走同一指派路径，重复 Embark 无副作用。开关关闭时补丁直接放行。
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
                __instance.Embark();
            }
            catch { }
        }
    }

    /// <summary>游戏重新分配工人后立即检查一次，保证传送门一侧的工人上限及时生效。</summary>
    [HarmonyPatch(typeof(Kingdom), nameof(Kingdom.DistributeWorkers))]
    public static class PortalWorkerDistributionPatch
    {
        public static void Postfix()
        {
            FeatureBehaviour.RequestPortalWorkerCheck();
        }
    }

    /// <summary>传送门被摧毁时立即标记检查：只响应非洞窟的传送门。</summary>
    [HarmonyPatch(typeof(Portal), nameof(Portal.SetDestroyed))]
    public static class PortalDestroyedPatch
    {
        public static void Postfix(Portal __instance)
        {
            if (__instance == null) return;
            try
            {
                if (__instance.type == Portal.Type.Cliff) return;   // 洞窟不参与该规则
            }
            catch { return; }
            FeatureBehaviour.RequestPortalWorkerCheck();
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
    /// 夜晚该侧城墙外有进攻怪物时，拒绝接受新的墙外工作（墙内工作与守城岗位照常）。
    /// 工人速度不及怪物，被派去远处墙外工作会被追上攻击。
    /// </summary>
    [HarmonyPatch(typeof(Worker), nameof(Worker.AssignJob))]
    public static class WorkerNightOutsideWorkPatch
    {
        public static bool Prefix(Worker __instance, Workable job)
        {
            try
            {
                if (__instance == null || job == null) return true;
                if (!FeatureBehaviour.IsSideUnderAttack(__instance._targetSide)) return true;

                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return true;

                float x = job.transform.position.x;
                return kingdom.IsWithinWalls(x);   // 墙内工作允许，墙外工作拒绝
            }
            catch { }
            return true;
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
