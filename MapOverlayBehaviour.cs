using System;
using System.Collections.Generic;
using Coatsink.Common;
using UnityEngine;

namespace BetterKingdom
{
    /// <summary>
    /// 屏幕顶部小地图 + 游戏信息面板。
    /// 黄色线表示整张地图，色点标注玩家/城堡/传送门/洞窟/城墙位/箭塔位。
    /// 紫色竖线实时标注怪物波次位置：按 waveID 分组，每批怪物只绘制距离最近的怪物位置。
    /// 左上显示金币（钱袋/国库）与职业统计（含骑士小队弓箭手/高级骑士细分、左右侧火油桶统计），左下显示时间/当前季节天数。
    /// 另含看门狗：每轮重刷新修正位于墙体内部的弓箭手守卫槽。
    /// </summary>
    public class MapOverlayBehaviour : MonoBehaviour
    {
        // 注：构建链为 .NET SDK 6（最高 C# 10），请勿使用主构造函数/集合表达式等 C# 12 语法
        public MapOverlayBehaviour(IntPtr ptr) : base(ptr) { }

        //   外观常量  
        private const float FrameHeightRatio = 0.06f;
        private const float LineThickness = 6f;
        private const float LeftMargin = 16f;
        private const float RightMargin = 260f;
        private const float LightRefreshInterval = 0.02f;
        private const float MediumRefreshInterval = 0.25f;
        private const float HeavyRefreshInterval = 2f;
        private const float DotRadius = 7f;
        private const int LabelFontSize = 18;
        private const int InfoFontSize = 20;
        private const int JobFontSize = 20;

        //   纹理/样式  
        private Texture2D _texYellow, _texPlayer, _texPortal, _texWall, _texTower, _texEnemy, _texCastle;
        private GUIStyle _styleYellow, _stylePlayer, _stylePortal, _styleWall, _styleTower, _styleEnemy, _styleCastle;
        private GUIStyle _styleLabel, _styleInfo, _styleJob;

        //   缓存坐标  
        private readonly List<float> _portalX = new();
        private readonly List<float> _caveX = new();
        private readonly List<float> _wallX = new();

        /// <summary>地图点标记（含坐标与状态标签，支持施工中/建造位等动态文本）。</summary>
        private struct MapMarker { public float X; public string Label; }
        private readonly List<MapMarker> _wallMarkers = new();
        private readonly List<MapMarker> _towerMarkers = new();

        //   功能开关控制的额外标记（难民营/雕像/坐骑出生点/宝箱/科技解锁点，仅对应开关开启时收集）  
        private readonly List<MapMarker> _campMarkers = new();
        private readonly List<MapMarker> _statueMarkers = new();
        private readonly List<MapMarker> _mountMarkers = new();
        private readonly List<MapMarker> _chestMarkers = new();
        private readonly List<MapMarker> _techMarkers = new();
        private Texture2D _texCamp, _texStatue, _texMount, _texChest, _texTech;
        private GUIStyle _styleCamp, _styleStatue, _styleMount, _styleChest, _styleTech;

        // 世界空间悬浮条（怪物血条/作物生长进度）
        private Texture2D _texBarBg, _texHpFill, _texCropFill;
        private GUIStyle _styleBarBg, _styleHpFill, _styleCropFill;
        private struct WorldBar { public Vector3 Pos; public float Frac; }
        // 作物进度条动画状态：显示值按估算速率连续匀速推进，与整数进度点的到达节奏解耦
        private struct CropAnim { public float Shown; public float Rate; public int LastCur; public float LastTime; }
        private readonly List<WorldBar> _hpBars = new();       // 怪物血条（轻刷新重建）
        private readonly List<WorldBar> _cropBars = new();     // 作物进度条（轻刷新重建）
        private readonly List<Farmland> _farmlands = new();    // 农田引用缓存（中频重建，低频读进度）
        private readonly Dictionary<Farmland, CropAnim> _cropSmooth = new();  // 每块农田的进度条动画状态（键随引用缓存一并清理）
        private float _lastCropTick;     // 上次作物进度轻刷新的 Time.time（推进间隔基准，替代与帧率挂钩的 deltaTime）
        private Camera _cachedCam;       // 世界转屏幕用的相机缓存（中频重建；Camera.main 在本游戏为 null，需遍历兜底）
        // 世界悬浮条尺寸与头顶偏移（屏幕像素/世界单位）
        private const float HpBarWidth = 80f, HpBarHeight = 10f;      // 怪物血条
        private const float CropBarWidth = 240f, CropBarHeight = 12f; // 作物进度条
        private const float HpBarWorldOffsetY = 1.2f;                // 血条距怪物脚部的世界高度
        private const float CropBarWorldOffsetY = 2f;              // 进度条距农田的世界高度（略高于城墙）
        private float _playerX;
        private bool _hasPlayer;
        //   城堡位置  
        private float _castleX;
        private bool _hasCastle;
        //   怪物波次竖线（每批怪物一条，存代表怪的世界X坐标）  
        private readonly List<float> _enemyLineX = new();
        private float _minX, _maxX;
        private bool _hasData;
        private float _nextLightRefresh, _nextMediumRefresh, _nextHeavyRefresh;

        //   每帧缓存的可见性状态（供 OnGUI 复用，避免每个 GUI 事件重复 IL2CPP 互操作查询）  
        private bool _overlayVisible;
        private bool _isMenuOpen;
        private static bool _wantCursor;   // 是否请求光标常显（游玩态游戏会隐藏系统光标）

        /// <summary>MOD 是否请求光标常显，供 CursorSystem 的补丁读取。</summary>
        internal static bool CursorWanted => _wantCursor;

        //   面板缓存字符串（仅中频刷新时重建，消除每次 Repaint 的字符串格式化分配）  
        private string _strTime, _strCoins, _strSeason;
        private readonly List<string> _jobLines = new();

        //   怪物扫描复用缓冲（消除 50Hz 下的数组/字典分配）  
        private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Enemy> _enemyBuffer;
        private readonly List<WaveGroup> _waveGroups = new();

        /// <summary>一波怪物的聚合数据（对象池复用，避免频繁分配）。</summary>
        private sealed class WaveGroup
        {
            public uint Id;
            public float Min, Max, Sum;
            public int Count;
            public void Reset(uint id, float x) { Id = id; Min = x; Max = x; Sum = x; Count = 1; }
            public void Add(float x)
            {
                if (x < Min) Min = x;
                if (x > Max) Max = x;
                Sum += x;
                Count++;
            }
        }

        //   城墙边界缓存（供弓箭手守墙bug修正使用）  
        private struct WallBound { public float X; public float HalfW; }
        private readonly List<WallBound> _wallBounds = new();

        //   游戏时间  
        private float _gameTime = -1f;
        private Player _cachedPlayer;
        private Player _cachedPlayer2;     // 分屏玩家2缓存（轻刷新重建；单人游戏为 null）
        private Castle _cachedCastle;      // 城堡缓存（中频重建），钱袋国库联动读写国库用
        private Banker _cachedBanker;      // 银行家（税官）缓存（中频重建），国库真实账本 Banker._stashedCoins 所在
        //   钱袋国库联动
        private const int PurseMaxCoins = 38;        // 钱袋容量上限（钻石占 3 金币位，有效金币上限 = 39 - 3×钻石数）
        private const float PayHoldThreshold = 0.4f; // 长按判定阈值（秒），短点击不触发取款
        private float _payHoldStartP1 = -1f, _payHoldStartP2 = -1f;   // 各玩家支付键按住计时起点（-1=未计时）

        //   季节  
        private Season _season;           // 当前季节
        private int _seasonDay;           // 季节内第几天（1-16）
        private bool _hasSeason;

        //   职业统计  
        private int _countArchers, _countKnights, _countPikemen, _countFarmers, _countWorkers, _countBerserkers;
        private int _countJobless;
        // 细分：骑士小队中的弓箭手（isKnightSoldier）、解锁铁科技后用铁剑武装晋升的高级骑士（_wasPromoted）
        private int _countSquadArchers, _countKnightsAdvanced;

        //   金币  
        private int _purseCoins, _treasuryCoins;
        private bool _hasCoins;

        //   火油桶统计（投石车已装填队列 + 运送中未送达的桶，按左右侧分别计数）  
        private int _oilLeft, _oilRight;

