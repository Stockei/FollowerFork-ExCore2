using RectangleF = ExileCore2.Shared.RectangleF;
using FollowerInternals;
using ExileCore2.Shared.Nodes;
using ExileCore2.Shared.Interfaces;
using ImGuiNET;
using System.Numerics;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Drawing;
using System.Windows.Forms;
using System.Threading;


namespace Follower;
public class Follower : BaseSettingsPlugin<FollowerSettings>
{
    private const int SPRINT_HOLD_TO_START_MS = 1900;
    private const int SPRINT_RELEASE_STABLE_MS = 250;
    private const int SPRINT_MAX_HOLD_MS = 2600;

    // Safety throttle: PoE can disconnect clients when movement/action input is generated too densely.
    // User-facing settings may still be set aggressively, but actual server-relevant key taps are clamped here.
    private const int MIN_SAFE_BOT_INPUT_MS = 90;
    private const int MIN_MOVEMENT_KEY_TAP_GAP_MS = 125;
    private const int MIN_FORCED_KEYUP_GAP_MS = 250;
    private const int MIN_CURSOR_MOVE_GAP_MS = 35;
    private const float MIN_CURSOR_MOVE_DISTANCE_PX = 4f;
    private AutoParty _autoParty;
    private PartyTeleport _partyTeleport;
    private PartyChatCommands _partyChatCommands;
    private TradeInventoryDump _tradeInventoryDump;
    private FollowerPickUpManager _pickUpManager;
    private bool _pausedByPartyChatCommand;
    private bool _wholePluginPausedByPartyChatCommand;
    private SpikeProfiler _spikeProfiler;
private Random random = new Random();
    private Camera Camera => GameController.IngameState.Camera;
    private Dictionary<uint, Entity> _areaTransitions = new Dictionary<uint, Entity>();

    // Hideout map portals are most reliable through visible ground labels. MTX portals expose their
    // clickable labels with metadata below; EntityType/IsTargetable are not consistent across skins.
    private const string MapCheckerPortalPath = "Metadata/MiscellaneousObjects/MultiplexPortal";
    private const string TownPortalMtxMetadataPrefix = "Metadata/Effects/Microtransactions/Town_Portals/";
    private const string HideoutPortalMetadataPrefix = "Metadata/MiscellaneousObjects/Portals/";
    private const string ArenaTransitionMetadataFilter = "Metadata/MiscellaneousObjects/AreaTransition";
    private const string AbyssSubAreaTransitionMetadataPath = "Metadata/MiscellaneousObjects/Abyss/AbyssSubAreaTransition";
    private const int PortalHoverDelayMs = 80;
    private const int PortalLabelScanMaxVisibleLabels = 96;
    private const int LeaderPortalEntryRequestTtlMs = 12000;
    private const int LeaderPortalEntryRequestScanMs = 250;
    private DateTime _portalHoverClickAt = DateTime.MinValue;
    private uint _portalHoverEntityId;
    private bool _leaderPortalEntryRequested;
    private DateTime _leaderPortalEntryRequestExpiresAt = DateTime.MinValue;
    private DateTime _nextLeaderPortalEntryRequestScanAt = DateTime.MinValue;

    // Boss arena entrances in maps are exposed as ground labels, not always as normal
    // AreaTransition entities. The UI hover from the user shows:
    // IngameUi -> ItemsOnGroundLabelsElement -> LabelsOnGroundVisible -> Label.Text == "Arena".
    private const int ArenaTransitionHoverDelayMs = 80;
    private DateTime _nextArenaTransitionScanAt = DateTime.MinValue;
    private DateTime _arenaTransitionHoverClickAt = DateTime.MinValue;
    private uint _arenaTransitionHoverEntityId;
    private DateTime _arenaTransitionRetrySuppressedUntil = DateTime.MinValue;
    private bool _activeArenaTransitionIsAbyssSubArea;

    private Vector3 _lastTargetPosition;
    private Vector3 _lastPlayerPosition;
    private Entity _followTarget;
    private DateTime _lastAreaChangeAt = DateTime.MinValue;

    
    private bool IsInHideout()
    {
        var area = GameController.Area.CurrentArea;
        if (area == null)
            return false;

        if (area.IsHideout || area.Name.Contains("Hideout", StringComparison.OrdinalIgnoreCase))
            return true;

        // NOTE: Do NOT treat Vaal Ruins as a hideout. The Atziri entrance there
        // is handled via special transition logic (see AreaChange/EntityAdded),
        // so we keep standard follow behaviour in this area.
        // if (area.Name.Contains("Vaal Ruins", StringComparison.OrdinalIgnoreCase))
        //     return true;

        return false;
    }


