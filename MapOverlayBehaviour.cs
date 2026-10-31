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
        private const float OilScanInterval = 4f;    // 火油桶统计间隔（全场景扫描，与 2 秒重刷新错峰）
        private const float CaveScanInterval = 10f;  // 洞窟位置扫描间隔（洞窟是静态对象，只在炸毁时变化）
        private const float FarmScanInterval = 2f;   // 农田引用重建间隔（原先 0.25 秒一次全场景扫描过重）
        private const float DotRadius = 7f;
        private const int EnemySideHysteresis = 2;         // 侧别切换滞回（多数侧需多出该数量才切换）
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
        // 双向传送门成对连线（A/B 为两端世界X；Level 为引导线分层，避免多组连线在同一高度重合）
        private struct TeleLink { public float A; public float B; public int Level; }
        private readonly List<float> _teleporterX = new();
        private readonly List<TeleLink> _teleLinks = new();
        private Texture2D _texCamp, _texStatue, _texMount, _texChest, _texTech, _texTeleporter, _texTeleLink;
        private GUIStyle _styleCamp, _styleStatue, _styleMount, _styleChest, _styleTech, _styleTeleporter, _styleTeleLink;

        // 世界空间悬浮条（怪物血条/作物生长进度）
        private Texture2D _texBarBg, _texHpFill, _texCropFill;
        private GUIStyle _styleBarBg, _styleHpFill, _styleCropFill;
        private struct WorldBar { public Vector3 Pos; public float Frac; }
        private readonly List<WorldBar> _hpBars = new();       // 怪物血条（轻刷新重建）
        private readonly List<WorldBar> _cropBars = new();     // 作物进度条（轻刷新重建）
        private readonly List<Farmland> _farmlands = new();    // 农田引用缓存（中频重建，低频读进度）
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
        // 世界边界缓存：仅关卡加载时计算一次，读取失败期间沿用上次有效值，避免整张地图比例来回跳变
        private float _boundLeft, _boundRight;
        // 关卡地面原始边界 + 洞窟侧裁剪标记：地面会延伸到悬崖内部，只按洞窟坐标裁剪一次，洞窟被炸后不再回弹
        private float _rawBoundLeft, _rawBoundRight;
        private bool _caveTrimmed;
        private bool _boundsReady;
        private bool _hasData;
        private float _nextLightRefresh, _nextMediumRefresh, _nextHeavyRefresh;
        // 全场景扫描（FindObjectsByType）彼此错峰：集中在一帧执行会形成每 2 秒一次的帧尖峰
        private float _nextOilScan, _nextCaveScan, _nextFarmScan;
        // 可选标记分帧轮询：每次重刷新只扫一类（难民营/雕像/坐骑/宝箱/科技），每类约 10 秒刷新一次
        private int _optionalScanSlot;

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
        private readonly Dictionary<uint, WaveGroup> _waveGroups = new();   // 按 waveID 分组，跨帧保留同一波的平滑位置
        private readonly List<uint> _staleWaveIds = new();                  // 本帧已消失的波次ID（遍历后再删除）

        /// <summary>一波怪物的聚合数据（按 waveID 缓存，避免频繁分配）。</summary>
        private sealed class WaveGroup
        {
            public float Min, Max;
            public int LeftCount, RightCount;   // 本帧以城堡为界的左右两侧数量（判定本波属于哪一侧）
            public int Side;                    // 0=未判定 1=左侧 2=右侧（带滞回，避免分界处来回切换）
            public bool Seen;                   // 本帧是否已统计（Min/Max 必须每帧重算，不能沿用历史极值）
            public void Reset(float x) { Min = x; Max = x; LeftCount = 0; RightCount = 0; Side = 0; Seen = true; }
            public void Begin(float x) { Min = x; Max = x; LeftCount = 0; RightCount = 0; Seen = true; }
            public void Add(float x)
            {
                if (x < Min) Min = x;
                if (x > Max) Max = x;
            }
        }

        //   游戏时间  
        private float _gameTime = -1f;
        private Banker _cachedBanker;      // 银行家（税官）缓存（中频重建），国库真实账本 Banker._stashedCoins 所在

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
            _texPlayer = MakeSolidTexture(new Color(0.25f, 1f, 0.35f, 1f));    // 玩家：绿
            _texPortal = MakeSolidTexture(new Color(1f, 0.25f, 0.2f, 1f));
            _texWall = MakeSolidTexture(new Color(0.62f, 0.62f, 0.66f, 1f));   // 城墙：灰
            _texTower = MakeSolidTexture(new Color(0.76f, 0.55f, 0.32f, 1f));  // 箭塔：木色
            _texEnemy = MakeSolidTexture(new Color(0.55f, 0.15f, 0.75f, 1f));  // 怪物竖线：较深紫
            _texCastle = MakeSolidTexture(new Color(1f, 1f, 1f, 1f));
            _texCamp = MakeSolidTexture(new Color(0.95f, 0.55f, 0.15f, 1f));   // 难民营：橙
            _texStatue = MakeSolidTexture(new Color(0.55f, 0.72f, 0.6f, 1f));  // 雕像：灰绿
            _texMount = MakeSolidTexture(new Color(0f, 0.5f, 0.25f, 1f));      // 坐骑出生点：深绿
            _texChest = MakeSolidTexture(new Color(1f, 0.78f, 0.1f, 1f));    // 宝箱：金
            _texTech = MakeSolidTexture(new Color(0.3f, 0.55f, 1f, 1f));      // 科技解锁点：蓝
            _texTeleporter = MakeSolidTexture(new Color(0.42f, 0.6f, 0.95f, 1f));   // 自建传送门：偏蓝的灰蓝
            _texTeleLink = MakeSolidTexture(new Color(0.5f, 0.68f, 1f, 1f));       // 双向传送门连线：与传送门相近、略亮
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
            _styleTeleporter = MakeSolidStyle(_texTeleporter);
            _styleTeleLink = MakeSolidStyle(_texTeleLink);
            _styleBarBg = MakeSolidStyle(_texBarBg);
            _styleHpFill = MakeSolidStyle(_texHpFill);
            _styleCropFill = MakeSolidStyle(_texCropFill);
            Instance = this;

            _styleLabel = new GUIStyle { normal = { textColor = Color.white }, fontSize = LabelFontSize, alignment = TextAnchor.UpperCenter };
            _styleInfo = new GUIStyle { normal = { textColor = Color.white }, fontSize = InfoFontSize, alignment = TextAnchor.UpperLeft };
            _styleJob = new GUIStyle { normal = { textColor = Color.white }, fontSize = JobFontSize, alignment = TextAnchor.UpperLeft };
            // 错峰初始化：首帧先做洞窟扫描与重刷新，其余全场景扫描推迟到后续帧
            _nextCaveScan = Time.unscaledTime;
            _nextOilScan = Time.unscaledTime + 1f;
            _nextFarmScan = Time.unscaledTime + 1f;
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

            // 全场景扫描彼此错峰：FindObjectsByType 会遍历整个场景，集中执行会形成每 2 秒一次的帧尖峰
            if (now >= _nextCaveScan)
            {
                _nextCaveScan = now + CaveScanInterval;
                RefreshCavePositions();
            }
            if (now >= _nextOilScan)
            {
                _nextOilScan = now + OilScanInterval;
                RefreshOilBarrels();
            }
            if (now >= _nextFarmScan)
            {
                _nextFarmScan = now + FarmScanInterval;
                RefreshFarmlandRefs();
            }

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
            DestroyImmediate(_texTeleporter);
            DestroyImmediate(_texTeleLink);
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

                // 农田引用缓存已独立成 2 秒定时任务（见 Update 的 FarmScanInterval），中频这里不再重建

                // 相机缓存（世界转屏幕用）：本游戏 Camera.main 为 null，需遍历所有相机找启用的
                RefreshCameraRef();

                // 时间
                try
                {
                    Director director = managers.director;
                    if (director != null)
                    {
                        _gameTime = director.currentTime;

                        // 季节与季节内天数：直接使用游戏的季节日历（关卡 LandData 数据驱动，默认每季 16 天）
                        try
                        {
                            int day = GetDayNumber(director);
                            // 游戏天数在午夜 0 点递增，这里把换日推迟到清晨（白天开始、怪物撤退/自动存档的时刻）：
                            // 午夜到清晨之间仍显示前一天的天数与季节
                            if (day > 0 && _gameTime < GetMorningHour(director)) day--;
                            if (day < 0) day = 0;
                            if (!TryGetSeasonFromLand(director, day, out _season, out _seasonDay))
                            {
                                _season = GetSeason(director, day);
                                _seasonDay = GetSeasonDay(director, day);
                            }
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

                // 按 waveID 分组：字典跨帧保留同一波次，侧别判定才能延续；
                // 每帧先把所有分组的“已统计”标记清零，Min/Max 必须按本帧怪物重算
                foreach (KeyValuePair<uint, WaveGroup> kv in _waveGroups) kv.Value.Seen = false;

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

                    WaveGroup g;
                    if (!_waveGroups.TryGetValue(id, out g))
                    {
                        g = new WaveGroup();
                        g.Reset(x);          // 全新一波：侧别与平滑起点重新初始化
                        _waveGroups.Add(id, g);
                    }
                    else if (!g.Seen)
                    {
                        g.Begin(x);          // 同一波跨帧延续：只重置本帧统计，保留侧别与平滑位置
                    }
                    else
                    {
                        g.Add(x);
                    }

                    // 按城堡分界统计本波怪物落在左右两侧的数量，用于稳定判定本波属于哪一侧
                    if (x >= divider) g.RightCount++;
                    else g.LeftCount++;
                }

                // 移除本帧已不存在的波次，避免残留位置被后续波次复用
                _staleWaveIds.Clear();
                foreach (KeyValuePair<uint, WaveGroup> kv in _waveGroups)
                {
                    if (!kv.Value.Seen) _staleWaveIds.Add(kv.Key);
                }
                for (int i = 0; i < _staleWaveIds.Count; i++) _waveGroups.Remove(_staleWaveIds[i]);

                foreach (KeyValuePair<uint, WaveGroup> kv in _waveGroups)
                {
                    WaveGroup g = kv.Value;

                    // 侧别带滞回：多数侧明显反超才切换，避免同波怪物跨城堡分界时竖线在左右端之间来回跳
                    if (g.Side == 0)
                        g.Side = g.RightCount >= g.LeftCount ? 2 : 1;
                    else if (g.Side == 2 && g.LeftCount > g.RightCount + EnemySideHysteresis)
                        g.Side = 1;
                    else if (g.Side == 1 && g.RightCount > g.LeftCount + EnemySideHysteresis)
                        g.Side = 2;

                    // 右侧出怪显示最左侧怪的位置，左侧出怪显示最右侧怪的位置；
                    // 目标点（最近怪物）变化时竖线立即跟随，不做平滑插值
                    _enemyLineX.Add(g.Side == 2 ? g.Min : g.Max);
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

                // 火油桶统计已独立成 4 秒定时任务（见 Update 的 OilScanInterval），不再放进重刷新

                _portalX.Clear();
                _wallX.Clear();
                _teleporterX.Clear();
                _teleLinks.Clear();
                // 悬停动画不再每次重刷新清空（否则鼠标停在标记上时标记会周期性缩回再放大），仅在键数量异常增长时清理
                if (_hoverAnim.Count > 512) _hoverAnim.Clear();

                float minX = float.MaxValue;
                float maxX = float.MinValue;

                World world = managers.world;
                bool boundsValid = false;
                if (TryGetWorldBounds(world, out float boundLeft, out float boundRight))
                {
                    // 地图范围只由关卡地面决定，不参与任何地图标记的计算，避免开关标记时整图比例变化
                    if (!_boundsReady || Math.Abs(boundLeft - _rawBoundLeft) > 1f || Math.Abs(boundRight - _rawBoundRight) > 1f)
                    {
                        // 换关卡：还原地面原始边界，允许重新按洞窟位置裁剪
                        _rawBoundLeft = boundLeft;
                        _rawBoundRight = boundRight;
                        _boundLeft = boundLeft;
                        _boundRight = boundRight;
                        _caveTrimmed = false;
                    }
                    _boundsReady = true;
                }
                // 读取失败（加载切换等瞬间）时沿用上次有效边界，避免退化成按玩家位置估算导致全部标记左右平移
                if (_boundsReady) boundsValid = true;

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

                // 洞窟坐标由 RefreshCavePositions 单独扫描（10 秒一次），这里只复用缓存：
                // 过滤与洞窟重合的怪物传送门；边界不可用时把洞窟坐标纳入地图范围
                try
                {
                    _portalX.RemoveAll(px => CaveOverlaps(px, _caveX));
                    if (!boundsValid)
                    {
                        for (int i = 0; i < _caveX.Count; i++)
                        {
                            minX = Math.Min(minX, _caveX[i]);
                            maxX = Math.Max(maxX, _caveX[i]);
                        }
                    }
                }
                catch { }

                // 洞窟侧的地面会一直延伸到悬崖后方，按洞窟位置裁掉这段无用区域，避免该侧地图留空过大。
                // 只在当前关卡首次拿到洞窟坐标时裁一次：洞窟被炸掉后不再回弹到地面原始边界。
                if (_boundsReady && !_caveTrimmed && _caveX.Count > 0)
                {
                    float edgePad = 0.005f * (_rawBoundRight - _rawBoundLeft);
                    float midBound = (_rawBoundLeft + _rawBoundRight) * 0.5f;
                    foreach (float caveX in _caveX)
                    {
                        if (caveX < midBound) _boundLeft = Math.Max(_boundLeft, caveX - edgePad);
                        else _boundRight = Math.Min(_boundRight, caveX + edgePad);
                    }
                    // 裁剪异常（左右交叉）时回退到地面原始边界
                    if (_boundLeft >= _boundRight)
                    {
                        _boundLeft = _rawBoundLeft;
                        _boundRight = _rawBoundRight;
                    }
                    _caveTrimmed = true;
                }

                // 边界确定后作为范围基准（此时裁剪已完成）
                if (boundsValid)
                {
                    minX = _boundLeft;
                    maxX = _boundRight;
                }

                CollectWallAndTowerMarkers(kingdom, boundsValid, ref minX, ref maxX);

                // 功能开关控制的额外标记：分帧轮询，每次重刷新只扫一类（开关关闭时立即清空）
                ScanOptionalMarkers(kingdom);
                if (ModWindowBehaviour.ShowTeleporters) CollectTeleporters(world);

                _hasData = minX < maxX;
                if (!_hasData) return;

                float pad = (boundsValid ? 0.005f : 0.02f) * (maxX - minX);
                _minX = minX - pad;
                _maxX = maxX + pad;
            }
            catch { }
        }

        /// <summary>
        /// 读取关卡真实左右边界，供地图坐标映射使用。
        /// 优先取 World.GroundCollider 的世界包围盒（游戏就是用地面碰撞体计算 worldBounds 的），
        /// 其次读 World.worldBounds；两者都拿不到才返回 false（由调用方回退到按标记估算）。
        /// </summary>
        private static bool TryGetWorldBounds(World world, out float left, out float right)
        {
            left = 0f;
            right = 0f;

            // GroundCollider 是 World 的静态成员：即当前关卡地面碰撞体
            try
            {
                BoxCollider2D ground = World.GroundCollider;
                if (ground != null)
                {
                    Bounds bounds = ground.bounds;
                    if (bounds.size.x > 10f)
                    {
                        left = bounds.min.x;
                        right = bounds.max.x;
                        return true;
                    }
                }
            }
            catch { }

            if (world == null) return false;

            try
            {
                Sided<float> bounds = world.worldBounds;
                if (bounds != null && bounds.right - bounds.left > 10f)
                {
                    left = bounds.left;
                    right = bounds.right;
                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>检查 worldX 是否与 caveX 列表中的任何坐标重合（距离<1）。</summary>
        private static bool CaveOverlaps(float worldX, List<float> caveX)
        {
            for (int i = 0; i < caveX.Count; i++)
                if (Math.Abs(caveX[i] - worldX) < 1f) return true;
            return false;
        }

        /// <summary>
        /// 刷新洞窟坐标（10 秒一次）：洞窟是静态关卡对象，只有被炸毁时集合才变化，
        /// 没必要塞进 2 秒重刷新里做一次全场景扫描。已裁剪的地图边界不会因洞窟消失而回弹。
        /// </summary>
        private void RefreshCavePositions()
        {
            _caveX.Clear();
            try
            {
                var cliffs = UnityEngine.Object.FindObjectsByType<BombablePortal>(FindObjectsSortMode.None);
                for (int i = 0; i < cliffs.Length; i++)
                {
                    if (TryGetX(cliffs[i], out float x)) _caveX.Add(x);
                }
            }
            catch { }
        }

        /// <summary>
        /// 可选标记分帧轮询：难民营/雕像/坐骑/宝箱/科技解锁点各自需要一次全场景扫描，
        /// 集中在一帧执行会形成明显卡顿；这里每次重刷新只处理一类（每类约 10 秒刷新一次）。
        /// 关闭对应开关时立即清空，不受轮询节奏影响。
        /// </summary>
        private void ScanOptionalMarkers(Kingdom kingdom)
        {
            if (!ModWindowBehaviour.ShowAllCamps) _campMarkers.Clear();
            if (!ModWindowBehaviour.ShowStatues) _statueMarkers.Clear();
            if (!ModWindowBehaviour.ShowMountSpawns) _mountMarkers.Clear();
            if (!ModWindowBehaviour.ShowChests) _chestMarkers.Clear();
            if (!ModWindowBehaviour.ShowTechPoints) _techMarkers.Clear();

            _optionalScanSlot++;
            if (_optionalScanSlot >= 5) _optionalScanSlot = 0;
            try
            {
                switch (_optionalScanSlot)
                {
                    case 0:
                        if (!ModWindowBehaviour.ShowAllCamps) break;
                        _campMarkers.Clear();
                        CollectCampMarkers(kingdom);
                        break;
                    case 1:
                        if (!ModWindowBehaviour.ShowStatues) break;
                        _statueMarkers.Clear();
                        CollectStatueMarkers();
                        break;
                    case 2:
                        if (!ModWindowBehaviour.ShowMountSpawns) break;
                        _mountMarkers.Clear();
                        CollectMountMarkers();
                        break;
                    case 3:
                        if (!ModWindowBehaviour.ShowChests) break;
                        _chestMarkers.Clear();
                        CollectChestMarkers();
                        break;
                    default:
                        if (!ModWindowBehaviour.ShowTechPoints) break;
                        _techMarkers.Clear();
                        CollectTechMarkers();
                        break;
                }
            }
            catch { }
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
            if (!ModWindowBehaviour.ShowCropProgress) return;
            try
            {
                var lands = UnityEngine.Object.FindObjectsByType<Farmland>(FindObjectsSortMode.None);
                if (lands == null) return;
                for (int i = 0; i < lands.Length; i++)
                    if (lands[i] != null) _farmlands.Add(lands[i]);
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
                    if (frac >= 1f) continue;   // 已成熟
                    // 没有农夫耕作过的田不显示（未耕作时进度点为 0 且未被标记耕作中）
                    if (cur <= 0 && !isFarmed) continue;

                    // 阶梯更新：按游戏的真实进度点（整数）直接显示，不做平滑插值
                    _cropBars.Add(new WorldBar { Pos = f.transform.position + new Vector3(0f, CropBarWorldOffsetY, 0f), Frac = Mathf.Clamp01(frac) });
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

        /// <summary>
        /// 收集自建传送门：World.teleporters 中的 PayableTeleporter。
        /// linkedTo 为空 = 普通传送门（可在地图任意位置往返穿梭）；
        /// linkedTo 非空 = 双向传送门（由传送裂缝建成，只能在两端之间定点穿梭），成对记录连线。
        /// 注意：这里不参与地图范围计算，否则开关本选项会让整张地图比例发生变化。
        /// </summary>
        private void CollectTeleporters(World world)
        {
            try
            {
                var teleporters = world != null ? world.teleporters : null;
                if (teleporters == null) return;

                foreach (PayableTeleporter tp in teleporters)
                {
                    if (tp == null || !TryGetX(tp, out float x)) continue;

                    _teleporterX.Add(x);

                    PayableTeleporter linked = null;
                    try { linked = tp.linkedTo; } catch { }
                    if (linked == null || !TryGetX(linked, out float lx)) continue;

                    // 双向传送门两端互指，同一对只记录一次连线（按坐标排序后去重）
                    float a = Math.Min(x, lx);
                    float b = Math.Max(x, lx);
                    bool exists = false;
                    for (int i = 0; i < _teleLinks.Count; i++)
                    {
                        if (Math.Abs(_teleLinks[i].A - a) < 0.01f && Math.Abs(_teleLinks[i].B - b) < 0.01f)
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists) _teleLinks.Add(new TeleLink { A = a, B = b });
                }

                AssignTeleLinkLevels();
            }
            catch { }
        }

        /// <summary>
        /// 给每组双向传送门连线分配一个引导线层级：横向区间重叠的连线放到不同层，
        /// 绘制时外层（层级小）画在上方、内层（层级大）画在下方，避免多组连线重合而无法区分配对。
        /// </summary>
        private void AssignTeleLinkLevels()
        {
            if (_teleLinks.Count <= 1)
            {
                if (_teleLinks.Count == 1)
                {
                    TeleLink only = _teleLinks[0];
                    only.Level = 0;
                    _teleLinks[0] = only;
                }
                return;
            }

            // 按左端点（再按右端点）排序，保证每次重刷新后的分层结果稳定
            _teleLinks.Sort((p, q) =>
            {
                int cmp = p.A.CompareTo(q.A);
                return cmp != 0 ? cmp : p.B.CompareTo(q.B);
            });

            for (int i = 0; i < _teleLinks.Count; i++)
            {
                TeleLink cur = _teleLinks[i];
                int level = 0;
                while (true)
                {
                    bool clash = false;
                    for (int j = 0; j < i; j++)
                    {
                        TeleLink prev = _teleLinks[j];
                        if (prev.Level != level) continue;
                        // 区间相交（含嵌套）：与同层已有连线重叠则换下一层
                        if (cur.A < prev.B && prev.A < cur.B)
                        {
                            clash = true;
                            break;
                        }
                    }
                    if (!clash) break;
                    level++;
                }
                cur.Level = level;
                _teleLinks[i] = cur;
            }
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
                // 双向传送门连线先画（位于标记上方），再画传送门标记
                // 横向区间重叠的连线已分层，这里按最大层数把它们展开到不同高度
                int maxLinkLevel = 0;
                foreach (TeleLink link in _teleLinks)
                    if (link.Level > maxLinkLevel) maxLinkLevel = link.Level;
                foreach (TeleLink link in _teleLinks)
                    DrawTeleLink(link, centerY, maxLinkLevel);
                foreach (float x in _teleporterX)
                    DrawDotWithLabel(x, centerY, DotRadius - 1f, _styleTeleporter, "传送门");
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

        /// <summary>
        /// 绘制双向传送门连线：两点各自向上引一小段竖线，再由顶部横线相连（样式参考游戏内传送门连线）。
        /// 多组连线横向重叠时按 Level 抬高顶部横线，高度不同即可区分各自配对，颜色保持不变。
        /// </summary>
        private void DrawTeleLink(TeleLink link, float centerY, int maxLevel)
        {
            float ax = ToScreenX(link.A);
            float bx = ToScreenX(link.B);
            if (Mathf.Abs(bx - ax) < 1f) return;

            const float halfW = 1f;                       // 线宽的一半（总宽 2 像素）
            const float baseGap = 8f;                     // 单组连线时竖线高度（标记上缘到顶部横线的距离）
            float dotTop = centerY - (DotRadius - 1f);    // 标记点（半径 DotRadius-1）的上缘

            // 多层时在标记上方可用高度内均匀排开，保证最高一层也留在屏幕内
            float step = 0f;
            if (maxLevel > 0)
            {
                float available = Mathf.Max(4f, dotTop - 2f - baseGap);
                step = Mathf.Min(7f, available / maxLevel);
            }
            // 层级越小越靠外：外层画得更高、内层画得更低（贴近标记），形成套叠的括号；
            // 这样内层竖线不会穿过外层的横线。部分重叠（一外一内）按分层顺序各自错开高度。
            float linkY = dotTop - baseGap - (maxLevel - link.Level) * step;

            // 两端竖线
            GUI.Box(new Rect(ax - halfW, linkY, halfW * 2f, dotTop - linkY), GUIContent.none, _styleTeleLink);
            GUI.Box(new Rect(bx - halfW, linkY, halfW * 2f, dotTop - linkY), GUIContent.none, _styleTeleLink);
            // 顶部横线（左右各外扩半线宽，与竖线形成折角）
            float left = Mathf.Min(ax, bx) - halfW;
            GUI.Box(new Rect(left, linkY - halfW, Mathf.Abs(bx - ax) + halfW * 2f, halfW * 2f),
                GUIContent.none, _styleTeleLink);
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

        // ==================== 季节时钟（覆盖层左下角时间/季节显示） ====================

        private const int DaysPerSeason = 16;   // 兜底用：默认关卡每季 16 天（14 常规 + 1 血月 + 1 恢复）
        private const float DefaultMorningHour = 5f;   // 缺省清晨时刻：游戏白天起点（怪物撤退/自动存档）

        /// <summary>
        /// 清晨换日时刻：优先取 Director.dayStart（游戏判定白天开始的时间，约 5 点），
        /// 取不到时退回当前 TimesOfDay 的黎明开始时间，再取默认值。
        /// </summary>
        private static float GetMorningHour(Director director)
        {
            if (director != null)
            {
                try
                {
                    float dayStart = director.dayStart;
                    if (dayStart > 0f && dayStart < 24f) return dayStart;
                }
                catch { }
                try
                {
                    TimesOfDay tod = director.CurrentTimesOfDay;
                    if (tod != null)
                    {
                        float dawn = tod.dawnStart;
                        if (dawn > 0f && dawn < 24f) return dawn;
                    }
                }
                catch { }
            }
            return DefaultMorningHour;
        }

        /// <summary>
        /// 统治累计天数（换岛不重置）：优先取 Director.TotalDaysInReign，这是游戏季节日历使用的日序号；
        /// 换岛加载瞬间该字段可能为 0，此时回退到存档级的战役累计天数 +1，避免日期跳变；均失败返回 -1。
        /// </summary>
        private static int GetDayNumber(Director director)
        {
            try
            {
                if (director != null)
                {
                    int day = director.TotalDaysInReign;
                    if (day > 0) return day;
                }
            }
            catch { }

            try
            {
                CampaignSaveData save = CampaignSaveData.current;
                if (save != null)
                {
                    int day = save.TotalGameDays + 1;
                    if (day > 0) return day;
                }
            }
            catch { }

            return -1;
        }

        /// <summary>
        /// 用关卡自身的季节日历取季与季内天数（LandData/SeasonData 数据驱动，默认每季 16 天）。
        /// 日序号与游戏一致：TotalDaysInReign 从 0 开始，季节起始日为 16 的整数倍；
        /// 季内天数 = 日序号 - 当季起始日 + 1，取值 1..TotalNumDays。
        /// </summary>
        private static bool TryGetSeasonFromLand(Director director, int day, out Season season, out int seasonDay)
        {
            season = (Season)8;
            seasonDay = 0;
            if (director == null || day < 0) return false;

            try
            {
                CycleData.LandData land = director.LandCycleData;
                if (land == null) return false;

                // 代理程序集里 byref 参数暴露为 ref（不是 out）
                int startDay = 0;
                CycleData.SeasonData data = land.GetSeasonForDay(day, ref startDay);
                if (data == null) return false;

                int total = data.TotalNumDays;
                int current = day - startDay + 1;
                if (total <= 0 || current < 1 || current > total) return false;

                season = data.season;
                seasonDay = current;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 兜底：按 0 基日序号直接推算季节（默认每季 16 天，一年从春天开始）；
        /// 拿到 LandCycleData 时不会走到这里。
        /// </summary>
        private static Season GetSeason(Director director, int day)
        {
            if (day >= 0)
            {
                int index = day / DaysPerSeason;
                try
                {
                    if (director != null && director.startGameInAutumn) index += 2;
                }
                catch { }

                switch (index % 4)
                {
                    case 0: return (Season)8;   // 春
                    case 1: return (Season)1;   // 夏
                    case 2: return (Season)2;   // 秋
                    default: return (Season)4;  // 冬
                }
            }

            try
            {
                if (director != null) return director.CurrentSeason;
            }
            catch { }

            return (Season)8;
        }

        /// <summary>
        /// 兜底：季内第几天（1..16）。游戏日序号从 0 开始，所以是 day % 16 + 1，不能再减 1。
        /// </summary>
        private static int GetSeasonDay(Director director, int day)
        {
            if (day >= 0) return day % DaysPerSeason + 1;

            try
            {
                if (director != null && director.CurrentSeasonStartDay >= 0)
                    return director.TotalDaysInReign - director.CurrentSeasonStartDay + 1;
            }
            catch { }

            return 0;
        }
    }
}