        //   悬停动画  
        private const float HoverAnimSpeed = 10f;
        private const float HoverExtraRadius = 10f;
        private const float HoverGrowScale = 0.5f;
        // 键为 (label哈希 << 32 | 坐标位)，避免每次调用拼接字符串产生 GC
        private readonly Dictionary<long, float> _hoverAnim = new();

        private void Awake()
        {
            _texYellow = MakeSolidTexture(new Color(1f, 0.9f, 0.15f, 1f));
            _texPlayer = MakeSolidTexture(new Color(0.2f, 1f, 1f, 1f));
            _texPortal = MakeSolidTexture(new Color(1f, 0.25f, 0.2f, 1f));
            _texWall = MakeSolidTexture(new Color(0.35f, 0.6f, 1f, 1f));
            _texTower = MakeSolidTexture(new Color(0.4f, 1f, 0.4f, 1f));
            _texEnemy = MakeSolidTexture(new Color(0.95f, 0.3f, 1f, 1f));
            _texCastle = MakeSolidTexture(new Color(1f, 1f, 1f, 1f));
            _texCamp = MakeSolidTexture(new Color(0.95f, 0.55f, 0.15f, 1f));   // 难民营：橙
            _texStatue = MakeSolidTexture(new Color(1f, 0.85f, 0.25f, 1f));   // 雕像：金
            _texMount = MakeSolidTexture(new Color(1f, 0.45f, 0.65f, 1f));    // 坐骑出生点：粉
            _texChest = MakeSolidTexture(new Color(1f, 0.78f, 0.1f, 1f));    // 宝箱：金
            _texTech = MakeSolidTexture(new Color(0.6f, 0.6f, 0.62f, 1f));   // 科技解锁点：灰
            _texBarBg = MakeSolidTexture(new Color(0f, 0f, 0f, 0.65f));       // 悬浮条底：半透明黑
            _texHpFill = MakeSolidTexture(new Color(0.85f, 0.15f, 0.2f, 1f)); // 怪物血条：红
            _texCropFill = MakeSolidTexture(new Color(0.35f, 0.85f, 0.3f, 1f)); // 作物进度：绿

            _styleYellow = MakeSolidStyle(_texYellow);
            _stylePlayer = MakeSolidStyle(_texPlayer);
            _stylePortal = MakeSolidStyle(_texPortal);
            _styleWall = MakeSolidStyle(_texWall);
            _styleTower = MakeSolidStyle(_texTower);
            _styleEnemy = MakeSolidStyle(_texEnemy);
            _styleCastle = MakeSolidStyle(_texCastle);
            _styleCamp = MakeSolidStyle(_texCamp);
            _styleStatue = MakeSolidStyle(_texStatue);
            _styleMount = MakeSolidStyle(_texMount);
            _styleChest = MakeSolidStyle(_texChest);
            _styleTech = MakeSolidStyle(_texTech);
            _styleBarBg = MakeSolidStyle(_texBarBg);
            _styleHpFill = MakeSolidStyle(_texHpFill);
            _styleCropFill = MakeSolidStyle(_texCropFill);
            Instance = this;

            _styleLabel = new GUIStyle { normal = { textColor = Color.white }, fontSize = LabelFontSize, alignment = TextAnchor.UpperCenter };
            _styleInfo = new GUIStyle { normal = { textColor = Color.white }, fontSize = InfoFontSize, alignment = TextAnchor.UpperLeft };
            _styleJob = new GUIStyle { normal = { textColor = Color.white }, fontSize = JobFontSize, alignment = TextAnchor.UpperLeft };
        }

        private static Texture2D MakeSolidTexture(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        private static GUIStyle MakeSolidStyle(Texture2D tex) => new() { normal = { background = tex } };

        private void Update()
        {
            // 可见性每帧只查询一次并缓存，供 Update/OnGUI 共用（OnGUI 每帧触发多个事件，重复查询开销大）
            _overlayVisible = ShouldShowOverlay();
            _wantCursor = false;
            if (!_overlayVisible) return;
            _isMenuOpen = IsMenuOpen();

            // 游玩态游戏会隐藏系统光标，而本 MOD 的覆盖层需要光标常显：这里只声明需求，
            // 真正的补写放在游戏 CursorSystem.Update 之后（见 Plugin.cs 的 CursorSystemUpdatePatch），
            // 使同一帧内 MOD 是最后写入者，避免与游戏互相覆盖同一个状态导致光标频闪
            _wantCursor = !_isMenuOpen || ModWindowBehaviour.IsOpen;

            float now = Time.unscaledTime;

            // 钱袋国库联动（每帧：溢出即时存入，长按取款需帧级按键追踪）
            UpdatePurseTreasuryLink(now);

            if (now >= _nextLightRefresh)
            {
                _nextLightRefresh = now + LightRefreshInterval;
                RefreshLightData();
            }
            if (now >= _nextMediumRefresh)
            {
                _nextMediumRefresh = now + MediumRefreshInterval;
                RefreshMediumData();
            }
            if (now >= _nextHeavyRefresh)
            {
                _nextHeavyRefresh = now + HeavyRefreshInterval;
                RefreshHeavyData();
            }
        }

        private void OnDestroy()
        {
            // 清理纹理资源
            DestroyImmediate(_texYellow);
            DestroyImmediate(_texPlayer);
            DestroyImmediate(_texPortal);
            DestroyImmediate(_texWall);
            DestroyImmediate(_texTower);
            DestroyImmediate(_texEnemy);
            DestroyImmediate(_texCastle);
            DestroyImmediate(_texCamp);
            DestroyImmediate(_texStatue);
            DestroyImmediate(_texMount);
            DestroyImmediate(_texChest);
            DestroyImmediate(_texTech);
            DestroyImmediate(_texBarBg);
            DestroyImmediate(_texHpFill);
            DestroyImmediate(_texCropFill);
        }

        /// <summary>当前实例（供模组窗口等功能开关变化时通知刷新）。</summary>
        public static MapOverlayBehaviour Instance { get; private set; }

        /// <summary>功能开关变化时调用：立即安排一次重刷新，使开关对地图标记即时生效。</summary>
        public static void RequestHeavyRefresh()
        {
            if (Instance != null) Instance._nextHeavyRefresh = 0f;
        }

        /// <summary>轻刷新（50Hz）：只更新需要实时跟随的数据——玩家位置与怪物竖线。</summary>
        private void RefreshLightData()
        {
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                if (managers == null) return;
                Kingdom kingdom = managers.kingdom;
                if (kingdom == null) return;

                _hasPlayer = false;
                try
                {
                    Player player = kingdom.playerOne ?? kingdom.GetPlayer(0);
                    if (TryGetX(player, out float px))
                    {
                        _playerX = px;
                        _hasPlayer = true;
                    }
                    _cachedPlayer = player;
                    // 分屏玩家2（单人游戏 GetPlayer(1) 返回空或玩家1本身，后者需剔除）
                    try
                    {
                        _cachedPlayer2 = kingdom.GetPlayer(1);
                        if (_cachedPlayer2 != null && player != null && _cachedPlayer2.playerId == player.playerId)
                            _cachedPlayer2 = null;
                    }
                    catch { _cachedPlayer2 = null; }
                }
                catch { }

                // 怪物波次竖线位置（实时刷新）
                RefreshEnemyLines(managers, kingdom);

                // 作物生长进度（引用中频缓存，这里只读进度值）
                RefreshCropBars();
            }
            catch { }
        }

        /// <summary>
        /// 钱袋国库联动（每帧调用）：钱袋有效金币上限 = 38 - 2×钻石数（钻石占用钱袋容量），超出部分自动存入国库
        /// （金币逐枚进袋，每帧检查不会漏）；钱袋为空且长按支付键（而非点击）达到阈值时，从国库取出填满有效上限
        /// （国库不足则全部取出）。
        /// </summary>
        private void UpdatePurseTreasuryLink(float now)
        {
            if (!ModWindowBehaviour.LinkPurseTreasury) return;
            Castle castle = _cachedCastle;
            if (castle == null) return;   // 尚无城堡（国库）时不做任何转移
            Banker banker = _cachedBanker;
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
            // 钻石占用钱袋容量（1 颗钻石 = 2 金币位），有效金币上限 = 38 - 2×钻石数，下限 0
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
            try { return ModWindowBehaviour.IsPayKeyHeld(playerIndex); } catch { return false; }
        }