    private bool IsInAtziriEntranceArea()
    {
        var area = GameController.Area.CurrentArea;
        if (area == null)
            return false;

        // Atziri entrance is inside the Vaal Ruins area.
        return area.Name.Contains("Vaal Ruins", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsInAbyssSubArea()
    {
        var area = GameController.Area.CurrentArea;
        if (area == null)
            return false;

        return area.Name.Contains("Abyssal Depths", StringComparison.OrdinalIgnoreCase);
    }

    private bool _hasUsedWP = true;


    private List<TaskNode> _tasks = new List<TaskNode>();
    private DateTime _nextBotAction = DateTime.Now;

    // Runtime diagnostics / watchdog state.
    private string _runtimeMode = "Init";
    private string _runtimeDetail = string.Empty;
    private string _runtimeTarget = string.Empty;
    private DateTime _runtimeStatusAt = DateTime.MinValue;
    private TaskNode.TaskNodeType? _watchdogTaskType;
    private Vector3 _watchdogTaskPosition = Vector3.Zero;
    private DateTime _watchdogTaskStartedAt = DateTime.MinValue;
    private DateTime _watchdogLastProgressAt = DateTime.MinValue;
    private float _watchdogBestTaskDistance = float.MaxValue;
    private int _watchdogResetCount;

    // Non-blocking input scheduler. Never sleep inside Render/Tick.
    private bool _movementKeyDownByPlugin;
    private DateTime _movementKeyReleaseAt = DateTime.MinValue;
    private bool _dodgeSprintTapDownByPlugin;
    private DateTime _dodgeSprintTapReleaseAt = DateTime.MinValue;
    private bool _escapeTapDownByPlugin;
    private DateTime _escapeTapReleaseAt = DateTime.MinValue;
    private DateTime _nextForcedInputReleaseAt = DateTime.MinValue;
    private DateTime _nextMovementKeyTapAt = DateTime.MinValue;
    private DateTime _nextForcedMovementKeyUpAt = DateTime.MinValue;
    private DateTime _nextForcedDodgeSprintKeyUpAt = DateTime.MinValue;
    private DateTime _nextCursorMoveAt = DateTime.MinValue;
    private Vector2 _lastCursorMoveTarget = Vector2.Zero;

    private DateTime _nextSprintAt = DateTime.MinValue;

    private enum SprintFsm { Idle, Holding }

    private float _distEwma = 0f;
    private DateTime _releaseGateStart = DateTime.MinValue;
    
    private SprintFsm _sprintFsmState = SprintFsm.Idle;
    private DateTime _sprintHoldUntil = DateTime.MinValue;
    private DateTime _sprintForceReleaseAt = DateTime.MinValue;
    private DateTime _nextSprintAllowed = DateTime.MinValue;
    private bool _sprintKeyDown = false;
    
    private bool _sprintHeld = false;
    private DateTime _lastSprintToggle = DateTime.MinValue;

    

    private int _numRows, _numCols;
    private byte[,] _tiles;

    public override bool Initialise()
    {
        Name = "Follower";
        Input.RegisterKey(Settings.General.MovementKey.Value);

        Input.RegisterKey(Settings.General.ToggleFollower.Value);
        Settings.General.ToggleFollower.OnValueChanged += () => { Input.RegisterKey(Settings.General.ToggleFollower.Value); };
        Input.RegisterKey(Settings.General.PanicPauseHotkey.Value);
        Settings.General.PanicPauseHotkey.OnValueChanged += () => { Input.RegisterKey(Settings.General.PanicPauseHotkey.Value); };
        Input.RegisterKey(Settings.Debug.AutoPartyHoverContextDumpHotkey.Value);
        Settings.Debug.AutoPartyHoverContextDumpHotkey.OnValueChanged += () => { Input.RegisterKey(Settings.Debug.AutoPartyHoverContextDumpHotkey.Value); };

        _autoParty = new AutoParty(this);
        _partyTeleport = new PartyTeleport(this);
        _partyChatCommands = new PartyChatCommands(this);
        _tradeInventoryDump = new TradeInventoryDump(this);
        _pickUpManager = new FollowerPickUpManager(this);
        _spikeProfiler = new SpikeProfiler(this);
        return base.Initialise();
    }

    public override void DrawSettings()
    {
        base.DrawSettings();
        DrawTradeDumpIgnoredCellsSettings();

        // DrawSettings can be opened before the plugin has been initialized/enabled.
        // Create the PickUp settings renderer lazily so the rule table is visible on a clean install too.
        _pickUpManager ??= new FollowerPickUpManager(this);
        _pickUpManager.DrawSettings();
    }

    private void DrawTradeDumpIgnoredCellsSettings()
    {
        Settings.TpTrade.TradeDumpIgnoredCells = InventoryGridIgnoreHelper.Normalize(Settings.TpTrade.TradeDumpIgnoredCells);
        var ignoredCells = Settings.TpTrade.TradeDumpIgnoredCells;

        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("TP / Trade - Dump ignored inventory slots"))
            return;

        ImGui.Text("Ignored Inventory Slots (checked = not dumped)");
        ImGui.TextDisabled("The dump command skips items whose top-left inventory cell is checked.");

        if (ImGui.Button("Clear dump ignored slots"))
        {
            Settings.TpTrade.TradeDumpIgnoredCells = new bool[InventoryGridIgnoreHelper.Rows, InventoryGridIgnoreHelper.Columns];
            ignoredCells = Settings.TpTrade.TradeDumpIgnoredCells;
        }

        ImGui.BeginChild("##FollowerTradeDumpIgnoredCellsMain", new Vector2(ImGui.GetContentRegionAvail().X, 204f), ImGuiChildFlags.Border,
            ImGuiWindowFlags.NoScrollWithMouse);

        for (var y = 0; y < InventoryGridIgnoreHelper.Rows; y++)
        {
            for (var x = 0; x < InventoryGridIgnoreHelper.Columns; x++)
            {
                var isCellIgnored = ignoredCells[y, x];
                if (ImGui.Checkbox($"##FollowerTradeDumpIgnoredCell_{y}_{x}", ref isCellIgnored))
                    ignoredCells[y, x] = isCellIgnored;

                if (x < InventoryGridIgnoreHelper.Columns - 1)
                    ImGui.SameLine();
            }
        }

        ImGui.EndChild();
    }


    internal long ProfileBegin(string scopeName) => _spikeProfiler?.Begin(scopeName) ?? 0L;

    internal void ProfileEnd(string scopeName, long startTimestamp) => _spikeProfiler?.End(scopeName, startTimestamp);

    internal IDisposable ProfileScope(string scopeName)
    {
        var start = ProfileBegin(scopeName);
        return new ProfileScopeToken(this, scopeName, start);
    }

    internal void ProfileFlushIfNeeded() => _spikeProfiler?.FlushIfNeeded();

    private sealed class ProfileScopeToken : IDisposable
    {
        private readonly Follower _owner;
        private readonly string _scopeName;
        private readonly long _start;
        private bool _disposed;

        public ProfileScopeToken(Follower owner, string scopeName, long start)
        {
            _owner = owner;
            _scopeName = scopeName;
            _start = start;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.ProfileEnd(_scopeName, _start);
        }
    }

    /// <summary>
    /// Clears all pathfinding values. Used on area transitions primarily.
    /// </summary>
    private void ResetPathing()
    {
        using var __profileScope = ProfileScope("Follower.ResetPathing");
        _tasks = new List<TaskNode>();
        _followTarget = null;
        _lastTargetPosition = Vector3.Zero;
        _lastPlayerPosition = Vector3.Zero;
        _areaTransitions = new Dictionary<uint, Entity>();
        _hasUsedWP = true;
        _lastAreaChangeAt = DateTime.Now;

        // Never carry held movement/sprint inputs across loading screens.
        ReleaseMovementKeyNow();
        ReleaseDodgeSprintKeyNow();
        _nextBotAction = DateTime.Now.AddMilliseconds(250);
        _nextMovementKeyTapAt = DateTime.Now.AddMilliseconds(MIN_MOVEMENT_KEY_TAP_GAP_MS);
        _nextCursorMoveAt = DateTime.MinValue;
        _lastCursorMoveTarget = Vector2.Zero;
        ResetPendingPortalClick();
        ResetPendingArenaTransitionClick();
        CancelLeaderPortalEntryRequest();
        ResetTaskWatchdog();
        SetRuntimeStatus("AreaChange", "pathing reset", "");
        _arenaTransitionRetrySuppressedUntil = DateTime.MinValue;
        _activeArenaTransitionIsAbyssSubArea = false;
    }

    public override void AreaChange(AreaInstance area)
    {
        using var __profileScope = ProfileScope("Follower.AreaChange.Total");
        ResetPathing();
        _pickUpManager?.Reset("AreaChange");

        //Load initial transitions!

        foreach (var transition in GameController.EntityListWrapper.Entities
            .Where(I =>
                I.Type == ExileCore2.Shared.Enums.EntityType.AreaTransition ||
                I.Type == ExileCore2.Shared.Enums.EntityType.Portal ||
                I.Type == ExileCore2.Shared.Enums.EntityType.TownPortal ||
                (!string.IsNullOrEmpty(I.RenderName) &&
                 I.RenderName.Contains("Atziri's Temple", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(I.Path) &&
                 (I.Path.Contains("Incursion/Objects/HubTransition") ||
                  IsMapCheckerPortalEntity(I) ||
                  I.Path.IndexOf(AbyssSubAreaTransitionMetadataPath, StringComparison.OrdinalIgnoreCase) >= 0)))
            .ToList())
        {
            if (!_areaTransitions.ContainsKey(transition.Id))
                _areaTransitions.Add(transition.Id, transition);
        }


        var terrain = GameController.IngameState.Data.Terrain;
        var terrainBytes = GameController.Memory.ReadBytes(terrain.LayerMelee.First, terrain.LayerMelee.ElementCount<byte>());
        _numCols = (int)(terrain.NumCols - 1) * 23;
        _numRows = (int)(terrain.NumRows - 1) * 23;
        if ((_numCols & 1) > 0)
            _numCols++;

        _tiles = new byte[_numCols, _numRows];
        int dataIndex = 0;
        for (int y = 0; y < _numRows; y++)
        {
            for (int x = 0; x < _numCols; x += 2)
            {
                var b = terrainBytes[dataIndex + (x >> 1)];
                _tiles[x, y] = (byte)((b & 0xf) > 0 ? 1 : 255);
                _tiles[x + 1, y] = (byte)((b >> 4) > 0 ? 1 : 255);
            }
            dataIndex += terrain.BytesPerRow;
        }

        terrainBytes = GameController.Memory.ReadBytes(terrain.LayerRanged.First, terrain.LayerRanged.ElementCount<byte>());
        _numCols = (int)(terrain.NumCols - 1) * 23;
        _numRows = (int)(terrain.NumRows - 1) * 23;
        if ((_numCols & 1) > 0)
            _numCols++;
        dataIndex = 0;
        for (int y = 0; y < _numRows; y++)
        {
            for (int x = 0; x < _numCols; x += 2)
            {
                var b = terrainBytes[dataIndex + (x >> 1)];

                var current = _tiles[x, y];
                if (current == 255)
                    _tiles[x, y] = (byte)((b & 0xf) > 3 ? 2 : 255);
                current = _tiles[x + 1, y];
                if (current == 255)
                    _tiles[x + 1, y] = (byte)((b >> 4) > 3 ? 2 : 255);
            }
            dataIndex += terrain.BytesPerRow;
        }


        if (Settings.Debug.DebugGeneratePngOnAreaChange.Value)
            GeneratePNG();
    }

    public void GeneratePNG()
    {
        using var __profileScope = ProfileScope("Follower.AreaChange.GeneratePNG");
        using (var img = new Bitmap(_numCols, _numRows))
        {
            for (int x = 0; x < _numCols; x++)
                for (int y = 0; y < _numRows; y++)
                {
                    try
                    {
                        var color = System.Drawing.Color.Black;
                        switch (_tiles[x, y])
                        {
                            case 1:
                                color = System.Drawing.Color.White;
                                break;
                            case 2:
                                color = System.Drawing.Color.Gray;
                                break;
                            case 255:
                                color = System.Drawing.Color.Black;
                                break;
                        }
                        img.SetPixel(x, y, color);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
            img.Save("output.png");
        }
    }



    private int SafeBotInputFrequencyMs()
    {
        try
        {
            return Math.Max(MIN_SAFE_BOT_INPUT_MS, Settings.General.BotInputFrequency.Value);
        }
        catch
        {
            return MIN_SAFE_BOT_INPUT_MS;
        }
    }

    private int SafeBotDelayMs(int multiplier = 1)
    {
        var safe = SafeBotInputFrequencyMs();
        var baseDelay = safe * Math.Max(1, multiplier);
        return baseDelay + random.Next(Math.Max(1, safe));
    }

    private bool TryMoveCursorForMovement(Vector2 screenPos)
    {
        using var __profileScope = ProfileScope("Follower.InputScheduler.TryMoveCursorForMovement");
        if (Mouse.IsGuardLocked) return false;

        var now = DateTime.Now;
        var distance = _lastCursorMoveTarget == Vector2.Zero
            ? float.MaxValue
            : Vector2.Distance(_lastCursorMoveTarget, screenPos);

        if (now < _nextCursorMoveAt && distance < MIN_CURSOR_MOVE_DISTANCE_PX)
            return false;

        Mouse.SetCursorPosHuman2(screenPos);
        _lastCursorMoveTarget = screenPos;
        _nextCursorMoveAt = now.AddMilliseconds(MIN_CURSOR_MOVE_GAP_MS);
        return true;
    }

    private void ProcessPendingInputReleases()
    {
        using var __profileScope = ProfileScope("Follower.InputScheduler.ProcessPendingReleases");
        var now = DateTime.Now;

        if (_movementKeyDownByPlugin && now >= _movementKeyReleaseAt)
        {
            Input.KeyUp(Settings.General.MovementKey);
            _movementKeyDownByPlugin = false;
        }

        if (_dodgeSprintTapDownByPlugin && now >= _dodgeSprintTapReleaseAt)
        {
            Input.KeyUp(Settings.General.DodgeSprintKey);
            _dodgeSprintTapDownByPlugin = false;
        }

        if (_escapeTapDownByPlugin && now >= _escapeTapReleaseAt)
        {
            Keyboard.KeyUp(Keys.Escape);
            _escapeTapDownByPlugin = false;
            _escapeTapReleaseAt = DateTime.MinValue;
        }

        if (_nextForcedInputReleaseAt != DateTime.MinValue && now >= _nextForcedInputReleaseAt)
        {
            _nextForcedInputReleaseAt = DateTime.MinValue;
            ReleaseAllPluginInputsNow(force: true, reason: "Follower.InputScheduler.ForcedPostActionRelease");
        }
    }

    private void QueueMovementKeyTap(int minHoldMs = 30, int randomExtraMs = 25)
    {
        using var __profileScope = ProfileScope("Follower.InputScheduler.QueueMovementKeyTap");
        var now = DateTime.Now;

        if (_movementKeyDownByPlugin || now < _nextMovementKeyTapAt)
            return;

        var holdMs = Math.Max(20, minHoldMs + random.Next(Math.Max(1, randomExtraMs)));
        Input.KeyDown(Settings.General.MovementKey);
        _movementKeyDownByPlugin = true;
        _movementKeyReleaseAt = now.AddMilliseconds(holdMs);

        // Separate from BotInputFrequency: this is the actual server-relevant move/action key cadence.
        _nextMovementKeyTapAt = now.AddMilliseconds(Math.Max(MIN_MOVEMENT_KEY_TAP_GAP_MS, holdMs + 60));
    }

    private void ReleaseMovementKeyNow(bool force = false)
    {
        using var __profileScope = ProfileScope("Follower.InputScheduler.ReleaseMovementKeyNow");
        var now = DateTime.Now;
        var shouldForceKeyUp = force && now >= _nextForcedMovementKeyUpAt;

        if (_movementKeyDownByPlugin || shouldForceKeyUp)
        {
            Input.KeyUp(Settings.General.MovementKey);
            if (force) _nextForcedMovementKeyUpAt = now.AddMilliseconds(MIN_FORCED_KEYUP_GAP_MS);
        }

        _movementKeyDownByPlugin = false;
        _movementKeyReleaseAt = DateTime.MinValue;
    }

    private void ReleaseDodgeSprintKeyNow(bool force = false)
    {
        using var __profileScope = ProfileScope("Follower.InputScheduler.ReleaseDodgeSprintKeyNow");
        var now = DateTime.Now;
        var shouldForceKeyUp = force && now >= _nextForcedDodgeSprintKeyUpAt;

        if (_dodgeSprintTapDownByPlugin || _sprintKeyDown || shouldForceKeyUp)
        {
            Input.KeyUp(Settings.General.DodgeSprintKey);
            if (force) _nextForcedDodgeSprintKeyUpAt = now.AddMilliseconds(MIN_FORCED_KEYUP_GAP_MS);
        }

        _dodgeSprintTapDownByPlugin = false;
        _sprintKeyDown = false;
        _dodgeSprintTapReleaseAt = DateTime.MinValue;
        _sprintFsmState = SprintFsm.Idle;
        _sprintHoldUntil = DateTime.MinValue;
        _sprintForceReleaseAt = DateTime.MinValue;
    }

    private void ReleaseEscapeKeyNow()
    {
        if (_escapeTapDownByPlugin)
        {
            Keyboard.KeyUp(Keys.Escape);
            _escapeTapDownByPlugin = false;
            _escapeTapReleaseAt = DateTime.MinValue;
        }
    }

    internal void ReleaseAllPluginInputsNow(bool force = false, string reason = "Follower.InputScheduler.ReleaseAllPluginInputsNow")
    {
        using var __profileScope = ProfileScope(reason);
        ReleaseMovementKeyNow(force);
        ReleaseDodgeSprintKeyNow(force);
        if (force) ReleaseEscapeKeyNow();
    }

    internal void PrepareForPluginMouseAction(string reason = "Follower.InputScheduler.PrepareForPluginMouseAction")
    {
        using var __profileScope = ProfileScope(reason);
        ReleaseAllPluginInputsNow(force: true, reason: reason + ".ReleaseBefore");
        ProcessPendingInputReleases();
    }

    internal void CompletePluginMouseAction(string reason = "Follower.InputScheduler.CompletePluginMouseAction")
    {
        using var __profileScope = ProfileScope(reason);
        ReleaseAllPluginInputsNow(force: true, reason: reason + ".ReleaseAfter");
        _nextForcedInputReleaseAt = DateTime.Now.AddMilliseconds(85);
    }

    internal bool TryMoveCursorForAutomation(Vector2 screenPos) => TryMoveCursorForMovement(screenPos);

    internal void QueueMovementKeyTapForAutomation(int minHoldMs = 30, int randomExtraMs = 25) => QueueMovementKeyTap(minHoldMs, randomExtraMs);

    internal Vector2 WorldToValidScreenPositionForAutomation(Vector3 worldPos) => WorldToValidScreenPosition(worldPos);

    // UI element rects (GetClientRect) are relative to the game window, but SetCursorPos needs screen coordinates.
    // Without the window offset, clicks only land correctly when the window sits at the desktop origin (fullscreen on the main monitor).
    internal Vector2 ClientToScreen(Vector2 clientPos) => clientPos + GameController.Window.GetWindowRectangleTimeCache.TopLeft;

    internal void DrawAutomationFrame(RectangleF rect, System.Drawing.Color color, int thickness) => Graphics.DrawFrame(rect, color, thickness);

    internal void SetFollowEnabledFromPartyChat(bool enabled, string leaderName, string commandText)
    {
        using var __profileScope = ProfileScope("Follower.PartyChatCommands.SetFollowEnabled");

        // Party-chat stop/start is intentionally kept as a volatile pause layer.
        // Do not persistently flip the main IsFollowEnabled setting on -p; otherwise
        // an old/stale chat line can leave the plugin disabled after reload.
        _pausedByPartyChatCommand = !enabled;

        if (enabled && !Settings.General.IsFollowEnabled.Value)
            Settings.General.IsFollowEnabled.SetValueNoEvent(true);

        _tasks.Clear();
        _pickUpManager?.Reset("PartyChatCommand");
        _followTarget = null;
        _lastTargetPosition = Vector3.Zero;
        _lastPlayerPosition = Vector3.Zero;
        _nextBotAction = DateTime.Now.AddMilliseconds(150);
        if (!enabled)
            CancelLeaderPortalEntryRequest();

        ReleaseAllPluginInputsNow(force: true, reason: enabled
            ? "Follower.PartyChatCommands.Start.Release"
            : "Follower.PartyChatCommands.Stop.Release");

        if (enabled)
            RequestLeaderPortalEntryIfUseful("PartyChatStart", clearExistingTasks: true);

        try
        {
            LogMessage($"PartyChatCommands: leader {leaderName} sent {commandText}; follow {(enabled ? "started" : "paused")}", 3);
        }
        catch { }
    }

    internal bool IsWholePluginPausedByPartyChat => _wholePluginPausedByPartyChatCommand;

    internal void SetPickUpEnabledFromPartyChat(bool enabled, string leaderName, string commandText)
    {
        using var __profileScope = ProfileScope("Follower.PartyChatCommands.SetPickUpEnabled");

        if (Settings?.PickUp?.Enabled == null)
            return;

        if (Settings.PickUp.Enabled.Value != enabled)
            Settings.PickUp.Enabled.SetValueNoEvent(enabled);

        _pickUpManager?.Reset(enabled ? "PartyChatPickUpStart" : "PartyChatPickUpPause");

        try
        {
            LogMessage($"PartyChatCommands: leader {leaderName} sent {commandText}; PickUp {(enabled ? "started" : "paused")}", 3);
        }
        catch { }
    }

    internal void SetWholePluginPausedFromPartyChat(bool paused, string leaderName, string commandText)
    {
        using var __profileScope = ProfileScope("Follower.PartyChatCommands.SetWholePluginPaused");

        _wholePluginPausedByPartyChatCommand = paused;

        if (!paused)
        {
            _pausedByPartyChatCommand = false;
            if (!Settings.General.IsFollowEnabled.Value)
                Settings.General.IsFollowEnabled.SetValueNoEvent(true);
        }

        _tasks.Clear();
        _pickUpManager?.Reset("PartyChatWholePluginCommand");
        _followTarget = null;
        _lastTargetPosition = Vector3.Zero;
        _lastPlayerPosition = Vector3.Zero;
        _nextBotAction = DateTime.Now.AddMilliseconds(150);
        if (paused)
            CancelLeaderPortalEntryRequest();

        ReleaseAllPluginInputsNow(force: true, reason: paused
            ? "Follower.PartyChatCommands.PluginPause.ReleaseBeforeEsc"
            : "Follower.PartyChatCommands.PluginResume.ReleaseBeforeEsc");

        TapEscapeForPartyCommand(paused
            ? "Follower.PartyChatCommands.PluginPause.Escape"
            : "Follower.PartyChatCommands.PluginResume.Escape");

        // ESC is released asynchronously by ProcessPendingInputReleases; keep movement/sprint released
        // without cancelling the queued ESC tap in the same Render frame.
        ReleaseMovementKeyNow(force: true);
        ReleaseDodgeSprintKeyNow(force: true);

        if (!paused)
            RequestLeaderPortalEntryIfUseful("PartyChatPluginResume", clearExistingTasks: true);

        try
        {
            LogMessage($"PartyChatCommands: leader {leaderName} sent {commandText}; whole plugin {(paused ? "paused" : "resumed")} + ESC", 3);
        }
        catch { }
    }

    internal void ForcePortalEntryFromPartyChat(string leaderName, string commandText)
    {
        using var __profileScope = ProfileScope("Follower.PartyChatCommands.ForcePortalEntry");

        _wholePluginPausedByPartyChatCommand = false;
        _pausedByPartyChatCommand = false;

        if (!Settings.General.IsFollowEnabled.Value)
            Settings.General.IsFollowEnabled.SetValueNoEvent(true);

        _followTarget = null;
        _lastPlayerPosition = Vector3.Zero;
        _nextBotAction = DateTime.Now.AddMilliseconds(80);
        _pickUpManager?.Reset("PartyChatContinuePortalCommand");
        ReleaseAllPluginInputsNow(force: true, reason: "Follower.PartyChatCommands.ContinuePortal.Release");

        var queued = RequestLeaderPortalEntryIfUseful("PartyChatContinuePortal", clearExistingTasks: true);

        try
        {
            LogMessage($"PartyChatCommands: leader {leaderName} sent {commandText}; portal entry {(queued ? "queued" : "requested")}", 3);
        }
        catch { }
    }

    private bool RequestLeaderPortalEntryIfUseful(string source, bool clearExistingTasks)
    {
        using var __profileScope = ProfileScope("Follower.Portal.RequestLeaderPortalEntry");

        if (!IsInHideout() && !IsInAtziriEntranceArea())
        {
            CancelLeaderPortalEntryRequest();
            return false;
        }

        if (TryQueueBestKnownPortalTransitionTask(source, clearExistingTasks))
        {
            CancelLeaderPortalEntryRequest();
            return true;
        }

        var now = DateTime.Now;
        _leaderPortalEntryRequested = true;
        _leaderPortalEntryRequestExpiresAt = now.AddMilliseconds(LeaderPortalEntryRequestTtlMs);
        _nextLeaderPortalEntryRequestScanAt = DateTime.MinValue;
        SetRuntimeStatus("Portal", "waiting for visible portal", source);
        return false;
    }

    private bool TryProcessPendingLeaderPortalEntryRequest()
    {
        if (!_leaderPortalEntryRequested)
            return false;

        var now = DateTime.Now;
        if (now > _leaderPortalEntryRequestExpiresAt)
        {
            CancelLeaderPortalEntryRequest();
            SetRuntimeStatus("Portal", "portal request expired", "no visible portal found");
            return false;
        }

        if (now < _nextLeaderPortalEntryRequestScanAt)
            return false;

        _nextLeaderPortalEntryRequestScanAt = now.AddMilliseconds(LeaderPortalEntryRequestScanMs);

        if (!IsInHideout() && !IsInAtziriEntranceArea())
        {
            CancelLeaderPortalEntryRequest();
            return false;
        }

        if (!TryQueueBestKnownPortalTransitionTask("PendingLeaderPortalEntry", clearExistingTasks: true))
            return false;

        CancelLeaderPortalEntryRequest();
        return true;
    }

    private void CancelLeaderPortalEntryRequest()
    {
        _leaderPortalEntryRequested = false;
        _leaderPortalEntryRequestExpiresAt = DateTime.MinValue;
        _nextLeaderPortalEntryRequestScanAt = DateTime.MinValue;
    }

    private bool TryQueueBestKnownPortalTransitionTask(string source, bool clearExistingTasks)
    {
        using var __profileScope = ProfileScope("Follower.Portal.TryQueueBestKnownPortalTransitionTask");

        PortalTarget portalTarget = null;
        if (IsInHideout())
            portalTarget = ResolveBestHideoutPortalTarget();
        else if (IsInAtziriEntranceArea())
            portalTarget = ToPortalTarget(FindNearestMapCheckerPortal(requireTargetable: false));

        if (portalTarget != null)
            return QueuePortalTransitionTask(portalTarget.WorldPosition, source, clearExistingTasks);

        var fallback = FindBestKnownPortalTransitionEntity();
        return fallback != null && QueuePortalTransitionTask(fallback.Pos, source, clearExistingTasks);
    }

    private Entity FindBestKnownPortalTransitionEntity()
    {
        using var __profileScope = ProfileScope("Follower.Portal.FindBestKnownPortalTransitionEntity");

        var playerPos = GameController.Player?.Pos ?? Vector3.Zero;
        var anchor = _lastTargetPosition != Vector3.Zero ? _lastTargetPosition : playerPos;

        return _areaTransitions.Values
            .Where(IsRelevantLeaderPortalTransitionEntity)
            .OrderBy(entity => Vector3.Distance(anchor, entity.Pos))
            .FirstOrDefault();
    }

    private bool IsRelevantLeaderPortalTransitionEntity(Entity entity)
    {
        if (entity == null)
            return false;

        if (IsInHideout())
        {
            return IsMapCheckerPortalEntity(entity) ||
                   entity.Type == ExileCore2.Shared.Enums.EntityType.Portal ||
                   entity.Type == ExileCore2.Shared.Enums.EntityType.TownPortal;
        }

        if (!IsInAtziriEntranceArea())
            return false;

        return IsMapCheckerPortalEntity(entity) ||
               (!string.IsNullOrEmpty(entity.RenderName) &&
                entity.RenderName.Contains("Atziri's Temple", StringComparison.OrdinalIgnoreCase));
    }

    private bool QueuePortalTransitionTask(Vector3 worldPosition, string source, bool clearExistingTasks)
    {
        if (worldPosition == Vector3.Zero)
            worldPosition = GameController.Player?.Pos ?? Vector3.Zero;

        if (worldPosition == Vector3.Zero)
            return false;

        if (clearExistingTasks)
            _tasks.Clear();

        _tasks.Add(new TaskNode(
            worldPosition,
            Settings.General.PathfindingNodeDistance.Value,
            TaskNode.TaskNodeType.Transition));

        ResetPendingPortalClick();
        ResetTaskWatchdog();
        _nextBotAction = DateTime.Now.AddMilliseconds(80);
        SetRuntimeStatus("Portal", "transition task queued", source);
        return true;
    }

    private void TapEscapeForPartyCommand(string reason)
    {
        using var __profileScope = ProfileScope(reason);
        try
        {
            var now = DateTime.Now;
            Keyboard.KeyUp(Keys.Escape);
            Keyboard.KeyDown(Keys.Escape);
            _escapeTapDownByPlugin = true;
            _escapeTapReleaseAt = now.AddMilliseconds(45);
            _nextForcedInputReleaseAt = now.AddMilliseconds(110);
        }
        catch { }
    }

    internal void StartTradeInventoryDumpFromPartyChat(string leaderName, string commandText)
    {
        using var __profileScope = ProfileScope("Follower.TradeInventoryDump.StartFromPartyChat");
        if (_wholePluginPausedByPartyChatCommand)
            return;
        _tradeInventoryDump?.Start(leaderName, commandText);
    }

    private void QueueDodgeSprintTap(int holdMs = 25)
    {
        using var __profileScope = ProfileScope("Follower.InputScheduler.QueueDodgeSprintTap");
        var now = DateTime.Now;
        if (_dodgeSprintTapDownByPlugin || _sprintKeyDown || now < _nextSprintAllowed)
            return;

        Input.KeyDown(Settings.General.DodgeSprintKey);
        _dodgeSprintTapDownByPlugin = true;
        _dodgeSprintTapReleaseAt = now.AddMilliseconds(Math.Max(1, holdMs));
        _nextSprintAllowed = now.AddMilliseconds(Math.Max(500, Settings.General.SprintRetriggerCooldownMs.Value));
    }

    private void MouseoverItem(Entity item)
    {
        using var __profileScope = ProfileScope("Follower.Task.Loot.MouseoverItem");
        var uiLoot = GameController.IngameState.IngameUi.ItemsOnGroundLabels.FirstOrDefault(I => I.IsVisible && I.ItemOnGround.Id == item.Id);
        if (uiLoot != null)
        {
            var clickPos = uiLoot.Label.GetClientRect().Center;
            Mouse.SetCursorPos(ClientToScreen(new Vector2(
                clickPos.X + random.Next(-15, 15),
                clickPos.Y + random.Next(-10, 10))));
            _nextBotAction = DateTime.Now.AddMilliseconds(30 + random.Next(SafeBotInputFrequencyMs()));
        }
    }

    public override void Render()
    {
        var __renderProfileStart = _spikeProfiler?.Begin("Render.Total") ?? 0L;
        try
        {
ProcessPendingInputReleases();
//Dont run logic if we're dead!
if (!GameController.Player.IsAlive)
{
    ReleaseAllPluginInputsNow(reason: "Follower.EarlyReturn.PlayerDead.Release");
    return;
}

// Hotkey toggle (pause the whole plugin logic)
var __toggleProfileStart = _spikeProfiler?.Begin("Option.ToggleFollower/IsFollowEnabled") ?? 0L;
try
{
if (Settings.General.ToggleFollower.PressedOnce())
{
    var nextFollowEnabled = !Settings.General.IsFollowEnabled.Value;
    Settings.General.IsFollowEnabled.SetValueNoEvent(nextFollowEnabled);
    if (nextFollowEnabled)
    {
        _pausedByPartyChatCommand = false;
        _wholePluginPausedByPartyChatCommand = false;
    }
    _tasks = new List<TaskNode>();
    _pickUpManager?.Reset("Toggle");
    ReleaseAllPluginInputsNow(force: true, reason: "Follower.Toggle.Release");
}
}
finally { _spikeProfiler?.End("Option.ToggleFollower/IsFollowEnabled", __toggleProfileStart); }

if (Settings.General.PanicPauseHotkey.PressedOnce())
{
    _wholePluginPausedByPartyChatCommand = true;
    _pausedByPartyChatCommand = true;
    _tasks.Clear();
    ResetTaskWatchdog();
    ResetPendingPortalClick();
    ResetPendingArenaTransitionClick();
    CancelLeaderPortalEntryRequest();
    _pickUpManager?.Reset("PanicPauseHotkey");
    ReleaseAllPluginInputsNow(force: true, reason: "Follower.PanicPauseHotkey.Release");
    SetRuntimeStatus("Panic", "Whole plugin paused and all plugin inputs released", "");
    try { LogMessage("Follower: panic pause hotkey pressed; plugin paused and inputs released.", 5); } catch { }
    return;
}

var __autoPartyDebugHotkeyProfileStart = _spikeProfiler?.Begin("Option.AutoPartyHoverContextDumpHotkey") ?? 0L;
try { _autoParty?.TickDebugHotkey(); }
finally { _spikeProfiler?.End("Option.AutoPartyHoverContextDumpHotkey", __autoPartyDebugHotkeyProfileStart); }

var __partyChatCommandsProfileStart = _spikeProfiler?.Begin("Option.PartyChatLeaderCommands") ?? 0L;
try { _partyChatCommands?.Tick(); }
finally { _spikeProfiler?.End("Option.PartyChatLeaderCommands", __partyChatCommandsProfileStart); }

if (_wholePluginPausedByPartyChatCommand && (Settings.PartyChatLeaderCommands.Enabled?.Value ?? false))
{
    ReleaseAllPluginInputsNow(reason: "Follower.EarlyReturn.PartyChatWholePluginPaused.Release");
    return;
}

var __tradeDumpProfileStart = _spikeProfiler?.Begin("Option.TradeInventoryDump") ?? 0L;
try
{
    if (_tradeInventoryDump?.Tick() == true)
    {
        SetRuntimeStatus("TradeDump", _tradeInventoryDump.Status, "");
        return;
    }
}
finally { _spikeProfiler?.End("Option.TradeInventoryDump", __tradeDumpProfileStart); }

if (!Settings.General.IsFollowEnabled.Value)
{
    ReleaseAllPluginInputsNow(reason: "Follower.EarlyReturn.FollowDisabled.Release");
    return;
}

if (_pausedByPartyChatCommand && (Settings.PartyChatLeaderCommands.Enabled?.Value ?? false))
{
    ReleaseAllPluginInputsNow(reason: "Follower.EarlyReturn.PartyChatPaused.Release");
    return;
}

// Optional safety: when inventory is open, do not move/click/teleport.
var __inventoryProfileStart = _spikeProfiler?.Begin("Option.PauseWhenInventoryOpen") ?? 0L;
try
{
if (IsInventoryOpen())
{
    ReleaseAllPluginInputsNow(force: true, reason: "Follower.EarlyReturn.InventoryOpen.Release");
    return;
}
}
finally { _spikeProfiler?.End("Option.PauseWhenInventoryOpen", __inventoryProfileStart); }

var __autoPartyProfileStart = _spikeProfiler?.Begin("Option.AutoAcceptParty/AutoAcceptTrade") ?? 0L;
try
{
    if (_autoParty?.Tick() == true)
    {
        SetRuntimeStatus("AutoParty", _autoParty.Status, "");
        return;
    }
}
finally { _spikeProfiler?.End("Option.AutoAcceptParty/AutoAcceptTrade", __autoPartyProfileStart); }

var __partyTeleportProfileStart = _spikeProfiler?.Begin("Option.TeleportToLeader") ?? 0L;
try
{
    if (_partyTeleport?.Tick() == true)
    {
        SetRuntimeStatus("PartyTeleport", _partyTeleport.Status, "");
        return;
    }
}
finally { _spikeProfiler?.End("Option.TeleportToLeader", __partyTeleportProfileStart); }

var __pickUpProfileStart = _spikeProfiler?.Begin("Option.PickUp") ?? 0L;
try
{
    if (_pickUpManager?.Tick() == true)
    {
        SetRuntimeStatus("PickUp", _pickUpManager.Status, "");
        return;
    }
}
finally { _spikeProfiler?.End("Option.PickUp", __pickUpProfileStart); }

//Cache the current follow target (if present)
        var __targetPlanningProfileStart = _spikeProfiler?.Begin("Option.LeaderName/Pathfinding/CloseFollow/QuestLoot") ?? 0L;
        try
        {
        var __leaderLookupProfileStart = ProfileBegin("Follower.Target.GetFollowingTarget");
        try { _followTarget = GetFollowingTarget(); }
        finally { ProfileEnd("Follower.Target.GetFollowingTarget", __leaderLookupProfileStart); }

        // Boss arena entrances can stay visible while the leader is still visible on the
        // same map. Scan/click the ground-label transition before normal follow planning
        // so following movement cannot override the Arena click.
        var arenaTransitionQueued = TryQueueArenaTransitionTask();
        var leaderCommandPortalQueued = !arenaTransitionQueued && TryProcessPendingLeaderPortalEntryRequest();

        if (!arenaTransitionQueued && !leaderCommandPortalQueued && _followTarget != null)
        {
            var distanceFromFollower = Vector3.Distance(GameController.Player.Pos, _followTarget.Pos);
            //We are NOT within clear path distance range of leader. Logic can continue
            if (distanceFromFollower >= Settings.General.ClearPathDistance.Value)
            {
                //Leader moved VERY far in one frame. Check for transition to use to follow them.
                var distanceMoved = Vector3.Distance(_lastTargetPosition, _followTarget.Pos);
                if (_lastTargetPosition != Vector3.Zero && distanceMoved > Settings.General.ClearPathDistance.Value)
                {
                    var transition = _areaTransitions.Values.OrderBy(I => Vector3.Distance(_lastTargetPosition, I.Pos)).FirstOrDefault();
                    if (transition != null && Vector3.Distance(_lastTargetPosition, transition.Pos) < Settings.General.ClearPathDistance.Value)
                        _tasks.Add(new TaskNode(transition.Pos, 200, TaskNode.TaskNodeType.Transition));
                }
                //We have no path, set us to go to leader pos.
                else if (_tasks.Count == 0)
                    _tasks.Add(new TaskNode(_followTarget.Pos, Settings.General.PathfindingNodeDistance));
                //We have a path. Check if the last task is far enough away from current one to add a new task node.
                else
                {
                    var distanceFromLastTask = Vector3.Distance(_tasks.Last().WorldPosition, _followTarget.Pos);
                    if (distanceFromLastTask >= Settings.General.PathfindingNodeDistance)
                        _tasks.Add(new TaskNode(_followTarget.Pos, Settings.General.PathfindingNodeDistance));
                }
            }
            else
            {
                //Clear all tasks except for looting/claim portal (as those only get done when we're within range of leader. 
                if (_tasks.Count > 0)
                {
                    for (var i = _tasks.Count - 1; i >= 0; i--)
                        if (_tasks[i].Type == TaskNode.TaskNodeType.Movement || _tasks[i].Type == TaskNode.TaskNodeType.Transition)
                            _tasks.RemoveAt(i);
                }
                else if (Settings.General.IsCloseFollowEnabled.Value)
                {
                    //Close follow logic. We have no current tasks. Check if we should move towards leader
                    if (distanceFromFollower >= Settings.General.PathfindingNodeDistance.Value)
                        _tasks.Add(new TaskNode(_followTarget.Pos, Settings.General.PathfindingNodeDistance));
                }

                //Check if we should add quest loot logic. We're close to leader already
                var __questLootLookupProfileStart = ProfileBegin("Follower.Target.GetLootableQuestItem");
                Entity questLoot;
                try { questLoot = GetLootableQuestItem(); }
                finally { ProfileEnd("Follower.Target.GetLootableQuestItem", __questLootLookupProfileStart); }
                if (questLoot != null &&
                    Vector3.Distance(GameController.Player.Pos, questLoot.Pos) < Settings.General.ClearPathDistance.Value &&
                    _tasks.FirstOrDefault(I => I.Type == TaskNode.TaskNodeType.Loot) == null)
                    _tasks.Add(new TaskNode(questLoot.Pos, Settings.General.ClearPathDistance, TaskNode.TaskNodeType.Loot));

                else if (!_hasUsedWP)
                {
                    //Check if there's a waypoint nearby
                    var waypoint = GameController.EntityListWrapper.Entities.SingleOrDefault(I => I.Type == ExileCore2.Shared.Enums.EntityType.Waypoint &&
                        Vector3.Distance(GameController.Player.Pos, I.Pos) < Settings.General.ClearPathDistance);

                    if (waypoint != null)
                    {
                        _hasUsedWP = true;
                        _tasks.Add(new TaskNode(waypoint.Pos, Settings.General.ClearPathDistance, TaskNode.TaskNodeType.ClaimWaypoint));
                    }

                }

            }
            _lastTargetPosition = _followTarget.Pos;
        }
        //Leader is null but we have tracked them this map.
        //Try using transition to follow them to their map
        else if (_tasks.Count == 0 &&
            _lastTargetPosition != Vector3.Zero &&
            (IsInHideout() || IsInAtziriEntranceArea()))
        {

            
            // Preferred path: use the visible portal label and its metadata, because MTX portals often do
            // not expose stable Portal/TownPortal entity types. Fall back to entity metadata for older builds.
            var directPortalLabel = IsInHideout() ? FindNearestVisibleHideoutPortalLabel() : null;
            var directPortal = directPortalLabel == null ? FindNearestMapCheckerPortal(requireTargetable: false) : null;
            if (directPortalLabel != null || directPortal != null)
            {
                var portalPosition = directPortalLabel?.WorldPosition ?? directPortal.Pos;
                if (portalPosition == Vector3.Zero)
                    portalPosition = GameController.Player.Pos;

                _tasks.Clear();
                _tasks.Add(new TaskNode(portalPosition,
                    Settings.General.PathfindingNodeDistance.Value,
                    TaskNode.TaskNodeType.Transition));
            }
            else
            {
                var transOptions = _areaTransitions.Values
                    .Where(I => Vector3.Distance(_lastTargetPosition, I.Pos) < Settings.General.ClearPathDistance)
                    .OrderBy(I => Vector3.Distance(_lastTargetPosition, I.Pos))
                    .ToArray();

                // Fallback for special cases like the Atziri's Temple entrance in Vaal Ruins:
                // if we didn't find any transition near the last leader position, explicitly
                // look for an "Atziri's Temple" transition and use that instead.
                if (transOptions.Length == 0)
                {
                    transOptions = _areaTransitions.Values
                        .Where(I => !string.IsNullOrEmpty(I.RenderName) &&
                                    I.RenderName.Contains("Atziri's Temple", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(I => Vector3.Distance(GameController.Player.Pos, I.Pos))
                        .ToArray();
                }

                if (transOptions.Length > 0)
                    _tasks.Add(new TaskNode(transOptions[0].Pos,
                        Settings.General.PathfindingNodeDistance.Value,
                        TaskNode.TaskNodeType.Transition));
            }

        }
        }
        finally { _spikeProfiler?.End("Option.LeaderName/Pathfinding/CloseFollow/QuestLoot", __targetPlanningProfileStart); }


        if (_tasks.Count == 0)
            ReleaseAllPluginInputsNow(reason: "Follower.NoTasks.Release");

        //We have our tasks, now we need to perform in game logic with them.
        if (DateTime.Now > _nextBotAction && _tasks.Count > 0)
        {
            var currentTask = _tasks.First();
            var taskDistance = Vector3.Distance(GameController.Player.Pos, currentTask.WorldPosition);
            var playerDistanceMoved = Vector3.Distance(GameController.Player.Pos, _lastPlayerPosition);

            //We are using a same map transition and have moved significnatly since last tick. Mark the transition task as done.
            if (currentTask.Type == TaskNode.TaskNodeType.Transition &&
                playerDistanceMoved >= Settings.General.ClearPathDistance.Value)
            {
                _tasks.RemoveAt(0);
                if (_tasks.Count > 0)
                    currentTask = _tasks.First();
                else
                {
                    _lastPlayerPosition = GameController.Player.Pos;
                    ReleaseAllPluginInputsNow(reason: "Follower.TaskListEmpty.Release");
                    return;
}
            }

            if (ShouldResetCurrentTaskForStuck(currentTask, taskDistance, playerDistanceMoved))
            {
                SetRuntimeStatus("AntiStuck", $"reset {currentTask.Type} after no progress", $"distance={taskDistance:0}");
                ResetPendingPortalClick();
                ResetPendingArenaTransitionClick();
                _tasks.RemoveAt(0);
                _watchdogResetCount++;
                ResetTaskWatchdog();
                ReleaseAllPluginInputsNow(force: true, reason: "Follower.AntiStuck.Release");
                _nextBotAction = DateTime.Now.AddMilliseconds(SafeBotInputFrequencyMs() * 2);
                return;
            }

            var __taskProfileName = "TaskExecution." + currentTask.Type + "/BotInputFrequency/MovementKey";
            if (currentTask.Type == TaskNode.TaskNodeType.Movement) __taskProfileName += "/Sprint";
            var __taskProfileStart = _spikeProfiler?.Begin(__taskProfileName) ?? 0L;
            try
            {
            switch (currentTask.Type)
            {
                case TaskNode.TaskNodeType.Movement:
                    SetRuntimeStatus("Follow", $"moving to task, distance={taskDistance:0}", $"tasks={_tasks.Count}");
                    _nextBotAction = DateTime.Now.AddMilliseconds(SafeBotDelayMs());

                    var __sprintProfileStart = ProfileBegin("Follower.Sprint.UpdateDodgeSprintFSM");
                    try { UpdateDodgeSprintFSM(); }
                    finally { ProfileEnd("Follower.Sprint.UpdateDodgeSprintFSM", __sprintProfileStart); }
if (false && CheckDashTerrain(currentTask.WorldPosition))
                        return;
                    var __movementScreenProfileStart = ProfileBegin("Follower.Task.Movement.WorldToScreen");
                    Vector2 __movementScreenPos;
                    try { __movementScreenPos = WorldToValidScreenPosition(currentTask.WorldPosition); }
                    finally { ProfileEnd("Follower.Task.Movement.WorldToScreen", __movementScreenProfileStart); }
                    var __movementMouseProfileStart = ProfileBegin("Follower.Task.Movement.MouseMove");
                    try { TryMoveCursorForMovement(__movementScreenPos); }
                    finally { ProfileEnd("Follower.Task.Movement.MouseMove", __movementMouseProfileStart); }
                    var __movementInputProfileStart = ProfileBegin("Follower.Task.Movement.InputNonBlockingKeyTap");
                    try
                    {
                        QueueMovementKeyTap(30, 25);
                    }
                    finally { ProfileEnd("Follower.Task.Movement.InputNonBlockingKeyTap", __movementInputProfileStart); }

                    //Within bounding range. Task is complete
                    //Note: Was getting stuck on close objects... testing hacky fix.
                    if (taskDistance <= Settings.General.PathfindingNodeDistance.Value * 1.5)
                        _tasks.RemoveAt(0);
                    break;
                case TaskNode.TaskNodeType.Loot:
                    {
                        SetRuntimeStatus("QuestLoot", $"distance={taskDistance:0}", $"attempts={currentTask.AttemptCount}");
                        _nextBotAction = DateTime.Now.AddMilliseconds(SafeBotDelayMs());
                        currentTask.AttemptCount++;
                        var __lootTaskLookupProfileStart = ProfileBegin("Follower.Task.Loot.GetLootableQuestItem");
                        Entity questLoot;
                        try { questLoot = GetLootableQuestItem(); }
                        finally { ProfileEnd("Follower.Task.Loot.GetLootableQuestItem", __lootTaskLookupProfileStart); }
                        if (questLoot == null
                            || currentTask.AttemptCount > 2
                            || Vector3.Distance(GameController.Player.Pos, questLoot.Pos) >= Settings.General.ClearPathDistance.Value)
                        {
                            _tasks.RemoveAt(0);
                            break;
                        }

                        PrepareForPluginMouseAction("Follower.Task.Loot.PreClickRelease");
                        _nextBotAction = DateTime.Now.AddMilliseconds(SafeBotInputFrequencyMs());
                        // Give movement time to settle without blocking Render.
                        var targetInfo = questLoot.GetComponent<Targetable>();
                        if (!targetInfo.isTargeted)
                            MouseoverItem(questLoot);
                        if (targetInfo.isTargeted)
                        {
                            PrepareForPluginMouseAction("Follower.Task.Loot.Click.Prepare");
                            Mouse.LeftMouseDown();
                            Mouse.LeftMouseUp();
                            CompletePluginMouseAction("Follower.Task.Loot.Click.Complete");
                            _nextBotAction = DateTime.Now.AddSeconds(1);
                        }

                        break;
                    }
                case TaskNode.TaskNodeType.Transition:
                    {
                        SetRuntimeStatus("Transition", $"distance={taskDistance:0}", "resolving portal/transition");
                        _nextBotAction = DateTime.Now.AddMilliseconds(SafeBotDelayMs(2));

                        // Re-resolve the portal every tick. Visible UI labels are preferred; entity metadata is
                        // the compatibility fallback for non-MTX/older ExileCore2 builds.
                        var portalTarget = IsInHideout()
                            ? ResolveBestHideoutPortalTarget()
                            : IsInAtziriEntranceArea()
                                ? ToPortalTarget(FindNearestMapCheckerPortal(requireTargetable: false))
                                : null;

                        var targetWorld = portalTarget?.WorldPosition ?? currentTask.WorldPosition;
                        if (targetWorld == Vector3.Zero)
                            targetWorld = GameController.Player.Pos;

                        currentTask.WorldPosition = targetWorld;
                        taskDistance = Vector3.Distance(GameController.Player.Pos, targetWorld);

                        Vector2 screenPos;
                        if (portalTarget != null && portalTarget.ClickPosition != Vector2.Zero)
                        {
                            screenPos = portalTarget.ClickPosition;
                        }
                        else
                        {
                            var __transitionScreenProfileStart = ProfileBegin("Follower.Task.Transition.WorldToScreen");
                            try { screenPos = WorldToValidScreenPosition(targetWorld); }
                            finally { ProfileEnd("Follower.Task.Transition.WorldToScreen", __transitionScreenProfileStart); }
                        }

                        if (taskDistance <= Settings.General.ClearPathDistance.Value)
                        {
                            // Hover first, then click after a short delay so the game registers the portal under cursor.
                            if (portalTarget != null)
                            {
                                if (TryHoverThenClickPortal(portalTarget, screenPos))
                                {
                                    _nextBotAction = _portalHoverClickAt == DateTime.MinValue
                                        ? DateTime.Now.AddSeconds(1)
                                        : DateTime.Now.AddMilliseconds(PortalHoverDelayMs);
                                    currentTask.AttemptCount++;
                                }
                            }
                            else
                            {
                                PrepareForPluginMouseAction("Follower.Task.Transition.Click.Prepare");
                                if (!Mouse.IsGuardLocked) Mouse.SetCursorPosAndLeftClickHuman(screenPos, 0);
                                CompletePluginMouseAction("Follower.Task.Transition.Click.Complete");
                                _nextBotAction = DateTime.Now.AddSeconds(1);
                                currentTask.AttemptCount++;
                            }
                        }
                        else
                        {
                            // Walk toward the actual portal entity if it is not close enough to click yet.
                            ResetPendingPortalClick();
                            var __transitionInputProfileStart = ProfileBegin("Follower.Task.Transition.MouseInputNonBlockingKeyTap");
                            try
                            {
                                TryMoveCursorForMovement(screenPos);
                                QueueMovementKeyTap(30, 25);
                            }
                            finally { ProfileEnd("Follower.Task.Transition.MouseInputNonBlockingKeyTap", __transitionInputProfileStart); }
                            currentTask.AttemptCount++;
                        }

                        if (currentTask.AttemptCount > 6)
                        {
                            ResetPendingPortalClick();
                            _tasks.RemoveAt(0);
                        }
                        break;
                    }

                case TaskNode.TaskNodeType.ArenaTransition:
                    {
                        SetRuntimeStatus("ArenaTransition", $"distance={taskDistance:0}", "visible label/entity transition");
                        _nextBotAction = DateTime.Now.AddMilliseconds(SafeBotDelayMs(2));

                        var target = _activeArenaTransitionIsAbyssSubArea
                            ? FindAbyssSubAreaTransitionEntityTarget()
                            : FindVisibleArenaTransitionLabel();
                        if (target == null)
                        {
                            ResetPendingArenaTransitionClick();
                            _activeArenaTransitionIsAbyssSubArea = false;
                            _tasks.RemoveAt(0);
                            break;
                        }

                        currentTask.WorldPosition = target.WorldPosition != Vector3.Zero
                            ? target.WorldPosition
                            : GameController.Player.Pos;

                        if (TryHoverThenClickArenaTransition(target))
                        {
                            _nextBotAction = _arenaTransitionHoverClickAt == DateTime.MinValue
                                ? DateTime.Now.AddSeconds(1)
                                : DateTime.Now.AddMilliseconds(ArenaTransitionHoverDelayMs);
                        }

                        currentTask.AttemptCount++;
                        if (currentTask.AttemptCount > Math.Max(1, Settings.Transition.ArenaTransitionMaxRetries.Value * 2))
                        {
                            ResetPendingArenaTransitionClick();
                            _activeArenaTransitionIsAbyssSubArea = false;
                            _tasks.RemoveAt(0);
                            _arenaTransitionRetrySuppressedUntil = DateTime.Now.AddMilliseconds(Math.Max(500, Settings.Transition.ArenaTransitionRetryCooldownMs.Value));
                        }
                        break;
                    }

                case TaskNode.TaskNodeType.ClaimWaypoint:
                    {
                        SetRuntimeStatus("Waypoint", $"distance={taskDistance:0}", "claim waypoint");
                        if (Vector3.Distance(GameController.Player.Pos, currentTask.WorldPosition) > 150)
                        {
                            var __waypointScreenProfileStart = ProfileBegin("Follower.Task.ClaimWaypoint.WorldToScreen");
                            Vector2 screenPos;
                            try { screenPos = WorldToValidScreenPosition(currentTask.WorldPosition); }
                            finally { ProfileEnd("Follower.Task.ClaimWaypoint.WorldToScreen", __waypointScreenProfileStart); }
                            PrepareForPluginMouseAction("Follower.Task.ClaimWaypoint.Click.Prepare");
                            _nextBotAction = DateTime.Now.AddMilliseconds(SafeBotInputFrequencyMs());
                            if (!Mouse.IsGuardLocked) Mouse.SetCursorPosAndLeftClickHuman(screenPos, 0);
                            CompletePluginMouseAction("Follower.Task.ClaimWaypoint.Click.Complete");
                            _nextBotAction = DateTime.Now.AddSeconds(1);
                        }
                        currentTask.AttemptCount++;
                        if (currentTask.AttemptCount > 3)
                            _tasks.RemoveAt(0);
                        break;
                    }
            }
            }
            finally { _spikeProfiler?.End(__taskProfileName, __taskProfileStart); }
        }
        _lastPlayerPosition = GameController.Player.Pos;
        if (Settings.Debug.DrawPathAndTransitions.Value)
            DrawPath();
        return;
        }
        finally
        {
            DrawRuntimeOverlaySafe();
            _spikeProfiler?.End("Render.Total", __renderProfileStart);
            _spikeProfiler?.FlushIfNeeded();
        }
    }

    
    
    private void SetRuntimeStatus(string mode, string detail = "", string target = "")
    {
        _runtimeMode = string.IsNullOrWhiteSpace(mode) ? "Idle" : mode;
        _runtimeDetail = detail ?? string.Empty;
        _runtimeTarget = target ?? string.Empty;
        _runtimeStatusAt = DateTime.Now;
    }

    private void DrawRuntimeOverlaySafe()
    {
        try
        {
            if (!(Settings.Debug.ShowRuntimeOverlay?.Value ?? false))
                return;

            var x = Settings.Debug.RuntimeOverlayX.Value;
            var y = Settings.Debug.RuntimeOverlayY.Value;
            var line = 0;
            void DrawLine(string text)
            {
                Graphics.DrawText(text ?? string.Empty, new Vector2(x, y + line * 18));
                line++;
            }

            DrawLine($"Follower mode: {_runtimeMode}");
            if (!string.IsNullOrWhiteSpace(_runtimeDetail)) DrawLine($"Detail: {_runtimeDetail}");
            if (!string.IsNullOrWhiteSpace(_runtimeTarget)) DrawLine($"Target: {_runtimeTarget}");
            DrawLine($"Follow: {Settings.General.IsFollowEnabled.Value} | Chat paused: {_pausedByPartyChatCommand} | Whole paused: {_wholePluginPausedByPartyChatCommand}");
            DrawLine($"Tasks: {_tasks?.Count ?? 0} | Watchdog resets: {_watchdogResetCount}");
            if (_tradeInventoryDump?.IsActive == true) DrawLine("TradeDump: " + _tradeInventoryDump.Status);
            if (_pickUpManager?.IsActive == true) DrawLine("PickUp: " + _pickUpManager.Status);
            if (_partyTeleport != null && !string.IsNullOrWhiteSpace(_partyTeleport.Status)) DrawLine("TP: " + _partyTeleport.Status);
        }
        catch { }
    }

    private void ResetTaskWatchdog()
    {
        _watchdogTaskType = null;
        _watchdogTaskPosition = Vector3.Zero;
        _watchdogTaskStartedAt = DateTime.MinValue;
        _watchdogLastProgressAt = DateTime.MinValue;
        _watchdogBestTaskDistance = float.MaxValue;
    }

    private bool ShouldResetCurrentTaskForStuck(TaskNode currentTask, float taskDistance, float playerDistanceMoved)
    {
        try
        {
            if (!(Settings.General.AntiStuckWatchdog?.Value ?? true) || currentTask == null)
            {
                ResetTaskWatchdog();
                return false;
            }

            var now = DateTime.Now;
            var minProgress = Math.Max(10, Settings.General.AntiStuckMinProgressDistance.Value);
            var timeoutMs = Math.Max(1500, Settings.General.AntiStuckNoProgressMs.Value);
            var newTask = !_watchdogTaskType.HasValue ||
                          _watchdogTaskType.Value != currentTask.Type ||
                          Vector3.Distance(_watchdogTaskPosition, currentTask.WorldPosition) > Math.Max(120, minProgress * 3);

            if (newTask)
            {
                _watchdogTaskType = currentTask.Type;
                _watchdogTaskPosition = currentTask.WorldPosition;
                _watchdogTaskStartedAt = now;
                _watchdogLastProgressAt = now;
                _watchdogBestTaskDistance = taskDistance;
                return false;
            }

            if (playerDistanceMoved >= minProgress || taskDistance <= _watchdogBestTaskDistance - minProgress)
            {
                _watchdogLastProgressAt = now;
                _watchdogBestTaskDistance = Math.Min(_watchdogBestTaskDistance, taskDistance);
                return false;
            }

            var taskAgeMs = (now - _watchdogTaskStartedAt).TotalMilliseconds;
            var noProgressMs = (now - _watchdogLastProgressAt).TotalMilliseconds;
            return taskAgeMs >= timeoutMs && noProgressMs >= timeoutMs;
        }
        catch
        {
            ResetTaskWatchdog();
            return false;
        }
    }

    // Lightweight fallback terrain lookup used by CheckDashTerrain.
    private static byte GetTile(int x, int y)
    {
        // 255 = blocked, 0 = walkable; default safe is walkable.
        return 0;
    }
    
    
    // Attempts to trigger a Sprint if leader is too far while close-follow is enabled.
    
    
    private void UpdateDodgeSprintFSM()
    {
        using var __profileScope = ProfileScope("Follower.Sprint.UpdateDodgeSprintFSM.Internal");
        try
        {
            if (!(Settings.General.IsSprintEnabled?.Value ?? false))
            {
                ReleaseDodgeSprintKeyNow();
                _sprintFsmState = SprintFsm.Idle;
                return;
            }

            var leader = GetFollowingTarget();
            if (leader == null)
            {
                ReleaseDodgeSprintKeyNow();
                _sprintFsmState = SprintFsm.Idle;
                return;
            }

            var now = DateTime.Now;
            var dist = Vector3.Distance(GameController.Player.Pos, leader.Pos);
            // distance smoothing to reduce oscillations
            if (_distEwma <= 0f) _distEwma = dist;
            _distEwma = (float)(0.8 * _distEwma + 0.2 * dist);

            var startFar = Settings.General.SprintDistanceThreshold.Value;
            var releaseNear = Math.Max(10, (int)(startFar * 0.7));
            bool followGate = (Settings.General.IsCloseFollowEnabled?.Value ?? false) || (true /* always when far */);

            switch (_sprintFsmState)
            {
                case SprintFsm.Idle:
                    if (followGate && _distEwma >= startFar && now >= _nextSprintAllowed)
                    {
                        Input.KeyDown(Settings.General.DodgeSprintKey);
                        _sprintKeyDown = true;
                        _sprintHoldUntil = now.AddMilliseconds(SPRINT_HOLD_TO_START_MS);
                        _sprintForceReleaseAt = now.AddMilliseconds(SPRINT_MAX_HOLD_MS);
                        _releaseGateStart = DateTime.MinValue;
                        _sprintFsmState = SprintFsm.Holding;
                    }
                    break;

                case SprintFsm.Holding:
                    // Hard cap: never hold the sprint/dodge key indefinitely if the follower is stuck or the leader remains far.
                    if (_sprintForceReleaseAt != DateTime.MinValue && now >= _sprintForceReleaseAt)
                    {
                        Input.KeyUp(Settings.General.DodgeSprintKey);
                        _sprintKeyDown = false;
                        _sprintFsmState = SprintFsm.Idle;
                        _sprintHoldUntil = DateTime.MinValue;
                        _sprintForceReleaseAt = DateTime.MinValue;
                        _releaseGateStart = DateTime.MinValue;
                        _nextSprintAllowed = now.AddMilliseconds(Settings.General.SprintRetriggerCooldownMs.Value);
                        break;
                    }

                    // must hold at least until _sprintHoldUntil
                    if (now < _sprintHoldUntil) break;

                    // after min-hold, continue holding until distance is stably below release threshold
                    if (_distEwma <= releaseNear)
                    {
                        if (_releaseGateStart == DateTime.MinValue)
                            _releaseGateStart = now;
                        var stableMs = (now - _releaseGateStart).TotalMilliseconds;
                        if (stableMs >= SPRINT_RELEASE_STABLE_MS)
                        {
                            Input.KeyUp(Settings.General.DodgeSprintKey);
                            _sprintKeyDown = false;
                            _sprintFsmState = SprintFsm.Idle;
                            _sprintHoldUntil = DateTime.MinValue;
                            _sprintForceReleaseAt = DateTime.MinValue;
                            _nextSprintAllowed = now.AddMilliseconds(Settings.General.SprintRetriggerCooldownMs.Value);
                        }
                    }
                    else
                    {
                        _releaseGateStart = DateTime.MinValue; // not yet stable
                    }
                    break;
            }
        }
        catch
        {
            // never throw from render/update
        }
    }
    
    private bool CheckDashTerrain(Vector3 targetWorld)
    {
        using var __profileScope = ProfileScope("Follower.Sprint.CheckDashTerrain");
        // Purpose: detect if dashing (using dash key) helps to traverse short wall segments by moving cursor to dash destination and firing dash key.
        try
        {
            var playerGrid = GameController.Player.GridPos;
            var targetPosition = FollowerInternals.MathEx.WorldToGrid(targetWorld);
            var distance = Vector2.Distance(playerGrid, targetPosition);
            var dir = targetPosition - playerGrid;
            if (dir == Vector2.Zero) return false;
            dir = Vector2.Normalize(dir);

            var distanceBeforeWall = 0;
            var distanceInWall = 0;
            var shouldDash = false;

            for (var i = 0; i < 500; i++)
            {
                var v2Point = playerGrid + i * dir;
                var pt = new System.Drawing.Point((int)Math.Round(v2Point.X), (int)Math.Round(v2Point.Y));
                // Read tile info via internal helper if available
                byte tile = 0;
                try
                {
                    tile = GetTile(pt.X, pt.Y);
                }
                catch
                {
                    // If terrain reader unavailable, abort dash
                    return false;
                }

                // Interpret tile: 255 == invalid/blocked, other values walkable
                if (tile == 255)
                {
                    // inside wall
                    distanceInWall++;
                    if (distanceInWall > 20)
                    {
                        shouldDash = false;
                        break;
                    }
                }
                else
                {
                    // walkable
                    if (distanceInWall > 0)
                    {
                        // we have emerged from wall after some in-wall length -> candidate for dash
                        shouldDash = true;
                        break;
                    }
                    distanceBeforeWall++;
                    if (distanceBeforeWall > 10)
                    {
                        break;
                    }
                }
            }

            if (distanceBeforeWall > 10 || distanceInWall < 5)
                shouldDash = false;

            if (shouldDash)
            {
                _nextBotAction = DateTime.Now.AddMilliseconds(500 + random.Next(SafeBotInputFrequencyMs()));
                // Move cursor to target world position and perform dash key press
                var worldPos = FollowerInternals.MathEx.GridToWorld(targetPosition, targetWorld.Z);
                Mouse.SetCursorPos(WorldToValidScreenPosition(worldPos));
                QueueDodgeSprintTap(25);
                return true;
            }
        }
        catch { }
        return false;
    }
    

    private Entity GetFollowingTarget()
    {
        using var __profileScope = ProfileScope("Follower.GetFollowingTarget.Internal");
        var leaderName = Settings.General.LeaderName.Value?.Trim();
        if (string.IsNullOrWhiteSpace(leaderName))
            return null;

        // Area changes can invalidate the entity collection for a short moment.
        // Do not let one bad/stale entity abort the whole lookup; scan defensively.
        var target = FindLeaderIn(GameController.EntityListWrapper?.Entities, leaderName);
        if (target != null)
            return target;

        target = FindLeaderIn(GameController.Entities, leaderName);
        if (target != null)
            return target;

        return null;
    }

    private Entity FindLeaderIn(IEnumerable<Entity> entities, string leaderName)
    {
        if (entities == null)
            return null;

        Entity[] snapshot;
        try
        {
            snapshot = entities.ToArray();
        }
        catch
        {
            return null;
        }

        foreach (var entity in snapshot)
        {
            if (entity == null)
                continue;

            try
            {
                if (entity.Type != ExileCore2.Shared.Enums.EntityType.Player)
                    continue;

                var player = entity.GetComponent<Player>();
                var playerName = player?.PlayerName;
                if (string.IsNullOrWhiteSpace(playerName))
                    continue;

                if (string.Equals(playerName.Trim(), leaderName, StringComparison.OrdinalIgnoreCase))
                    return entity;
            }
            catch
            {
                // Entity memory can be transient directly after area changes. Skip only this entity.
            }
        }

        return null;
    }

    private Entity GetLootableQuestItem()
    {
        using var __profileScope = ProfileScope("Follower.GetLootableQuestItem.Internal");
        try
        {
            return GameController.EntityListWrapper.Entities
                .Where(e => e.Type == ExileCore2.Shared.Enums.EntityType.WorldItem)
                .Where(e => e.IsTargetable)
                .Where(e => e.GetComponent<WorldItem>() != null)
                .FirstOrDefault(e =>
                {
                    Entity itemEntity = e.GetComponent<WorldItem>().ItemEntity;
                    return GameController.Files.BaseItemTypes.Translate(itemEntity.Path).ClassName ==
                            "QuestItem";
                });
        }
        catch
        {
            return null;
}
    }
    
    private bool TryQueueArenaTransitionTask()
    {
        using var __profileScope = ProfileScope("Follower.ArenaTransition.TryQueue");

        try
        {
            var now = DateTime.Now;
            if (now < _arenaTransitionRetrySuppressedUntil)
                return false;

            if (_tasks.Any(t => t.Type == TaskNode.TaskNodeType.ArenaTransition))
                return true;

            if (now < _nextArenaTransitionScanAt)
                return false;

            // Keep this scan intentionally slow. Arena uses visible labels; Abyss uses the entity metadata.
            _nextArenaTransitionScanAt = now.AddMilliseconds(Math.Max(750, Settings.Transition.ArenaTransitionScanMs.Value));

            ArenaTransitionTarget target = null;
            var transitionName = "Arena";
            var isAbyssSubArea = false;

            if (Settings.Transition.AutoClickArenaTransition?.Value ?? true)
                target = FindVisibleArenaTransitionLabel();

            // Abyss sub-area transition is handled like a local map entrance. The follower waits until
            // the leader disappears from this area, then clicks the actual AreaTransition entity from metadata.
            if (target == null && ShouldQueueAbyssSubAreaTransition())
            {
                target = FindAbyssSubAreaTransitionEntityTarget();
                transitionName = "AbyssSubAreaTransition";
                isAbyssSubArea = true;
            }

            if (target == null)
                return false;

            // Stop normal follow/path actions while the transition is being clicked.
            _tasks.Clear();
            _tasks.Add(new TaskNode(
                target.WorldPosition != Vector3.Zero ? target.WorldPosition : GameController.Player.Pos,
                Settings.General.ClearPathDistance.Value,
                TaskNode.TaskNodeType.ArenaTransition));
            _activeArenaTransitionIsAbyssSubArea = isAbyssSubArea;
            ReleaseAllPluginInputsNow(force: true, reason: "Follower.ArenaTransition.Queue.ReleaseFollowMovement");
            _nextBotAction = DateTime.Now.AddMilliseconds(80);
            try { LogMessage($"ArenaTransition: queued {transitionName} '{target.Text}' ({target.MetadataPath})", 3); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            try { LogMessage("ArenaTransition queue error: " + ex.Message, 5); } catch { }
            return false;
        }
    }

    private bool ShouldQueueAbyssSubAreaTransition()
    {
        try
        {
            if (!(Settings.Transition.AutoClickAbyssSubAreaTransition?.Value ?? true))
                return false;

            if (IsInAbyssSubArea())
                return false;

            if (_lastTargetPosition == Vector3.Zero)
                return false;

            // Do not pre-click while the leader is still visible. The trigger is: leader was tracked on
            // this map, then disappeared after entering the sub-area.
            if (_followTarget != null)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private ArenaTransitionTarget FindAbyssSubAreaTransitionEntityTarget()
    {
        using var __profileScope = ProfileScope("Follower.AbyssSubAreaTransition.FindEntityTarget");

        try
        {
            Entity[] entities;
            try
            {
                entities = GameController.EntityListWrapper?.Entities?.ToArray()
                           ?? GameController.Entities?.ToArray()
                           ?? Array.Empty<Entity>();
            }
            catch
            {
                return null;
            }

            var playerPos = GameController.Player.Pos;
            var anchor = _lastTargetPosition != Vector3.Zero ? _lastTargetPosition : playerPos;
            var maxClickDistance = Math.Max(80, Settings.General.ClearPathDistance.Value * 2);

            Entity best = null;
            float bestScore = float.MaxValue;

            foreach (var entity in entities)
            {
                if (entity == null)
                    continue;

                if (!IsAbyssSubAreaTransitionEntity(entity))
                    continue;

                try
                {
                    if (entity.IsHidden)
                        continue;
                }
                catch { }

                var distanceFromPlayer = Vector3.Distance(playerPos, entity.Pos);
                if (distanceFromPlayer > maxClickDistance)
                    continue;

                var distanceFromLeaderLastPos = Vector3.Distance(anchor, entity.Pos);
                var score = distanceFromPlayer + (distanceFromLeaderLastPos * 0.35f);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = entity;
                }
            }

            if (best == null)
                return null;

            var center = WorldToValidScreenPosition(best.Pos);
            if (center == Vector2.Zero)
                return null;

            return new ArenaTransitionTarget
            {
                LabelElement = null,
                LabelCenter = center,
                WorldPosition = best.Pos,
                EntityId = best.Id,
                Text = "AbyssSubAreaTransition",
                MetadataPath = best.Path
            };
        }
        catch
        {
            return null;
        }
    }

    private ArenaTransitionTarget FindVisibleArenaTransitionLabel()
    {
        using var __profileScope = ProfileScope("Follower.ArenaTransition.FindVisibleLabel");

        var wantedText = NormalizeUiText(Settings.Transition.ArenaTransitionLabelText?.Value);
        if (string.IsNullOrWhiteSpace(wantedText))
            wantedText = "Arena";

        var metadataFilter = ArenaTransitionMetadataFilter;

        try
        {
            dynamic ingameUi = GameController.IngameState?.IngameUi;
            if (ingameUi == null)
                return null;

            // Low-cost path from the hover dump:
            // IngameUi -> ItemsOnGroundLabelsElement -> LabelsOnGroundVisible -> Label.Text == "Arena".
            // Do not enumerate LabelsOnGround here. On juiced maps it can be very large and can stall the API.
            dynamic labelsElement = null;
            try { labelsElement = ingameUi.ItemsOnGroundLabelsElement; } catch { labelsElement = null; }

            dynamic visibleLabels = null;
            try { visibleLabels = labelsElement?.LabelsOnGroundVisible; } catch { visibleLabels = null; }

            var target = FindArenaTransitionTargetInLabelCollection(
                visibleLabels,
                wantedText,
                metadataFilter,
                maxLabelsToInspect: 32);
            if (target != null)
                return target;

            // Compatibility fallback for ExileCore2 builds where ItemsOnGroundLabels is already the visible collection.
            // Keep this capped. It is a fallback only and must not turn into a full ground-label sweep.
            dynamic directLabels = null;
            try { directLabels = ingameUi.ItemsOnGroundLabels; } catch { directLabels = null; }
            return FindArenaTransitionTargetInLabelCollection(
                directLabels,
                wantedText,
                metadataFilter,
                maxLabelsToInspect: 64);
        }
        catch
        {
            return null;
        }
    }

    private ArenaTransitionTarget FindArenaTransitionTargetInLabelCollection(
        dynamic labels,
        string wantedText,
        string metadataFilter,
        int maxLabelsToInspect)
    {
        if (labels == null)
            return null;

        var inspected = 0;
        try
        {
            foreach (dynamic groundLabel in labels)
            {
                if (groundLabel == null)
                    continue;

                inspected++;
                if (inspected > maxLabelsToInspect)
                    break;

                var target = TryReadArenaTransitionTarget(groundLabel, wantedText, metadataFilter);
                if (target != null)
                    return target;
            }
        }
        catch
        {
            // Some UI nodes are not enumerable in some ExileCore2 builds.
        }

        return null;
    }

    private ArenaTransitionTarget TryReadArenaTransitionTarget(dynamic groundLabel, string wantedText, string metadataFilter)
    {
        try
        {
            dynamic labelElement = null;
            try { labelElement = groundLabel.Label; } catch { labelElement = null; }
            if (labelElement == null)
                return null;

            var text = NormalizeUiText(UiTextOf(labelElement));
            if (!string.Equals(text, wantedText, StringComparison.OrdinalIgnoreCase))
                return null;

            dynamic itemOnGround = null;
            try { itemOnGround = groundLabel.ItemOnGround; } catch { itemOnGround = null; }

            var path = PathOf(itemOnGround);
            if (!string.IsNullOrWhiteSpace(metadataFilter) &&
                (string.IsNullOrWhiteSpace(path) || path.IndexOf(metadataFilter.Trim(), StringComparison.OrdinalIgnoreCase) < 0))
                return null;

            if (!IsVisibleElement(labelElement))
                return null;

            var center = CenterOfElement(labelElement);
            if (center == Vector2.Zero)
                return null;

            return new ArenaTransitionTarget
            {
                LabelElement = labelElement,
                LabelCenter = ClientToScreen(center),
                WorldPosition = PositionOf(itemOnGround),
                EntityId = IdOf(itemOnGround),
                Text = text,
                MetadataPath = path
            };
        }
        catch
        {
            // UI memory can mutate while the tree is being read. Skip this label only.
            return null;
        }
    }

    private bool TryHoverThenClickArenaTransition(ArenaTransitionTarget target)
    {
        using var __profileScope = ProfileScope("Follower.ArenaTransition.TryHoverThenClick");
        if (target == null)
            return false;

        var now = DateTime.Now;
        var entityId = target.EntityId;
        var clickPos = target.LabelCenter;
        var offset = Math.Max(0, Settings.General.RandomClickOffset.Value);
        if (offset > 0)
        {
            clickPos += new Vector2(
                random.Next(-offset, offset + 1),
                random.Next(-Math.Max(1, offset / 2), Math.Max(1, offset / 2) + 1));
        }

        if (_arenaTransitionHoverEntityId != entityId || _arenaTransitionHoverClickAt == DateTime.MinValue)
        {
            PrepareForPluginMouseAction("Follower.ArenaTransition.Hover.Prepare");
            if (!Mouse.IsGuardLocked) Mouse.SetCursorPosHuman2(clickPos);
            _arenaTransitionHoverEntityId = entityId;
            _arenaTransitionHoverClickAt = now.AddMilliseconds(ArenaTransitionHoverDelayMs);
            return true;
        }

        if (now < _arenaTransitionHoverClickAt)
            return true;

        PrepareForPluginMouseAction("Follower.ArenaTransition.Click.Prepare");
        if (!Mouse.IsGuardLocked) Mouse.SetCursorPosAndLeftClickHuman(clickPos, 0);
        CompletePluginMouseAction("Follower.ArenaTransition.Click.Complete");
        ResetPendingArenaTransitionClick();
        _nextBotAction = now.AddSeconds(1);

        try { LogMessage($"ArenaTransition: clicked '{target.Text}' ({target.MetadataPath})", 3); } catch { }
        return true;
    }

    private void ResetPendingArenaTransitionClick()
    {
        _arenaTransitionHoverEntityId = 0;
        _arenaTransitionHoverClickAt = DateTime.MinValue;
    }

    private static string UiTextOf(dynamic element)
    {
        try
        {
            string textNoTags = null;
            try { textNoTags = (string)element.TextNoTags; } catch { }
            if (!string.IsNullOrWhiteSpace(textNoTags)) return textNoTags;

            string text = null;
            try { text = (string)element.Text; } catch { }
            return text;
        }
        catch { return null; }
    }

    private static string NormalizeUiText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var normalized = text.Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

        var chars = new List<char>(normalized.Length);
        var insideTag = false;
        foreach (var ch in normalized)
        {
            if (ch == '<')
            {
                insideTag = true;
                continue;
            }

            if (ch == '>' && insideTag)
            {
                insideTag = false;
                continue;
            }

            if (!insideTag)
                chars.Add(ch);
        }

        normalized = new string(chars.ToArray()).Trim();
        while (normalized.Contains("  "))
            normalized = normalized.Replace("  ", " ");
        return normalized;
    }

    private static string PathOf(dynamic entity)
    {
        if (entity == null) return string.Empty;
        try { return (string)entity.Path ?? string.Empty; } catch { return string.Empty; }
    }

    private static uint IdOf(dynamic entity)
    {
        if (entity == null) return 0;
        try { return Convert.ToUInt32(entity.Id); } catch { return 0; }
    }

    private static Vector3 PositionOf(dynamic entity)
    {
        if (entity == null) return Vector3.Zero;
        try { return (Vector3)entity.Pos; } catch { return Vector3.Zero; }
    }

    private static bool IsVisibleElement(dynamic element)
    {
        if (element == null) return false;
        try { return (bool)element.IsVisible; } catch { }
        try { return (bool)element.IsVisibleLocal; } catch { }
        return true;
    }

    private static Vector2 CenterOfElement(dynamic element)
    {
        if (element == null) return Vector2.Zero;

        try
        {
            var rect = element.GetClientRect();
            var center = rect.Center;
            return new Vector2(Convert.ToSingle(center.X), Convert.ToSingle(center.Y));
        }
        catch
        {
            return Vector2.Zero;
        }
    }

    private sealed class PortalTarget
    {
        public dynamic LabelElement { get; set; }
        public Vector2 ClickPosition { get; set; }
        public Vector3 WorldPosition { get; set; }
        public uint EntityId { get; set; }
        public string MetadataPath { get; set; }
        public bool FromLabel { get; set; }
    }

    private sealed class ArenaTransitionTarget
    {
        public dynamic LabelElement { get; set; }
        public Vector2 LabelCenter { get; set; }
        public Vector3 WorldPosition { get; set; }
        public uint EntityId { get; set; }
        public string Text { get; set; }
        public string MetadataPath { get; set; }
    }

    public override void EntityAdded(Entity entity)
    {
        using var __profileScope = ProfileScope("Follower.EntityAdded");
        // Special handling for transitions that don't use the normal AreaTransition/Portal entity types.
        if (!string.IsNullOrEmpty(entity.RenderName))
        {
            // Atziri's Temple entrance inside Vaal Ruins behaves like a portal but uses a misc object entity type.
            // Treat it as an area transition so the follower can path to and click it.
            if (entity.RenderName.Contains("Atziri's Temple", StringComparison.OrdinalIgnoreCase))
            {
                if (!_areaTransitions.ContainsKey(entity.Id))
                    _areaTransitions.Add(entity.Id, entity);
            }

            // Incursion hub transition is also not a standard AreaTransition.
            if (!string.IsNullOrEmpty(entity.Path) && entity.Path.Contains("Incursion/Objects/HubTransition"))
            {
                if (!_areaTransitions.ContainsKey(entity.Id))
                    _areaTransitions.Add(entity.Id, entity);
            }
        }

        if (IsMapCheckerPortalEntity(entity) || IsAbyssSubAreaTransitionEntity(entity))
        {
            if (!_areaTransitions.ContainsKey(entity.Id))
                _areaTransitions.Add(entity.Id, entity);
        }

        switch (entity.Type)
        {
            //Handle clickable teleporters
            case ExileCore2.Shared.Enums.EntityType.AreaTransition:
            case ExileCore2.Shared.Enums.EntityType.Portal:
            case ExileCore2.Shared.Enums.EntityType.TownPortal:
                if (!_areaTransitions.ContainsKey(entity.Id))
                    _areaTransitions.Add(entity.Id, entity);
                break;
        }

        base.EntityAdded(entity);
    }

    public override void EntityRemoved(Entity entity)
    {
        using var __profileScope = ProfileScope("Follower.EntityRemoved");
        // Special handling for non-standard transition entities (must mirror EntityAdded).
        if (!string.IsNullOrEmpty(entity.RenderName))
        {
            if (entity.RenderName.Contains("Atziri's Temple", StringComparison.OrdinalIgnoreCase))
            {
                if (_areaTransitions.ContainsKey(entity.Id))
                    _areaTransitions.Remove(entity.Id);
            }
        }

        if (!string.IsNullOrEmpty(entity.Path) && entity.Path.Contains("Incursion/Objects/HubTransition"))
        {
            if (_areaTransitions.ContainsKey(entity.Id))
                _areaTransitions.Remove(entity.Id);
        }

        if (IsMapCheckerPortalEntity(entity) || IsAbyssSubAreaTransitionEntity(entity))
        {
            if (_areaTransitions.ContainsKey(entity.Id))
                _areaTransitions.Remove(entity.Id);
        }

        switch (entity.Type)
        {
            //Handle clickable teleporters
            case ExileCore2.Shared.Enums.EntityType.AreaTransition:
            case ExileCore2.Shared.Enums.EntityType.Portal:
            case ExileCore2.Shared.Enums.EntityType.TownPortal:
                if (_areaTransitions.ContainsKey(entity.Id))
                    _areaTransitions.Remove(entity.Id);
                break;
        }

        base.EntityRemoved(entity);
    }



    private bool IsMapCheckerPortalEntity(Entity entity)
    {
        try
        {
            return IsSupportedHideoutPortalMetadataPath(entity?.Path);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSupportedHideoutPortalMetadataPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = path.Replace('\\', '/').Trim();

        return normalized.IndexOf(MapCheckerPortalPath, StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalized.StartsWith(TownPortalMtxMetadataPrefix, StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(HideoutPortalMetadataPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAbyssSubAreaTransitionEntity(Entity entity)
    {
        try
        {
            var path = entity?.Path;
            return !string.IsNullOrEmpty(path) &&
                   path.IndexOf(AbyssSubAreaTransitionMetadataPath, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private PortalTarget ResolveBestHideoutPortalTarget()
    {
        using var __profileScope = ProfileScope("Follower.Portal.ResolveBestHideoutPortalTarget");

        var labelTarget = FindNearestVisibleHideoutPortalLabel();
        if (labelTarget != null)
            return labelTarget;

        var entity = FindNearestMapCheckerPortal(requireTargetable: false);
        return entity == null ? null : ToPortalTarget(entity);
    }

    private PortalTarget FindNearestVisibleHideoutPortalLabel()
    {
        using var __profileScope = ProfileScope("Follower.Portal.FindNearestVisibleHideoutPortalLabel");

        try
        {
            dynamic ingameUi = GameController.IngameState?.IngameUi;
            if (ingameUi == null)
                return null;

            dynamic labelsElement = null;
            try { labelsElement = ingameUi.ItemsOnGroundLabelsElement; } catch { labelsElement = null; }
            if (labelsElement == null)
            {
                // Compatibility with dumps/builds that expose the singular property name.
                try { labelsElement = ingameUi.ItemsOnGroundLabelElement; } catch { labelsElement = null; }
            }

            dynamic visibleLabels = null;
            try { visibleLabels = labelsElement?.LabelsOnGroundVisible; } catch { visibleLabels = null; }
            if (visibleLabels == null)
            {
                // Compatibility with dumps/builds that expose the singular property name.
                try { visibleLabels = labelsElement?.LabelOnGroundVisible; } catch { visibleLabels = null; }
            }

            var target = FindPortalTargetInLabelCollection(visibleLabels, PortalLabelScanMaxVisibleLabels);
            if (target != null)
                return target;

            // Fallback only. Keep capped; the full ground-label collection can be large on maps.
            dynamic directLabels = null;
            try { directLabels = ingameUi.ItemsOnGroundLabels; } catch { directLabels = null; }
            return FindPortalTargetInLabelCollection(directLabels, PortalLabelScanMaxVisibleLabels);
        }
        catch
        {
            return null;
        }
    }

    private PortalTarget FindPortalTargetInLabelCollection(dynamic labels, int maxLabelsToInspect)
    {
        if (labels == null)
            return null;

        var inspected = 0;
        PortalTarget best = null;
        float bestScore = float.MaxValue;

        Vector3 playerPos;
        try { playerPos = GameController.Player.Pos; }
        catch { playerPos = Vector3.Zero; }

        var anchor = _lastTargetPosition != Vector3.Zero ? _lastTargetPosition : playerPos;

        try
        {
            foreach (dynamic groundLabel in labels)
            {
                if (groundLabel == null)
                    continue;

                inspected++;
                if (inspected > maxLabelsToInspect)
                    break;

                var target = TryReadPortalTarget(groundLabel);
                if (target == null)
                    continue;

                var score = ScorePortalTarget(target, playerPos, anchor);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = target;
                }
            }
        }
        catch
        {
            // UI memory can mutate while enumerating visible labels. Return the best target found so far.
        }

        return best;
    }

    private float ScorePortalTarget(PortalTarget target, Vector3 playerPos, Vector3 anchor)
    {
        if (target != null && target.WorldPosition != Vector3.Zero && playerPos != Vector3.Zero)
        {
            var distanceFromPlayer = Vector3.Distance(playerPos, target.WorldPosition);
            var distanceFromLeaderLastPos = anchor != Vector3.Zero ? Vector3.Distance(anchor, target.WorldPosition) : 0f;
            return distanceFromPlayer + (distanceFromLeaderLastPos * 0.25f);
        }

        // If world position is not available, still prefer real visible labels over entity fallback.
        return 0f;
    }

    private PortalTarget TryReadPortalTarget(dynamic groundLabel)
    {
        try
        {
            dynamic labelElement = null;
            try { labelElement = groundLabel.Label; } catch { labelElement = null; }
            if (labelElement == null || !IsVisibleElement(labelElement))
                return null;

            var itemOnGround = EntityFromGroundLabel(groundLabel);
            var path = PathOf(itemOnGround);
            if (!IsSupportedHideoutPortalMetadataPath(path))
                return null;

            var center = CenterOfElement(labelElement);
            if (center == Vector2.Zero)
                return null;

            return new PortalTarget
            {
                LabelElement = labelElement,
                ClickPosition = ClientToScreen(center),
                WorldPosition = PositionOf(itemOnGround),
                EntityId = StableEntityId(IdOf(itemOnGround), path, center),
                MetadataPath = path,
                FromLabel = true
            };
        }
        catch
        {
            return null;
        }
    }

    private static dynamic EntityFromGroundLabel(dynamic groundLabel)
    {
        if (groundLabel == null)
            return null;

        try
        {
            var entity = groundLabel.ItemOnGround;
            if (entity != null) return entity;
        }
        catch { }

        try
        {
            var entity = groundLabel.Entity;
            if (entity != null) return entity;
        }
        catch { }

        try
        {
            var entity = groundLabel.Item;
            if (entity != null) return entity;
        }
        catch { }

        return null;
    }

    private Entity FindNearestMapCheckerPortal(bool requireTargetable)
    {
        using var __profileScope = ProfileScope("Follower.Portal.FindNearestMetadataPortalEntity");
        Entity[] entities;

        try
        {
            entities = GameController.EntityListWrapper?.Entities?.ToArray()
                       ?? GameController.Entities?.ToArray()
                       ?? Array.Empty<Entity>();
        }
        catch
        {
            return null;
        }

        Vector3 playerPos;
        try { playerPos = GameController.Player.Pos; }
        catch { return null; }

        Entity best = null;
        float bestDist = float.MaxValue;

        foreach (var e in entities)
        {
            if (e == null) continue;

            if (!IsMapCheckerPortalEntity(e))
                continue;

            if (requireTargetable)
            {
                bool targetable;
                try { targetable = e.IsTargetable; }
                catch { targetable = false; }

                if (!targetable) continue;
            }

            float dist;
            try { dist = Vector3.Distance(playerPos, e.Pos); }
            catch { continue; }

            if (dist < bestDist)
            {
                bestDist = dist;
                best = e;
            }
        }

        return best;
    }

    private PortalTarget ToPortalTarget(Entity entity)
    {
        if (entity == null)
            return null;

        try
        {
            var worldPosition = entity.Pos;
            return new PortalTarget
            {
                LabelElement = null,
                ClickPosition = WorldToValidScreenPosition(worldPosition),
                WorldPosition = worldPosition,
                EntityId = StableEntityId(entity.Id, entity.Path, Vector2.Zero),
                MetadataPath = entity.Path,
                FromLabel = false
            };
        }
        catch
        {
            return null;
        }
    }

    private bool TryHoverThenClickPortal(PortalTarget portal, Vector2 screenPos)
    {
        using var __profileScope = ProfileScope("Follower.Portal.TryHoverThenClickPortal");
        if (portal == null) return false;

        var now = DateTime.Now;
        var entityId = portal.EntityId;
        var clickPos = screenPos != Vector2.Zero ? screenPos : portal.ClickPosition;
        if (clickPos == Vector2.Zero && portal.WorldPosition != Vector3.Zero)
            clickPos = WorldToValidScreenPosition(portal.WorldPosition);

        if (clickPos == Vector2.Zero)
            return false;

        var offset = Math.Max(0, Settings.General.RandomClickOffset.Value);
        if (offset > 0 && portal.FromLabel)
        {
            clickPos += new Vector2(
                random.Next(-offset, offset + 1),
                random.Next(-Math.Max(1, offset / 2), Math.Max(1, offset / 2) + 1));
        }

        if (_portalHoverEntityId != entityId || _portalHoverClickAt == DateTime.MinValue)
        {
            PrepareForPluginMouseAction("Follower.Portal.Hover.Prepare");
            if (!Mouse.IsGuardLocked) Mouse.SetCursorPosHuman2(clickPos);
            _portalHoverEntityId = entityId;
            _portalHoverClickAt = now.AddMilliseconds(PortalHoverDelayMs);
            return true;
        }

        if (now < _portalHoverClickAt)
            return true;

        PrepareForPluginMouseAction("Follower.Portal.Click.Prepare");
        if (!Mouse.IsGuardLocked) Mouse.SetCursorPosAndLeftClickHuman(clickPos, 0);
        CompletePluginMouseAction("Follower.Portal.Click.Complete");
        ResetPendingPortalClick();
        _nextBotAction = now.AddSeconds(1);
        try { LogMessage($"Portal: clicked {(portal.FromLabel ? "label" : "entity")} ({portal.MetadataPath})", 3); } catch { }
        return true;
    }

    private void ResetPendingPortalClick()
    {
        _portalHoverEntityId = 0;
        _portalHoverClickAt = DateTime.MinValue;
    }

    private static uint StableEntityId(uint entityId, string path, Vector2 clickPosition)
    {
        if (entityId != 0)
            return entityId;

        unchecked
        {
            uint hash = 2166136261u;
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var ch in path)
                {
                    hash ^= (uint)char.ToUpperInvariant(ch);
                    hash *= 16777619u;
                }
            }

            hash ^= (uint)Math.Round(clickPosition.X);
            hash *= 16777619u;
            hash ^= (uint)Math.Round(clickPosition.Y);
            hash *= 16777619u;
            return hash == 0 ? 1u : hash;
        }
    }

    private Vector2 WorldToValidScreenPosition(Vector3 worldPos)
    {
        using var __profileScope = ProfileScope("Follower.WorldToValidScreenPosition.Internal");
        var windowRect = GameController.Window.GetWindowRectangle();
        var screenPos = Camera.WorldToScreen(worldPos);
        var result = screenPos + windowRect.Location;

        var edgeBounds = 50;
        if (!windowRect.Intersects(new RectangleF(result.X, result.Y, edgeBounds, edgeBounds)))
        {
            //Adjust for offscreen entity. Need to clamp the screen position using the game window info. 
            if (result.X < windowRect.TopLeft.X) result.X = windowRect.TopLeft.X + edgeBounds;
            if (result.Y < windowRect.TopLeft.Y) result.Y = windowRect.TopLeft.Y + edgeBounds;
            if (result.X > windowRect.BottomRight.X) result.X = windowRect.BottomRight.X - edgeBounds;
            if (result.Y > windowRect.BottomRight.Y) result.Y = windowRect.BottomRight.Y - edgeBounds;
        }
        return result;
    }

    private void DrawPath()
    {
        using var __profileScope = ProfileScope("Follower.DrawPath");

        if (_tasks != null && _tasks.Count > 1)
            for (var i = 1; i < _tasks.Count; i++)
            {
                var start = WorldToValidScreenPosition(_tasks[i - 1].WorldPosition);
                var end = WorldToValidScreenPosition(_tasks[i].WorldPosition);
                Graphics.DrawLine(start, end, 2, Color.Pink);
            }
        var dist = _tasks.Count > 0 ? Vector3.Distance(GameController.Player.Pos, _tasks.First().WorldPosition) : 0;
        var targetDist = _lastTargetPosition == null ? "NA" : Vector3.Distance(GameController.Player.Pos, _lastTargetPosition).ToString();
        Graphics.DrawText($"Follow Enabled: {Settings.General.IsFollowEnabled.Value}", new Vector2(500, 120));
        Graphics.DrawText($"Task Count: {_tasks.Count} Next WP Distance: {dist} Target Distance: {targetDist}", new Vector2(500, 140));
        var counter = 0;
        foreach (var transition in _areaTransitions)
        {
            counter++;
            Graphics.DrawText($"{transition.Key} at {transition.Value.Pos.X} {transition.Value.Pos.Y}", new Vector2(100, 120 + counter * 20));
        }
    
    }



private bool IsInventoryOpen()
{
    using var __profileScope = ProfileScope("Follower.IsInventoryOpen.Internal");
    if (!Settings.General.PauseWhenInventoryOpen.Value) return false;

    try
    {
        var ingame = GameController.IngameState;
        var ui = ingame?.IngameUi;
        if (ui == null) return false;

        // Prefer the actual "open right panel" inventory, because some root inventory widgets
        // can remain visible even when the panel is closed.
        object? panel =
            TryGetNestedProperty(ui, "OpenRightPanel", "InventoryPanel") ??
            TryGetProperty(ui, "InventoryPanel") ??
            TryGetProperty(ui, "Inventory") ??
            TryGetProperty(ui, "InventoryWindow");

        if (panel == null) return false;

        // If this is an Element-like object, rely on visibility flags + geometry.
        // In PoE2 UI many panels remain IsActive=true even when hidden, so NEVER use IsActive alone.
        var isVisibleLocal = GetBool(panel, "IsVisibleLocal");
        var isVisible = GetBool(panel, "IsVisible");
        var width = GetInt(panel, "Width");
        var height = GetInt(panel, "Height");

        // Some UI elements can report negative coordinates depending on anchoring,
        // so position is not a reliable signal for "open".

        // Inventory open heuristic:
        // - Must be visible (locally or globally)
        // - Must have a reasonable size (closed panels often report 0 or tiny sizes)
        if ((isVisibleLocal || isVisible) && width >= 200 && height >= 200)
            return true;

        // Fallback flags for unusual UI models (still requires visibility).
        return (isVisibleLocal || isVisible) && (GetBool(panel, "IsOpened") || GetBool(panel, "IsOpen"));
    }
    catch
    {
        return false;
    }

    static object? TryGetProperty(object obj, string name)
    {
        var t = obj.GetType();
        var p = t.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        return p?.GetValue(obj);
    }

    static object? TryGetNestedProperty(object obj, string parentName, string childName)
    {
        var parent = TryGetProperty(obj, parentName);
        return parent != null ? TryGetProperty(parent, childName) : null;
    }

    static bool GetBool(object obj, string prop)
    {
        try
        {
            var t = obj.GetType();
            var p = t.GetProperty(prop, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (p == null) return false;
            return p.GetValue(obj) is bool b && b;
        }
        catch { return false; }
    }

    static int GetInt(object obj, string prop)
    {
        try
        {
            var t = obj.GetType();
            var p = t.GetProperty(prop, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (p == null) return 0;
            var v = p.GetValue(obj);
            return v switch
            {
                int i => i,
                long l => unchecked((int)l),
                float f => (int)f,
                double d => (int)d,
                _ => 0
            };
        }
        catch { return 0; }
    }
}

}