        /// <summary>
        /// 中频刷新（4Hz）：时间/季节/金币/职业统计等数值信息。
        /// 这些数值变化慢，4Hz 更新肉眼无感知，集中在此避免 50Hz 的 IL2CPP 互操作开销。
        /// 显示字符串也在此重建，OnGUI 每次 Repaint 只做纯绘制。
        /// </summary>
        private void RefreshMediumData()
        {
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                if (managers == null) return;
                Kingdom kingdom = managers.kingdom;
                if (kingdom == null) return;

                Player player = null;
                try { player = kingdom.playerOne ?? kingdom.GetPlayer(0); } catch { }

                // 农田引用缓存（作物生长进度条用：中频重建引用，轻频读进度）
                RefreshFarmlandRefs();

                // 相机缓存（世界转屏幕用）：本游戏 Camera.main 为 null，需遍历所有相机找启用的
                RefreshCameraRef();

                // 时间
                try
                {
                    Director director = managers.director;
                    if (director != null)
                    {
                        _gameTime = director.currentTime;
                        try
                        {
                            TimesOfDay tod = director.CurrentTimesOfDay;
                        }
                        catch { }

                        // 季节与季节内天数（每季 16 天，由统治累计天数推算，换岛不重置）
                        try
                        {
                            int day = SeasonClock.GetDayNumber(director);
                            _season = SeasonClock.GetSeason(director, day);
                            _seasonDay = SeasonClock.GetSeasonDay(director, day);
                            _hasSeason = true;
                        }
                        catch { _hasSeason = false; }
                    }
                }
                catch { }

                // 金币（三路径读取确保可靠性）
                _hasCoins = false;
                int purse = 0, treasury = 0;
                try
                {
                    if (player != null)
                    {
                        purse = player.coins;
                        _hasCoins = true;
                    }

                    // 路径1：Castle._currentCoinCount（城堡 UI 显示的金币堆数量）
                    int castleCoins = 0;
                    Castle castle = kingdom.castle;
                    _cachedCastle = castle;
                    if (castle != null)
                    {
                        try { castleCoins = castle._currentCoinCount; } catch { }
                    }

                    // 路径2：Banker._stashedCoins（Banker 藏匿的金币）
                    int bankerCoins = 0;
                    // 缓存优先复用：FindObjectsByType(含隐藏对象) 是全场景遍历，每 0.25s 执行会造成周期性卡顿，
                    // 只有缓存失效（税官被销毁）或从未找到时才重新查找
                    Banker banker = _cachedBanker;
                    if (banker != null)
                    {
                        try { if (banker.gameObject == null) banker = null; } catch { banker = null; }
                    }
                    if (banker == null)
                    {
                        banker = kingdom.banker;
                        // kingdom.banker 常为空（税官藏在城堡里时引用不维护），用场景查找兜底（含隐藏对象）
                        if (banker == null)
                        {
                            try
                            {
                                var bankers = UnityEngine.Object.FindObjectsByType<Banker>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                                if (bankers.Length > 0) banker = bankers[0];
                            }
                            catch { }
                        }
                        _cachedBanker = banker;
                    }
                    if (banker != null)
                    {
                        try { bankerCoins = banker._stashedCoins; } catch { }
                    }

                    // 取两者中较大的值作为国库金币
                    treasury = Math.Max(castleCoins, bankerCoins);
                }
                catch { }

                _purseCoins = purse;
                _treasuryCoins = treasury;

                // 职业
                try
                {
                    _countArchers = kingdom.ArcherCount;
                    _countKnights = kingdom.KnightCount;
                    _countPikemen = kingdom.Pikemen?.Count ?? 0;
                    _countFarmers = kingdom.Farmers?.Count ?? 0;
                    _countWorkers = kingdom.Workers?.Count ?? 0;
                    _countBerserkers = kingdom.Berserkers?.Count ?? 0;

                    int totalChars = 0;
                    try { totalChars = kingdom._characters?.Count ?? 0; } catch { }
                    int totalJobs = _countArchers + _countKnights + _countPikemen
                        + _countFarmers + _countWorkers + _countBerserkers;
                    int beggars = 0;
                    try { beggars = kingdom.Beggars?.Count ?? 0; } catch { }
                    _countJobless = Math.Max(0, totalChars - beggars - totalJobs);
                }
                catch { }

                // 细分：骑士小队中的弓箭手（isKnightSoldier=true 表示已被骑士征入小队）
                _countSquadArchers = 0;
                try
                {
                    Il2CppSystem.Collections.Generic.ICollection<Archer> archers = kingdom.Archers;
                    if (archers != null && archers.Count > 0)
                    {
                        int n = archers.Count;
                        var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Archer>(n);
                        archers.CopyTo(arr, 0);
                        for (int i = 0; i < n; i++)
                        {
                            Archer a = arr[i];
                            if (a == null) continue;
                            try { if (a.isKnightSoldier) _countSquadArchers++; } catch { }
                        }
                    }
                }
                catch { }

                // 细分：高级骑士（_wasPromoted：解锁铁科技后被铁剑武装晋升的骑士；rank 字段恒为正，不能用于区分）
                _countKnightsAdvanced = 0;
                try
                {
                    Il2CppSystem.Collections.Generic.ICollection<Knight> knights = kingdom.Knights;
                    if (knights != null && knights.Count > 0)
                    {
                        int n = knights.Count;
                        var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Knight>(n);
                        knights.CopyTo(arr, 0);
                        for (int i = 0; i < n; i++)
                        {
                            Knight k = arr[i];
                            if (k == null) continue;
                            try { if (k._wasPromoted) _countKnightsAdvanced++; } catch { }
                        }
                    }
                }
                catch { }

                RebuildDisplayStrings();
            }
            catch { }
        }

        /// <summary>重建面板显示字符串与职业统计行，仅在中频刷新时执行。</summary>
        private void RebuildDisplayStrings()
        {
            _strTime = _gameTime >= 0f ? $"时间: {GameTimeTo24H(_gameTime)}" : null;
            _strCoins = _hasCoins ? $"钱袋: {_purseCoins}  国库: {_treasuryCoins}" : null;
            if (_hasSeason)
                _strSeason = $"{SeasonName(_season)}：第{_seasonDay}天";
            else
                _strSeason = null;

            _jobLines.Clear();
            // 弓箭手总数中拆出骑士小队中的弓箭手，分两行显示
            AddJobLine("弓箭手", Math.Max(0, _countArchers - _countSquadArchers));
            AddJobLine("小队弓箭手", _countSquadArchers);
            // 骑士总数中拆出用剑升级过的高级骑士，分两行显示
            AddJobLine("骑士", Math.Max(0, _countKnights - _countKnightsAdvanced));
            AddJobLine("高级骑士", _countKnightsAdvanced);
            AddJobLine("长矛兵", _countPikemen);
            AddJobLine("农夫", _countFarmers);
            AddJobLine("工人", _countWorkers);
            AddJobLine("狂战士", _countBerserkers);
            AddJobLine("平民", _countJobless);
            // 火油桶统计（仅存在桶时显示，避免无投石车时常驻空行）
            if (_oilLeft > 0 || _oilRight > 0)
                _jobLines.Add($"火油桶 左:{_oilLeft} 右:{_oilRight}");
        }

        private void AddJobLine(string name, int count)
        {
            if (count <= 0) return;
            _jobLines.Add($"{name}: {count}");
        }

        /// <summary>
        /// 扫描全部敌人，按 waveID 分组（一批怪物一组），每组只保留一条竖线：
        /// 以城堡位置为左右分界，只取最近的怪物位置。
        /// </summary>
        private void RefreshEnemyLines(Managers managers, Kingdom kingdom)
        {
            _enemyLineX.Clear();
            _hpBars.Clear();
            try
            {
                EnemyManager em = managers.enemies;
                if (em == null) return;
                Il2CppSystem.Collections.Generic.ICollection<Enemy> all = em.AllEnemies;
                if (all == null) return;
                int count = all.Count;
                if (count <= 0) return;

                bool collectHp = ModWindowBehaviour.ShowGreedHealth;

                // 左右分界：优先城堡位置，失败则用地图中心
                if (!TryGetX(kingdom.castle, out float divider))
                    divider = (_minX + _maxX) * 0.5f;

                // Il2Cpp 集合通过 CopyTo 到 Il2CppReferenceArray 后按下标访问；缓冲区复用，不够才扩容
                if (_enemyBuffer == null || _enemyBuffer.Length < count)
                    _enemyBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Enemy>(Math.Max(count, 16));
                all.CopyTo(_enemyBuffer, 0);

                // 按 waveID 分组，组对象复用（波次很少，线性查找即可，避免字典分配）
                int groupCount = 0;
                for (int i = 0; i < count; i++)
                {
                    Enemy e = _enemyBuffer[i];
                    if (e == null) continue;
                    try { if (e.IsDespawning) continue; } catch { }
                    if (!TryGetX(e, out float x)) continue;

                    // 怪物血条：优先 hitPoints/initialHitPoints 算比例，悬浮在头顶；仅受伤（血量不满）时显示
                    if (collectHp)
                    {
                        try
                        {
                            Damageable d = e.damageable;
                            if (d == null) { try { d = e.Damageable; } catch { } }
                            if (d != null)
                            {
                                bool dead = false;
                                try { dead = d.isDead; } catch { }
                                if (!dead)
                                {
                                    int hp = -1, init = -1;
                                    try { hp = d.hitPoints; } catch { }
                                    try { init = d.initialHitPoints; } catch { }
                                    float frac = -1f;
                                    if (init > 0 && hp > 0 && hp < init)
                                        frac = (float)hp / init;
                                    else
                                    {
                                        // 部分怪物无整数血量字段（initialHitPoints 为 0），回退用百分比属性
                                        try
                                        {
                                            float p = d.HealthPercentage;
                                            if (p > 0f && p < 1f) frac = p;
                                        }
                                        catch { }
                                    }
                                    if (frac >= 0f)
                                        _hpBars.Add(new WorldBar { Pos = e.transform.position + new Vector3(0f, HpBarWorldOffsetY, 0f), Frac = frac });
                                }
                            }
                        }
                        catch { }
                    }
                    uint id;
                    try { id = e.waveID; } catch { id = 0u; }

                    WaveGroup g = null;
                    for (int j = 0; j < groupCount; j++)
                    {
                        if (_waveGroups[j].Id == id) { g = _waveGroups[j]; break; }
                    }
                    if (g == null)
                    {
                        if (groupCount < _waveGroups.Count)
                        {
                            g = _waveGroups[groupCount];
                            g.Reset(id, x);
                        }
                        else
                        {
                            g = new WaveGroup();
                            g.Reset(id, x);
                            _waveGroups.Add(g);
                        }
                        groupCount++;
                    }
                    else
                    {
                        g.Add(x);
                    }
                }

                for (int j = 0; j < groupCount; j++)
                {
                    WaveGroup g = _waveGroups[j];
                    float avg = g.Sum / g.Count;
                    // 右侧出怪显示最左侧怪的位置，左侧出怪显示最右侧怪的位置
                    _enemyLineX.Add(avg >= divider ? g.Min : g.Max);
                }
            }
            catch { }
        }

        private void RefreshHeavyData()
        {
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                if (managers == null) { _hasData = false; return; }
                Kingdom kingdom = managers.kingdom;
                if (kingdom == null) { _hasData = false; return; }

                // 城堡位置（基本静态，重刷新时更新即可）
                _hasCastle = TryGetX(kingdom.castle, out _castleX);

                // 火油桶统计（按左右侧）
                RefreshOilBarrels();

                _portalX.Clear();
                _caveX.Clear();
                _wallX.Clear();
                _hoverAnim.Clear();

                float minX = float.MaxValue;
                float maxX = float.MinValue;

                World world = managers.world;
                bool boundsValid = false;
                if (world != null)
                {
                    try
                    {
                        Sided<float> bounds = world.worldBounds;
                        if (bounds != null && bounds.right - bounds.left > 10f)
                        {
                            minX = bounds.left;
                            maxX = bounds.right;
                            boundsValid = true;
                        }
                    }
                    catch { }
                }

                if (!boundsValid)
                {
                    try
                    {
                        Player player = kingdom.playerOne ?? kingdom.GetPlayer(0);
                        if (TryGetX(player, out float px))
                        {
                            minX = Math.Min(minX, px);
                            maxX = Math.Max(maxX, px);
                        }
                    }
                    catch { }
                }

                try
                {
                    foreach (Portal portal in kingdom.AllPortals)
                    {
                        if (TryGetX(portal, out float x))
                        {
                            _portalX.Add(x);
                            if (!boundsValid) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); }
                        }
                    }
                }
                catch { }

                try
                {
                    var cliffs = UnityEngine.Object.FindObjectsByType<BombablePortal>(FindObjectsSortMode.None);
                    foreach (BombablePortal cliff in cliffs)
                    {
                        if (TryGetX(cliff, out float x))
                        {
                            _caveX.Add(x);
                            if (!boundsValid) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); }
                        }
                    }
                    _portalX.RemoveAll(px => CaveOverlaps(px, _caveX));
                }
                catch { }

                CollectWallAndTowerMarkers(kingdom, boundsValid, ref minX, ref maxX);
                CollectWallBounds(kingdom);

                // 功能开关控制的额外标记：仅开关开启时收集（关闭时清空不绘制）
                _campMarkers.Clear();
                _statueMarkers.Clear();
                _mountMarkers.Clear();
                _chestMarkers.Clear();
                _techMarkers.Clear();
                if (ModWindowBehaviour.ShowAllCamps) CollectCampMarkers(kingdom);
                if (ModWindowBehaviour.ShowStatues) CollectStatueMarkers();
                if (ModWindowBehaviour.ShowMountSpawns) CollectMountMarkers();
                if (ModWindowBehaviour.ShowChests) CollectChestMarkers();
                if (ModWindowBehaviour.ShowTechPoints) CollectTechMarkers();

                _hasData = minX < maxX;
                if (!_hasData) return;

                float pad = (boundsValid ? 0.005f : 0.02f) * (maxX - minX);
                _minX = minX - pad;
                _maxX = maxX + pad;

                // 看门狗：修正位于墙体内部的弓箭手守卫槽与弓箭手本体（游戏原生bug）
                FixWallDefensePositions(kingdom);
            }
            catch { }
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

        /// <summary>
        /// 游戏原生bug修正入口：弓箭手守墙待机位有时会位于墙体内部。每轮重刷新检测并修正。
        /// </summary>
        private void FixWallDefensePositions(Kingdom kingdom)
        {
            try
            {
                if (_wallBounds.Count == 0) return;
                // 需要城堡位置判断“墙后”是哪一侧
                if (!TryGetX(kingdom.castle, out float castleX)) return;

                FixGuardSlotsInsideWalls(kingdom, castleX);
                FixArchersInsideWalls(kingdom, castleX);
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
        /// 根因 ：游戏原生bug导致 _guardDepth 守卫深度漂移为负值且不再恢复，
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

        /// <summary>检查 worldX 是否与 caveX 列表中的任何坐标重合（距离<1）。</summary>
        private static bool CaveOverlaps(float worldX, List<float> caveX)
        {
            for (int i = 0; i < caveX.Count; i++)
                if (Math.Abs(caveX[i] - worldX) < 1f) return true;
            return false;
        }

        /// <summary>
        /// 统计两侧火油桶：投石车已装填队列（Catapult.queuedOilBarrels）
        /// + 运送中未送达的桶（RollableOilBarrel 未 delivered），按 Catapult.side/RollableOilBarrel.side 分侧。
        /// </summary>
        private void RefreshOilBarrels()
        {
            _oilLeft = 0;
            _oilRight = 0;
            try
            {
                // 投石车已装填的火油桶队列
                var catapults = UnityEngine.Object.FindObjectsByType<Catapult>(FindObjectsSortMode.None);
                foreach (Catapult cat in catapults)
                {
                    if (cat == null) continue;
                    try
                    {
                        int queued = cat.queuedOilBarrels;
                        if (queued <= 0) continue;
                        if (cat.side == Side.Left) _oilLeft += queued;
                        else if (cat.side == Side.Right) _oilRight += queued;
                    }
                    catch { }
                }

                // 运送中（工人正在滚动、尚未送达投石车）的火油桶
                var rollables = UnityEngine.Object.FindObjectsByType<RollableOilBarrel>(FindObjectsSortMode.None);
                foreach (RollableOilBarrel barrel in rollables)
                {
                    if (barrel == null) continue;
                    try
                    {
                        if (barrel._delivered) continue;
                        if (barrel.side == Side.Left) _oilLeft++;
                        else if (barrel.side == Side.Right) _oilRight++;
                    }
                    catch { }
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

        /// <summary>农田引用缓存重建（中频）：仅在开关开启时扫描，失败置空避免误用失效引用。</summary>
        private void RefreshFarmlandRefs()
        {
            _farmlands.Clear();
            if (!ModWindowBehaviour.ShowCropProgress) { _cropSmooth.Clear(); return; }
            try
            {
                var lands = UnityEngine.Object.FindObjectsByType<Farmland>(FindObjectsSortMode.None);
                if (lands == null) return;
                for (int i = 0; i < lands.Length; i++)
                    if (lands[i] != null) _farmlands.Add(lands[i]);

                // 剔除已失效农田的平滑状态（引用缓存重建后键可能已不存在）
                if (_cropSmooth.Count > 0)
                {
                    List<Farmland> stale = null;
                    foreach (var kv in _cropSmooth)
                        if (!_farmlands.Contains(kv.Key)) (stale ??= new List<Farmland>()).Add(kv.Key);
                    if (stale != null)
                        foreach (Farmland k in stale) _cropSmooth.Remove(k);
                }
            }
            catch { }
        }

        /// <summary>
        /// 相机引用缓存重建（中频）：本游戏的相机未打 "MainCamera" 标签，Camera.main 恒为 null，
        /// 优先取游戏自定义 MainCamera 组件上的相机，其次排除界面相机后取第一个启用的。
        /// </summary>
        private void RefreshCameraRef()
        {
            if (!ModWindowBehaviour.ShowCropProgress && !ModWindowBehaviour.ShowGreedHealth) { _cachedCam = null; return; }
            // 缓存复用：相机仍启用且激活时直接返回，避免每 0.25s 两次场景遍历（周期性卡顿来源）
            if (_cachedCam != null)
            {
                try { if (_cachedCam.enabled && _cachedCam.gameObject.activeInHierarchy) return; } catch { }
                _cachedCam = null;
            }
            try { _cachedCam = Camera.main; } catch { }
            if (_cachedCam != null) return;
            try
            {
                var cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
                if (cams == null || cams.Length == 0) return;

                // 优先：游戏自定义 MainCamera 组件持有的相机（世界相机）
                var mains = UnityEngine.Object.FindObjectsByType<MainCamera>(FindObjectsSortMode.None);
                if (mains != null)
                {
                    for (int i = 0; i < mains.Length; i++)
                    {
                        if (mains[i] == null) continue;
                        Camera c = null;
                        try { c = mains[i].GetComponent<Camera>(); } catch { }
                        if (c != null) { _cachedCam = c; return; }
                    }
                }
                // 其次：排除界面/UI 相机，取第一个启用且激活的
                for (int i = 0; i < cams.Length; i++)
                {
                    Camera c = cams[i];
                    if (c == null) continue;
                    string n = c.name;
                    if (n.IndexOf("interface", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("ui", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    try
                    {
                        if (c.enabled && c.gameObject.activeInHierarchy) { _cachedCam = c; return; }
                    }
                    catch { }
                }
                // 兜底：第一个非空相机
                for (int i = 0; i < cams.Length; i++)
                    if (cams[i] != null) { _cachedCam = cams[i]; return; }
            }
            catch { }
        }

        /// <summary>
        /// 作物生长进度（轻频）：遍历缓存的农田引用，进度 = currentFarmPoints / farmPoints；
        /// 已成熟（进度满）或无农夫耕作的田不显示，失效引用顺带剔除。
        /// </summary>
        private void RefreshCropBars()
        {
            _cropBars.Clear();
            if (!ModWindowBehaviour.ShowCropProgress) return;

            // 推进时长必须用轻刷新的真实间隔：本方法被限制在 50Hz 执行，
            // 若直接用 Time.deltaTime（帧时间），帧率高于 50fps 时推进会按比例变慢
            float now = Time.time;
            float tickDt = now - _lastCropTick;
            _lastCropTick = now;
            if (tickDt < 0f || tickDt > 0.1f) tickDt = 0f;   // 首次调用或开关重开后的异常间隔不计

            for (int i = _farmlands.Count - 1; i >= 0; i--)
            {
                Farmland f = _farmlands[i];
                try
                {
                    if (f == null) { _farmlands.RemoveAt(i); continue; }

                    int farmPoints = 0, cur = 0;
                    bool isFarmed = false;
                    try { farmPoints = f.farmPoints; } catch { }
                    try { cur = f.currentFarmPoints; } catch { }
                    try { isFarmed = f._isFarmed; } catch { }

                    if (farmPoints <= 0) continue;
                    float frac = (float)cur / farmPoints;
                    if (frac >= 1f) { _cropSmooth.Remove(f); continue; }   // 已成熟
                    // 没有农夫耕作过的田不显示（未耕作时进度点为 0 且未被标记耕作中）
                    if (cur <= 0 && !isFarmed) { _cropSmooth.Remove(f); continue; }

                    // 完全线性增长：显示值按实测速率连续匀速推进，不追随整数进度点的到达节奏。
                    // 速率 = 最近进度跳变量 / 跳变间隔时间，新跳变只更新速率、不改写显示值，
                    // 因此进度条斜率稳定、无停顿；进度回退（收获/清空）立即吸附。
                    CropAnim anim;
                    if (!_cropSmooth.TryGetValue(f, out anim))
                    {
                        anim = new CropAnim { Shown = frac, Rate = 0f, LastCur = cur, LastTime = now };
                    }
                    else if (cur != anim.LastCur)
                    {
                        if (cur < anim.LastCur)
                        {
                            // 收获/清空：立即吸附
                            anim.Shown = frac;
                            anim.Rate = 0f;
                        }
                        else
                        {
                            float dt = now - anim.LastTime;
                            if (dt > 0.01f)
                            {
                                float instRate = ((float)(cur - anim.LastCur) / farmPoints) / dt;
                                // 速率平滑：新观测占 35%，避免单次异常间隔导致斜率突变
                                anim.Rate = anim.Rate <= 0f ? instRate : Mathf.Lerp(anim.Rate, instRate, 0.35f);
                            }
                        }
                        anim.LastCur = cur;
                        anim.LastTime = now;
                    }
                    // 匀速推进（用轻刷新真实间隔，与帧率无关）；仅做 [0,1] 夹取，不设中间硬上限，
                    // 避免显示值顶到真实进度 frac 后产生停顿（分段线性）
                    if (anim.Rate > 0f)
                        anim.Shown = Mathf.Clamp(anim.Shown + anim.Rate * tickDt, 0f, 1f);
                    // 校准：保持与真实进度贴合。落后时快速追上（消除累计误差），
                    // 超前时极慢回吐（速率偏快时几乎无可见收缩）
                    if (frac > anim.Shown)
                        anim.Shown = Mathf.Lerp(anim.Shown, frac, tickDt * 0.9f);
                    else if (frac < anim.Shown)
                        anim.Shown = Mathf.Lerp(anim.Shown, frac, tickDt * 0.1f);
                    _cropSmooth[f] = anim;

                    _cropBars.Add(new WorldBar { Pos = f.transform.position + new Vector3(0f, CropBarWorldOffsetY, 0f), Frac = Mathf.Clamp01(anim.Shown) });
                }
                catch { _farmlands.RemoveAt(i); }
            }
        }

        /// <summary>
        /// 收集城墙与箭塔标记（含建造状态）：
        /// 真墙/真塔来自 kingdom.Walls/Towers，施工中由 WorkableBuilding.UnderConstruction 判定；
        /// 箭塔建造位为 level&lt;=0 的 Tower；城墙建造位来自 PayableManager 中 nextPrefab 含 Wall 的 PayableUpgrade。
        /// 同时填充 _wallX（供建造位与真墙去重）。
        /// </summary>
        private void CollectWallAndTowerMarkers(Kingdom kingdom, bool boundsValid, ref float minX, ref float maxX)
        {
            _wallMarkers.Clear();
            _towerMarkers.Clear();

            // 真墙（含施工中状态）
            try
            {
                Il2CppSystem.Collections.Generic.ICollection<Wall> walls = kingdom.Walls;
                if (walls != null && walls.Count > 0)
                {
                    int count = walls.Count;
                    var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Wall>(count);
                    walls.CopyTo(arr, 0);
                    for (int i = 0; i < count; i++)
                    {
                        Wall w = arr[i];
                        if (!TryGetX(w, out float x)) continue;
                        string label = "城墙";
                        try
                        {
                            WorkableBuilding wb = w.GetComponent<WorkableBuilding>();
                            if (wb != null && wb.UnderConstruction) label = "城墙（建造中）";
                            else label = StructureLabel("城墙", w.wallEraLevel, w.level);
                        }
                        catch { }
                        _wallMarkers.Add(new MapMarker { X = x, Label = label });
                        _wallX.Add(x);
                        if (!boundsValid) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); }
                    }
                }
            }
            catch { }

            // 箭塔（level<=0 为未建造的空塔基座 = 建造位；否则按施工状态区分）
            try
            {
                Il2CppSystem.Collections.Generic.ICollection<Tower> towers = kingdom.Towers;
                if (towers != null && towers.Count > 0)
                {
                    int count = towers.Count;
                    var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Tower>(count);
                    towers.CopyTo(arr, 0);
                    for (int i = 0; i < count; i++)
                    {
                        Tower t = arr[i];
                        if (!TryGetX(t, out float x)) continue;
                        string label = "箭塔";
                        try
                        {
                            if (t.level <= 0)
                            {
                                label = "箭塔建造位";
                            }
                            else
                            {
                                WorkableBuilding wb = t.GetComponent<WorkableBuilding>();
                                if (wb != null && wb.UnderConstruction) label = "箭塔（建造中）";
                                else label = StructureLabel("箭塔", TowerEra(t), t.level);
                            }
                        }
                        catch { }
                        _towerMarkers.Add(new MapMarker { X = x, Label = label });
                        if (!boundsValid) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); }
                    }
                }
            }
            catch { }

            // 城墙建造位：PayableManager 中可升级为 Wall 的支付点（与已有真墙去重）
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                PayableManager pm = managers != null ? managers.payables : null;
                var all = pm != null ? pm.AllPayables : null;
                if (all != null)
                {
                    foreach (Payable payable in all)
                    {
                        var upgrade = payable.TryCast<PayableUpgrade>();
                        if (upgrade == null) continue;
                        GameObject next = upgrade.nextPrefab;
                        if (next == null || next.GetComponent<Wall>() == null) continue;
                        if (TryGetX(upgrade, out float x) && !_wallX.Contains(x))
                        {
                            _wallMarkers.Add(new MapMarker { X = x, Label = "城墙建造位" });
                            _wallX.Add(x);
                            if (!boundsValid) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); }
                        }
                    }
                }
            }
            catch { }
        }

        private const int WoodMaxLevel = 2;   // 木制共 2 级
        private const int StoneMaxLevel = 2;  // 石制共 2 级（其后为铁制：城墙 1 级、箭塔 2 级）

        private static readonly string[] _cnLevels = { "一", "二", "三", "四", "五", "六", "七", "八", "九", "十" };

        /// <summary>
        /// 已建成的城墙/箭塔名称后附材质与等级，如「箭塔（木制 二级）」「城墙（石制 一级）」。
        /// 游戏内等级为全局计数（木制 1-2、石制 3-4、铁制 5 起），此处换算为该材质内的等级；
        /// 若某版本按材质内计数（数值 ≤ 前置等级数）则不再偏移，两种计数方式都能显示正确结果。
        /// </summary>
        private static string StructureLabel(string name, TechnologyAge era, int level)
        {
            int lv = level > 0 ? level : 1;
            if (era == TechnologyAge.None) era = EraFromLevel(lv);
            int inMaterial = lv - EraBaseLevel(era);
            if (inMaterial <= 0) inMaterial = lv;
            string levelText = inMaterial <= _cnLevels.Length ? _cnLevels[inMaterial - 1] + "级" : inMaterial + "级";
            string material = EraName(era);
            return material.Length > 0 ? $"{name}（{material} {levelText}）" : $"{name}（{levelText}）";
        }

        /// <summary>技术时代 → 材质名（Wall.wallEraLevel / EraUpgradeBuilding.techAge 同用该枚举）。</summary>
        private static string EraName(TechnologyAge era) => era switch
        {
            TechnologyAge.Wood => "木制",
            TechnologyAge.Stone => "石制",
            TechnologyAge.Iron => "铁制",
            _ => string.Empty,
        };

        /// <summary>该材质之前累计的等级数（木制 2 级、石制 2 级在前，铁制从第 5 级起）。</summary>
        private static int EraBaseLevel(TechnologyAge era) => era switch
        {
            TechnologyAge.Stone => WoodMaxLevel,
            TechnologyAge.Iron => WoodMaxLevel + StoneMaxLevel,
            _ => 0,
        };

        /// <summary>等级 → 材质（游戏材质字段缺失时兜底：1-2 木制、3-4 石制、5 级起铁制）。</summary>
        private static TechnologyAge EraFromLevel(int level)
        {
            if (level <= WoodMaxLevel) return TechnologyAge.Wood;
            if (level <= WoodMaxLevel + StoneMaxLevel) return TechnologyAge.Stone;
            return TechnologyAge.Iron;
        }

        /// <summary>取箭塔的材质时代：箭塔带 EraUpgradeBuilding 时以其 techAge 为准，缺失时为未知。</summary>
        private static TechnologyAge TowerEra(Tower tower)
        {
            try
            {
                EraUpgradeBuilding era = tower.GetComponent<EraUpgradeBuilding>();
                return era != null ? era.techAge : TechnologyAge.None;
            }
            catch { return TechnologyAge.None; }
        }

        /// <summary>
        /// 收集所有难民营与民房标记：Kingdom.BeggarCamps 由游戏维护（难民营被摧毁时从集合移除），
        /// 摧毁后重建的为 CitizenHousePayable（民房）——不在 BeggarCamps 中，需单独收集，颜色与难民营一致。
        /// </summary>
        private void CollectCampMarkers(Kingdom kingdom)
        {
            try
            {
                Il2CppSystem.Collections.Generic.HashSet<BeggarCamp> camps = kingdom.BeggarCamps;
                if (camps != null && camps.Count > 0)
                {
                    var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BeggarCamp>(camps.Count);
                    camps.CopyTo(arr, 0);
                    for (int i = 0; i < arr.Count; i++)
                    {
                        BeggarCamp camp = arr[i];
                        if (camp == null || !TryGetX(camp, out float x)) continue;
                        _campMarkers.Add(new MapMarker { X = x, Label = "难民营" });
                    }
                }
            }
            catch { }

            // 民房（被摧毁难民营的重建形态），颜色不改，名称"民房"
            try
            {
                var houses = UnityEngine.Object.FindObjectsByType<CitizenHousePayable>(FindObjectsSortMode.None);
                foreach (CitizenHousePayable h in houses)
                {
                    if (h == null || !TryGetX(h, out float x)) continue;
                    _campMarkers.Add(new MapMarker { X = x, Label = "民房" });
                }
            }
            catch { }
        }

        /// <summary>收集所有雕像标记，标签为雕像名称+激活状态（如"弓箭手雕像（已激活）"）。</summary>
        private void CollectStatueMarkers()
        {
            try
            {
                var statues = UnityEngine.Object.FindObjectsByType<Statue>(FindObjectsSortMode.None);
                foreach (Statue s in statues)
                {
                    if (s == null || !TryGetX(s, out float x)) continue;
                    string label = "雕像";
                    try
                    {
                        label = DeityName(s.deity);
                        // deityStatus：GemLocked/CoinLocked=未激活，Activated=已激活
                        label += s.deityStatus == Statue.DeityStatus.Activated ? "（已激活）" : "（未激活）";
                    }
                    catch { }
                    _statueMarkers.Add(new MapMarker { X = x, Label = label });
                }
            }
            catch { }
        }

        /// <summary>雕像 deity 枚举 → 中文名称。</summary>
        private static string DeityName(Statue.Deity d)
        {
            switch (d)
            {
                case Statue.Deity.Archer: return "弓箭手雕像";
                case Statue.Deity.Worker: return "工人雕像";
                case Statue.Deity.Knight: return "骑士雕像";
                case Statue.Deity.Farmer: return "农民雕像";
                case Statue.Deity.Time: return "时间雕像";
                case Statue.Deity.Pike: return "长枪兵雕像";
                default: return "雕像";
            }
        }

        /// <summary>收集所有坐骑出生点标记，标签为坐骑具体名称（取出生点首只坐骑的 steedType）。</summary>
        private void CollectMountMarkers()
        {
            try
            {
                var spawns = UnityEngine.Object.FindObjectsByType<SteedSpawn>(FindObjectsSortMode.None);
                foreach (SteedSpawn sp in spawns)
                {
                    if (sp == null || !TryGetX(sp, out float x)) continue;
                    string label = "坐骑";
                    try
                    {
                        var steeds = sp.steeds;
                        if (steeds != null && steeds.Count > 0 && steeds[0] != null)
                            label = SteedName(steeds[0].steedType);
                    }
                    catch { }
                    _mountMarkers.Add(new MapMarker { X = x, Label = label });
                }
            }
            catch { }
        }

        /// <summary>收集所有未开启的宝箱标记：currencyType 区分金币宝箱/宝石宝箱，已开启(_wasTriggered)的不显示。</summary>
        private void CollectChestMarkers()
        {
            try
            {
                var chests = UnityEngine.Object.FindObjectsByType<Chest>(FindObjectsSortMode.None);
                foreach (Chest c in chests)
                {
                    if (c == null || !TryGetX(c, out float x)) continue;
                    bool opened = false;
                    string label = "金币宝箱";
                    try
                    {
                        opened = c._wasTriggered;          // 已触发开启的宝箱跳过
                        if (c.currencyType == CurrencyType.Gems) label = "宝石宝箱";
                    }
                    catch { }
                    if (opened) continue;
                    _chestMarkers.Add(new MapMarker { X = x, Label = label });
                }
            }
            catch { }
        }

        /// <summary>
        /// 收集科技解锁点标记：解锁点是名字含 "undeveloped" 的 Payable 预制体（普通 Payable 组件，无专用类型），
        /// 采石场 Quarry_undeveloped = 石器科技，铁矿（Iron/Mine 命名）= 铁器科技；
        /// 解锁后对象被已开发版本替换，名字不再含 undeveloped，标记自动消失。
        /// </summary>
        private void CollectTechMarkers()
        {
            try
            {
                var all = UnityEngine.Object.FindObjectsByType<Payable>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (Payable p in all)
                {
                    if (p == null) continue;
                    string name;
                    float x;
                    try
                    {
                        name = p.gameObject.name;
                        x = p.transform.position.x;
                    }
                    catch { continue; }
                    if (string.IsNullOrEmpty(name) || !name.Contains("undeveloped")) continue;
                    string label;
                    if (name.Contains("Quarry")) label = "石器科技";
                    else if (name.Contains("Iron") || name.Contains("Mine")) label = "铁器科技";
                    else continue;
                    _techMarkers.Add(new MapMarker { X = x, Label = label });
                }
            }
            catch { }
        }

        /// <summary>坐骑类型枚举 → 中文名称。</summary>
        private static string SteedName(SteedType t)
        {
            switch (t)
            {
                case SteedType.Bear: return "熊";
                case SteedType.P1Griffin:
                case SteedType.P2Griffin: return "狮鹫";
                case SteedType.Lizard: return "蜥蜴";
                case SteedType.Reindeer:
                case SteedType.P2Reindeer_Norselands:
                case SteedType.Reindeer_Norselands: return "驯鹿";
                case SteedType.Spookyhorse: return "幽灵马";
                case SteedType.P2Stag:
                case SteedType.Stag: return "雄鹿";
                case SteedType.Unicorn: return "独角兽";
                case SteedType.P1Warhorse:
                case SteedType.P2Warhorse: return "战马";
                case SteedType.P1Default:
                case SteedType.P2Default: return "马";
                case SteedType.HorseStamina: return "耐力马";
                case SteedType.HorseBurst: return "烈马";
                case SteedType.HorseFast: return "快马";
                case SteedType.P1Wolf:
                case SteedType.P2Wolf: return "狼";
                case SteedType.Bloodstained: return "血污战马";
                case SteedType.Gullinbursti: return "金鬃野猪";
                case SteedType.Sleipnir: return "八足神骏";
                case SteedType.CatCart: return "猫车";
                case SteedType.P2Kelpie:
                case SteedType.Kelpie: return "水马";
                case SteedType.DayNight: return "昼夜马";
                case SteedType.Hippocampus: return "海马";
                case SteedType.Cerberus: return "地狱犬";
                case SteedType.Spider: return "蜘蛛";
                case SteedType.TheChariotDay: return "日神战车";
                case SteedType.TheChariotNight: return "夜神战车";
                case SteedType.Pegasus: return "飞马";
                case SteedType.Donkey: return "毛驴";
                case SteedType.MolossianHound: return "猎犬";
                case SteedType.Chimera: return "奇美拉";
                case SteedType.RainbowPony: return "彩虹小马";
                default: return "坐骑";
            }
        }

        private static bool ShouldShowOverlay()
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

        private static string GameTimeTo24H(float timeHours)
        {
            float dayTime = timeHours % 24f;
            if (dayTime < 0f) dayTime += 24f;
            int h = (int)dayTime;
            int m = (int)((dayTime - h) * 60f);
            return $"{h:D2}:{m:D2}";
        }

        /// <summary>季节枚举转中文名（游戏内枚举值：1=夏、2=秋、4=冬、8=春）。</summary>
        private static string SeasonName(Season season)
        {
            switch ((int)season)
            {
                case 1: return "夏";
                case 2: return "秋";
                case 4: return "冬";
                case 8: return "春";
                default: return "未知";
            }
        }

        private void OnGUI()
        {
            // 使用 Update 中缓存的可见性状态，避免每个 GUI 事件都做 IL2CPP 互操作查询
            if (!_hasData || !_overlayVisible) return;

            bool isMenuOpen = _isMenuOpen;
            GUI.depth = 100;

            float screenW = Screen.width;
            float screenH = Screen.height;
            float frameH = screenH * FrameHeightRatio;
            float centerY = frameH * 0.5f;

            bool showMap = ModWindowBehaviour.ShowMap;
            if (showMap)
            {
                // 屏幕提示：地图框下方正中，显示 MOD 设置窗口快捷键（游戏菜单或 MOD 窗口打开时隐藏）
                if (!isMenuOpen && !ModWindowBehaviour.IsOpen)
                {
                    const float hintW = 400f;
                    GUI.Label(new Rect(screenW * 0.5f - hintW * 0.5f, frameH + 4f, hintW, LabelFontSize + 6f),
                        "按 " + ModWindowBehaviour.OpenKeyDisplay + " 进入BetterKingdom MOD设置菜单", _styleLabel);
                }

                GUI.Box(new Rect(LeftMargin, centerY - LineThickness * 0.5f,
                    screenW - LeftMargin - RightMargin, LineThickness), GUIContent.none, _styleYellow);

                if (_hasPlayer)
                    DrawDotWithLabel(_playerX, centerY, DotRadius, _stylePlayer, "玩家", true);
                if (_hasCastle)
                    DrawDotWithLabel(_castleX, centerY, DotRadius + 1f, _styleCastle, "主城", true);
                foreach (float x in _portalX)
                    DrawDotWithLabel(x, centerY, DotRadius - 1f, _stylePortal, "传送门");
                foreach (float x in _caveX)
                    DrawDotWithLabel(x, centerY, DotRadius - 1f, _stylePortal, "洞窟");
                // 箭塔与城墙："只显示已建造"开启时过滤掉建造位与建造中（标签含"建造"）的标记
                if (ModWindowBehaviour.ShowAllDefenses)
                {
                    bool onlyBuilt = ModWindowBehaviour.ShowOnlyBuiltDefenses;
                    foreach (MapMarker m in _wallMarkers)
                    {
                        if (onlyBuilt && m.Label.Contains("建造")) continue;
                        DrawDotWithLabel(m.X, centerY, DotRadius - 1f, _styleWall, m.Label, true);
                    }
                    foreach (MapMarker m in _towerMarkers)
                    {
                        if (onlyBuilt && m.Label.Contains("建造")) continue;
                        DrawDotWithLabel(m.X, centerY, DotRadius - 2f, _styleTower, m.Label);
                    }
                }
                // 功能开关控制的额外标记（对应列表仅开关开启时才有数据）
                foreach (MapMarker m in _campMarkers)
                    DrawDotWithLabel(m.X, centerY, DotRadius - 1f, _styleCamp, m.Label);
                foreach (MapMarker m in _statueMarkers)
                    DrawDotWithLabel(m.X, centerY, DotRadius - 1f, _styleStatue, m.Label);
                foreach (MapMarker m in _mountMarkers)
                    DrawDotWithLabel(m.X, centerY, DotRadius - 2f, _styleMount, m.Label);
                foreach (MapMarker m in _chestMarkers)
                    DrawDotWithLabel(m.X, centerY, DotRadius - 1f, _styleChest, m.Label);
                foreach (MapMarker m in _techMarkers)
                    DrawDotWithLabel(m.X, centerY, DotRadius - 1f, _styleTech, m.Label);
                // 怪物位置紫线：独立开关控制
                if (ModWindowBehaviour.ShowMapEnemies)
                    foreach (float x in _enemyLineX)
                        DrawEnemyLine(x, frameH);
            }

            // 世界空间悬浮条：怪物血条 / 作物生长进度（独立于地图开关；暂停菜单或 MOD 窗口打开时不绘制）
            if (!isMenuOpen && !ModWindowBehaviour.IsOpen && _cachedCam != null && (_hpBars.Count > 0 || _cropBars.Count > 0))
            {
                Camera cam = _cachedCam;
                foreach (WorldBar b in _hpBars)
                    DrawWorldBar(cam, b.Pos, b.Frac, _styleBarBg, _styleHpFill, HpBarWidth, HpBarHeight);
                foreach (WorldBar b in _cropBars)
                    DrawWorldBar(cam, b.Pos, b.Frac, _styleBarBg, _styleCropFill, CropBarWidth, CropBarHeight);
            }

            // 左上角：金币面板（钱袋/国库），位于职业统计面板上方；地图隐藏时面板贴屏幕顶
            float infoX = 8f;
            float infoLineH = InfoFontSize + 8f;
            float topPanelY = showMap ? frameH + 10f : 8f;
            float coinsPanelH = 0f;
            if (_strCoins != null && ModWindowBehaviour.ShowCoins)
            {
                coinsPanelH = infoLineH + 8f;
                GUI.Label(new Rect(infoX, topPanelY, 290f, infoLineH), _strCoins, _styleInfo);
            }

            // 职业统计面板（左上角，金币面板下方；金币面板存在时整体下移让出空间）
            if (ModWindowBehaviour.ShowClassStats)
            {
                float jobPanelY = topPanelY + coinsPanelH + (coinsPanelH > 0f ? 10f : 0f);
                float jobLineH = JobFontSize + 8f;
                float jobPanelW = 160f;
                GUI.Label(new Rect(infoX, jobPanelY, jobPanelW, jobLineH), "职业统计", _styleJob);
                for (int i = 0; i < _jobLines.Count; i++)
                {
                    GUI.Label(new Rect(infoX, jobPanelY + jobLineH * (i + 1), jobPanelW, jobLineH), _jobLines[i], _styleJob);
                }
            }

            // 左下角信息面板（时间+当前季节天数），字符串均为中频刷新时缓存，此处纯绘制
            if (ModWindowBehaviour.ShowTimeSeason)
            {
                int infoRows = 0;
                if (_strTime != null) infoRows++;
                if (_strSeason != null) infoRows++;
                float infoPanelH = infoLineH * infoRows + 8f;
                float infoPanelY = screenH - infoPanelH - 8f;

                int infoRow = 0;
                if (_strTime != null)
                {
                    GUI.Label(new Rect(infoX, infoPanelY + infoRow * infoLineH, 280f, infoLineH), _strTime, _styleInfo);
                    infoRow++;
                }
                if (_strSeason != null)
                {
                    GUI.Label(new Rect(infoX, infoPanelY + infoRow * infoLineH, 290f, infoLineH), _strSeason, _styleInfo);
                }
            }
        }

        private float ToScreenX(float worldX)
        {
            float usable = Screen.width - LeftMargin - RightMargin;
            float t = (_maxX - _minX) > 0.0001f ? (worldX - _minX) / (_maxX - _minX) : 0.5f;
            return LeftMargin + Mathf.Clamp01(t) * usable;
        }

        /// <summary>绘制怪物位置竖线（贯穿地图框高度），悬停时在线下方显示标签。</summary>
        private void DrawEnemyLine(float worldX, float frameH)
        {
            float sx = ToScreenX(worldX);
            const float halfW = 1.5f;
            GUI.Box(new Rect(sx - halfW, 3f, halfW * 2f, frameH - 6f), GUIContent.none, _styleEnemy);

            // 竖线位置实时变化，不做渐入动画，悬停直接显示标签
            Vector2 mousePos = Event.current.mousePosition;
            if (Mathf.Abs(mousePos.x - sx) < 10f && mousePos.y >= 0f && mousePos.y <= frameH)
            {
                const float labelW = 80f;
                GUI.Label(new Rect(sx - labelW * 0.5f, frameH + 2f, labelW, LabelFontSize + 4f), "怪物", _styleLabel);
            }
        }

        /// <summary>绘制一条世界空间悬浮条（黑底+彩色填充），世界坐标经主相机投影，屏幕外自动跳过。</summary>
        private void DrawWorldBar(Camera cam, Vector3 worldPos, float frac, GUIStyle bgStyle, GUIStyle fillStyle, float barW, float barH)
        {
            Vector3 sp;
            try { sp = cam.WorldToScreenPoint(worldPos); }
            catch { return; }
            if (sp.z < 0f) return;   // 相机背后

            float sx = sp.x - barW * 0.5f;
            float sy = Screen.height - sp.y;   // IMGUI 的 y 轴向下，与屏幕坐标相反
            if (sx > Screen.width || sx + barW < 0f || sy > Screen.height || sy + barH < 0f) return;

            GUI.Box(new Rect(sx, sy, barW, barH), GUIContent.none, bgStyle);
            float fill = Mathf.Clamp01(frac) * (barW - 2f);
            if (fill > 0.5f)
                GUI.Box(new Rect(sx + 1f, sy + 1f, fill, barH - 2f), GUIContent.none, fillStyle);
        }

        private void DrawDotWithLabel(float worldX, float centerY, float baseRadius, GUIStyle style,
            string label, bool labelAbove = false)
        {
            float sx = ToScreenX(worldX);
            Vector2 mousePos = Event.current.mousePosition;
            bool hovered = Vector2.Distance(mousePos, new Vector2(sx, centerY)) < baseRadius + HoverExtraRadius;

            // 数值键代替字符串拼接，消除每次调用的字符串分配
            long key = ((long)label.GetHashCode() << 32) | (uint)BitConverter.SingleToInt32Bits(worldX);
            _hoverAnim.TryGetValue(key, out float anim);
            if (Event.current.type == EventType.Repaint)
            {
                anim = Mathf.MoveTowards(anim, hovered ? 1f : 0f, Time.unscaledDeltaTime * HoverAnimSpeed);
                _hoverAnim[key] = anim;
            }

            float radius = baseRadius * (1f + HoverGrowScale * anim);
            GUI.Box(new Rect(sx - radius, centerY - radius, radius * 2f, radius * 2f), GUIContent.none, style);

            if (anim > 0.01f)
            {
                // 标签宽度按字数自适应（城墙/箭塔名称含材质与等级，固定宽度会截断）
                float labelW = LabelFontSize * label.Length + 8f;
                float labelX = sx - labelW * 0.5f;
                float labelY = labelAbove
                    ? centerY - radius - 2f - (LabelFontSize + 4f)
                    : centerY + radius + 2f;
                Color prev = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, anim);
                GUI.Label(new Rect(labelX, labelY, labelW, LabelFontSize + 4f), label, _styleLabel);
                GUI.color = prev;
            }
        }
    }
}
