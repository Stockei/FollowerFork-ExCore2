using System;
using System.IO;
using System.Linq;
using System.Numerics;
using ExileCore2.PoEMemory.MemoryObjects;

namespace Follower;

// End-of-map portal. In maps, when the leader disappears right at a portal that leads to a hideout, the follower walks
// to that portal and clicks it, instead of teleporting through the party panel. The party teleport stays as fallback.
// Sequence: leader vanished at the portal -> confirm the leader stays gone -> walk -> hover label, click once -> wait for the
// zone change -> click again only after the full wait, with a small click budget per zone.
// After every zone change the party teleport and this logic wait for a settle time.
public partial class Follower
{
    private const double MapExitMaxCountedTickGapMs = 250;  // longer pauses between Render calls (loading, window in background) do not count as time
    private const double MapExitMaxPlanningGapMs = 1000;    // follow planning paused longer than this: leader data is stale
    private const double MapExitVanishConfirmMs = 1500;     // the leader must stay gone this long before the follower acts
    private const double MapExitRequeueDelayMs = 1000;      // pause before walking again after the anti-stuck watchdog dropped the walk
    private const float MapExitClickRange = 400f;           // click the portal from here, walk closer before
    private const float MapExitStandStillRange = 100f;      // this close without a clickable label: stop walking
    private const float MapExitLabelMatchDistance = 40f;
    private const int MapExitHoverDelayMs = 300;            // the cursor rests on the label this long before the click
    private const string MapExitDebugFileName = "MapExitPortalDebug.txt";

    // Active clock: advances only while Render runs, so loading screens and a background window never count toward a wait.
    private double _mapExitClockMs;
    private DateTime _mapExitLastRenderAt = DateTime.MinValue;

    private bool _mapExitArrivalPending = true;
    private double _mapExitSettleUntilMs;
    private uint _mapExitZonePlayerId;

    private readonly LeaderVanishTracker _leaderVanishTracker = new LeaderVanishTracker(MapExitMaxPlanningGapMs);
    private MapExitPhase _mapExitPhase = MapExitPhase.Idle;
    private double _mapExitPhaseStartedMs;
    private uint _mapExitPortalId;
    private string _mapExitPortalName = string.Empty;
    private int _mapExitClicks;
    private double _mapExitLastClickMs = double.NegativeInfinity;
    private double _mapExitRequeueAtMs = -1;
    private bool _mapExitTpFallbackReported;

    private bool MapExitEnabled => Settings.Transition.FollowThroughMapExitPortal?.Value ?? true;
    private int MapExitRadius => Settings.Transition.MapExitPortalRadius?.Value ?? 150;
    private int MapExitMaxLeaderDistance => Settings.Transition.MapExitMaxLeaderDistance?.Value ?? 1200;
    private int MapExitRetryWaitMs => Math.Max(6000, Settings.Transition.MapExitRetryWaitMs?.Value ?? 8000);
    private int MapExitMaxClicks => Math.Clamp(Settings.Transition.MapExitMaxClicks?.Value ?? 3, 1, 4);
    private int MapExitTpFallbackMs => Math.Max(10000, Settings.Transition.MapExitTpFallbackMs?.Value ?? 20000);
    private int ZoneSettleMs => Math.Max(0, Settings.Transition.ZoneSettleMs?.Value ?? 8000);

    private bool IsZoneSettling => _mapExitClockMs < _mapExitSettleUntilMs;

    private bool IsMapExitBusy =>
        _mapExitPhase == MapExitPhase.Confirming ||
        _mapExitPhase == MapExitPhase.Approach ||
        _mapExitPhase == MapExitPhase.AwaitZoneChange;

    /// <summary>
    /// First thing in every Render: advances the active clock and starts the settle time after a zone change.
    /// A new id for the follower's own character (its own transition, seen before the zone name changes) counts as well.
    /// </summary>
    private void TickMapExitZoneState()
    {
        var now = DateTime.Now;
        if (_mapExitLastRenderAt != DateTime.MinValue)
            _mapExitClockMs += Math.Clamp((now - _mapExitLastRenderAt).TotalMilliseconds, 0, MapExitMaxCountedTickGapMs);
        _mapExitLastRenderAt = now;

        uint playerId = 0;
        try { playerId = GameController.Player?.Id ?? 0; } catch { }

        if (_mapExitArrivalPending)
        {
            _mapExitArrivalPending = false;
            BeginZoneSettle(playerId, "zone change");
        }
        else if (playerId != 0 && _mapExitZonePlayerId == 0)
        {
            _mapExitZonePlayerId = playerId;
        }
        else if (playerId != 0 && playerId != _mapExitZonePlayerId)
        {
            BeginZoneSettle(playerId, "own character changed (own transition)");
        }
    }

    private void OnMapExitAreaChange()
    {
        // The settle time starts with the first Render in the new zone, when the plugin runs again.
        _mapExitArrivalPending = true;
    }

    private void BeginZoneSettle(uint playerId, string reason)
    {
        CancelMapExit(reason);
        _mapExitZonePlayerId = playerId;
        _mapExitSettleUntilMs = _mapExitClockMs + ZoneSettleMs;
        _leaderVanishTracker.Reset(_mapExitClockMs);
        _mapExitPortalId = 0;
        _mapExitPortalName = string.Empty;
        _mapExitClicks = 0;
        _mapExitLastClickMs = double.NegativeInfinity;
        _mapExitTpFallbackReported = false;

        string areaName = null;
        try { areaName = GameController.Area.CurrentArea?.Name; } catch { }
        WriteMapExitDebug($"settle {ZoneSettleMs} ms ({reason}); area '{areaName}', map={IsInMapArea()}");
    }

    /// <summary>
    /// Asked by PartyTeleport before it acts. True while the teleport has to wait; the reason becomes its status.
    /// </summary>
    internal bool ShouldHoldPartyTeleport(out string reason)
    {
        try
        {
            var inMap = IsInMapArea();
            var leaderVisible = _leaderVanishTracker.IsLeaderVisibleAt(_mapExitClockMs);
            reason = MapExitRules.WhyHoldTeleport(
                _mapExitSettleUntilMs - _mapExitClockMs,
                MapExitEnabled,
                inMap,
                _mapExitPhase,
                _mapExitClockMs - _mapExitLastClickMs,
                MapExitRetryWaitMs,
                leaderVisible,
                LeaderGoneMs(),
                MapExitTpFallbackMs);

            if (reason == null && inMap && MapExitEnabled && !leaderVisible && !_mapExitTpFallbackReported)
            {
                _mapExitTpFallbackReported = true;
                WriteMapExitDebug($"party TP fallback allowed: leader gone {LeaderGoneMs() / 1000:0.0}s, portal phase {_mapExitPhase}");
            }

            return reason != null;
        }
        catch
        {
            reason = null;
            return false;
        }
    }

    /// <summary>
    /// Runs in follow planning right after the leader lookup. Returns true while the map exit owns the follower;
    /// normal follow and the Arena/Abyss transitions then stay out of the way.
    /// </summary>
    private bool UpdateMapExitPortal()
    {
        using var __profileScope = ProfileScope("Follower.MapExit.Update");
        try
        {
            var leader = _followTarget;
            var leaderPos = Vector3.Zero;
            var leaderDistance = 0f;
            if (leader != null)
            {
                leaderPos = leader.Pos;
                leaderDistance = Distance2D(GameController.Player.Pos, leaderPos);
            }

            var vanished = _leaderVanishTracker.Update(_mapExitClockMs, leader != null, leaderPos, leaderDistance);
            if (!_leaderVanishTracker.LastUpdateWasContinuous && IsMapExitBusy)
                CancelMapExit("follow logic was paused");

            if (!MapExitEnabled && IsMapExitBusy)
                CancelMapExit("map exit portal disabled");

            if (leader != null)
            {
                _mapExitTpFallbackReported = false;
                if (_mapExitPhase != MapExitPhase.Idle)
                    CancelMapExit("leader is visible again");
                return false;
            }

            if (vanished)
                OnLeaderVanished();

            switch (_mapExitPhase)
            {
                case MapExitPhase.Confirming: UpdateMapExitConfirming(); break;
                case MapExitPhase.Approach: UpdateMapExitApproach(); break;
                case MapExitPhase.AwaitZoneChange: UpdateMapExitAwait(); break;
            }

            // Also after a finished attempt (Done): the leader left through a hideout portal, so no other transition
            // here is worth clicking; the party TP fallback comes next.
            return _mapExitPhase != MapExitPhase.Idle;
        }
        catch (Exception ex)
        {
            try { LogMessage("MapExit error: " + ex.Message, 5); } catch { }
            CancelMapExit("error: " + ex.Message);
            return false;
        }
    }

    private void OnLeaderVanished()
    {
        var portal = FindNearestHideoutExitPortal(_leaderVanishTracker.LastSeenPosition, out var portalDistance);
        var whyNot = MapExitRules.WhyNotTakePortal(
            MapExitEnabled,
            IsInMapArea(),
            IsZoneSettling,
            _leaderVanishTracker.LastSeenDistance,
            MapExitMaxLeaderDistance,
            portal == null ? float.MaxValue : portalDistance,
            MapExitRadius);

        if (whyNot == null && _mapExitPhase != MapExitPhase.Idle)
            whyNot = $"portal logic already {_mapExitPhase}";
        if (whyNot == null && _mapExitClicks >= MapExitMaxClicks)
            whyNot = $"click budget for this zone used ({_mapExitClicks})";

        WriteMapExitDebug($"leader vanished {_leaderVanishTracker.LastSeenDistance:0} from the follower; nearest hideout portal: " +
                          (portal == null ? "none" : $"'{portal.RenderName}' {portalDistance:0} from the leader's last position") +
                          $" -> {whyNot ?? "follow through the portal"}");
        if (whyNot != null)
            return;

        _mapExitPhase = MapExitPhase.Confirming;
        _mapExitPhaseStartedMs = _mapExitClockMs;
        _mapExitPortalId = portal.Id;
        _mapExitPortalName = portal.RenderName ?? string.Empty;
    }

    private void UpdateMapExitConfirming()
    {
        var goneMs = _leaderVanishTracker.AbsentForMs(_mapExitClockMs);
        if (goneMs < MapExitVanishConfirmMs)
        {
            SetRuntimeStatus("MapExit", $"leader left at a portal, confirming {goneMs / 1000:0.0}s", _mapExitPortalName);
            return;
        }

        var portal = FindTrackedMapExitPortal();
        if (portal == null)
        {
            EndMapExit("portal disappeared before the follower started");
            return;
        }

        // An earlier click in this zone (leader came back and left again): keep the full wait between clicks.
        if (_mapExitClockMs - _mapExitLastClickMs < MapExitRetryWaitMs)
        {
            _mapExitPhase = MapExitPhase.AwaitZoneChange;
            return;
        }

        StartMapExitApproach(portal, "leader stayed gone");
    }

    private void StartMapExitApproach(Entity portal, string why)
    {
        _mapExitPhase = MapExitPhase.Approach;
        _mapExitPhaseStartedMs = _mapExitClockMs;
        _mapExitRequeueAtMs = -1;

        _tasks.Clear();
        _tasks.Add(new TaskNode(portal.Pos, Settings.General.ClearPathDistance.Value, TaskNode.TaskNodeType.MapExitPortal));
        ResetPendingPortalClick();
        ResetPendingArenaTransitionClick();
        ResetTaskWatchdog();
        _pickUpManager?.Reset("MapExitPortal");
        ReleaseAllPluginInputsNow(force: true, reason: "Follower.MapExit.Start.Release");
        _nextBotAction = DateTime.Now.AddMilliseconds(SafeBotDelayMs(2));

        var distance = Distance2D(GameController.Player.Pos, portal.Pos);
        WriteMapExitDebug($"walk to '{_mapExitPortalName}' ({why}), distance {distance:0}, click {_mapExitClicks + 1} of max {MapExitMaxClicks}");
        if (_mapExitClicks == 0)
            try { LogMessage($"MapExit: leader left through '{_mapExitPortalName}', following through the portal", 3); } catch { }
    }

    private void UpdateMapExitApproach()
    {
        if (LeaderGoneMs() >= MapExitTpFallbackMs)
        {
            EndMapExit("party TP fallback time reached before the portal was clicked");
            return;
        }

        if (_tasks.Any(t => t.Type == TaskNode.TaskNodeType.MapExitPortal))
        {
            _mapExitRequeueAtMs = -1;
            return;
        }

        // The anti-stuck watchdog dropped the walk. Pause briefly, then walk again.
        var portal = FindTrackedMapExitPortal();
        if (portal == null)
        {
            EndMapExit("portal is gone");
            return;
        }

        SetRuntimeStatus("MapExit", "walk reset by anti-stuck, retrying", _mapExitPortalName);
        if (_mapExitRequeueAtMs < 0)
            _mapExitRequeueAtMs = _mapExitClockMs + MapExitRequeueDelayMs;
        else if (_mapExitClockMs >= _mapExitRequeueAtMs)
            StartMapExitApproach(portal, "walking again after anti-stuck reset");
    }

    private void UpdateMapExitAwait()
    {
        var sinceClickMs = _mapExitClockMs - _mapExitLastClickMs;
        if (sinceClickMs < MapExitRetryWaitMs)
        {
            SetRuntimeStatus("MapExit", $"portal clicked, waiting for zone change {sinceClickMs / 1000:0.0}s", _mapExitPortalName);
            return;
        }

        // Still the same zone and character after the whole wait (both would have reset this): the click missed.
        var portal = FindTrackedMapExitPortal();
        if (portal != null && MapExitRules.MayClickAgain(_mapExitClicks, MapExitMaxClicks, LeaderGoneMs(), MapExitTpFallbackMs))
        {
            StartMapExitApproach(portal, $"no zone change {sinceClickMs / 1000:0.0}s after click {_mapExitClicks}");
            return;
        }

        EndMapExit(portal == null
            ? "portal is gone"
            : _mapExitClicks >= MapExitMaxClicks
                ? $"no zone change after {_mapExitClicks} click(s)"
                : "party TP fallback time reached");
    }

    /// <summary>Task step: walk toward the portal, then hover its label and click once.</summary>
    private void ExecuteMapExitPortalTask(TaskNode task)
    {
        var now = DateTime.Now;
        if (_mapExitPhase != MapExitPhase.Approach)
        {
            _tasks.Remove(task);
            return;
        }

        var portal = FindTrackedMapExitPortal();
        if (portal == null)
        {
            EndMapExit("portal is gone");
            return;
        }

        var portalPos = portal.Pos;
        task.WorldPosition = portalPos;
        var distance = Distance2D(GameController.Player.Pos, portalPos);
        var target = distance <= MapExitClickRange ? FindMapExitPortalClickTarget(portal) : null;

        if (target == null && distance <= MapExitStandStillRange)
        {
            // Right at the portal but nothing clickable (no label, model not on screen): no input, the fallback follows.
            ResetPendingPortalClick();
            SetRuntimeStatus("MapExit", "at the portal, but no clickable label", _mapExitPortalName);
            _nextBotAction = now.AddSeconds(1);
            return;
        }

        if (target == null)
        {
            // Walk toward the portal like normal following, a bit slower.
            ResetPendingPortalClick();
            SetRuntimeStatus("MapExit", $"walking to portal, distance={distance:0}", _mapExitPortalName);
            TryMoveCursorForMovement(WorldToValidScreenPosition(portalPos));
            QueueMovementKeyTap(30, 25);
            _nextBotAction = now.AddMilliseconds(SafeBotDelayMs(3));
            return;
        }

        SetRuntimeStatus("MapExit", $"clicking portal {_mapExitClicks + 1}/{MapExitMaxClicks}, distance={distance:0}", _mapExitPortalName);
        if (!TryHoverThenClickPortal(target, target.ClickPosition, MapExitHoverDelayMs))
        {
            _nextBotAction = now.AddMilliseconds(SafeBotDelayMs(3));
            return;
        }

        if (_portalHoverClickAt != DateTime.MinValue)
        {
            // The cursor rests on the label; the click follows after the hover delay.
            _nextBotAction = now.AddMilliseconds(MapExitHoverDelayMs);
            return;
        }

        _mapExitClicks++;
        _mapExitLastClickMs = _mapExitClockMs;
        _mapExitPhase = MapExitPhase.AwaitZoneChange;
        _tasks.Remove(task);
        _nextBotAction = now.AddSeconds(1);
        WriteMapExitDebug($"clicked portal {(target.FromLabel ? "label" : "model")} (click {_mapExitClicks}/{MapExitMaxClicks}) at distance {distance:0}; " +
                          $"waiting {MapExitRetryWaitMs / 1000}s for the zone change");
    }

    private void EndMapExit(string reason)
    {
        RemoveMapExitTasks();
        _mapExitPhase = MapExitPhase.Done;
        WriteMapExitDebug($"stopped: {reason}; party TP fallback takes over");
        try { LogMessage($"MapExit: stopped ({reason}); party TP fallback takes over", 3); } catch { }
    }

    private void CancelMapExit(string reason)
    {
        if (_mapExitPhase == MapExitPhase.Idle)
            return;

        RemoveMapExitTasks();
        WriteMapExitDebug($"cancelled in phase {_mapExitPhase}: {reason}");
        _mapExitPhase = MapExitPhase.Idle;
    }

    private void RemoveMapExitTasks()
    {
        if (_tasks.RemoveAll(t => t.Type == TaskNode.TaskNodeType.MapExitPortal) > 0)
            ResetPendingPortalClick();
        _mapExitRequeueAtMs = -1;
    }

    private double LeaderGoneMs()
    {
        if (_leaderVanishTracker.IsLeaderVisibleAt(_mapExitClockMs))
            return 0;

        // Counted from the last sighting, but never before the settle time of this zone ended.
        return Math.Max(0, _mapExitClockMs - Math.Max(_leaderVanishTracker.LastSeenMs, _mapExitSettleUntilMs));
    }

    /// <summary>Endgame map: not a town, hideout or other peaceful area, and not Vaal Ruins (handled by the Atziri logic).</summary>
    private bool IsInMapArea()
    {
        try
        {
            var area = GameController.Area.CurrentArea;
            if (area == null || area.IsTown || area.IsPeaceful || IsInHideout() || IsInAtziriEntranceArea())
                return false;

            // Endgame areas report act 10 (seen live); the known map names are a second opinion.
            return area.Act >= 10 || PartyTeleport.IsKnownMapName(area.Name);
        }
        catch
        {
            return false;
        }
    }

    private bool IsMapExitPortalCandidate(Entity entity)
    {
        try
        {
            if (entity == null || !entity.IsValid)
                return false;

            var portalLike = entity.Type == ExileCore2.Shared.Enums.EntityType.TownPortal ||
                             entity.Type == ExileCore2.Shared.Enums.EntityType.Portal ||
                             IsSupportedHideoutPortalMetadataPath(entity.Path);
            return portalLike && MapExitRules.IsHideoutPortalName(entity.RenderName);
        }
        catch
        {
            return false;
        }
    }

    private Entity FindNearestHideoutExitPortal(Vector3 position, out float distance)
    {
        distance = float.MaxValue;
        Entity best = null;
        foreach (var entity in _areaTransitions.Values.ToArray())
        {
            if (!IsMapExitPortalCandidate(entity))
                continue;

            float d;
            try { d = Distance2D(position, entity.Pos); }
            catch { continue; }

            if (d < distance)
            {
                distance = d;
                best = entity;
            }
        }

        return best;
    }

    private Entity FindTrackedMapExitPortal()
    {
        if (_mapExitPortalId == 0)
            return null;

        return _areaTransitions.TryGetValue(_mapExitPortalId, out var portal) && IsMapExitPortalCandidate(portal)
            ? portal
            : null;
    }

    private PortalTarget FindMapExitPortalClickTarget(Entity portal)
    {
        var portalId = portal.Id;
        var portalPos = portal.Pos;
        var label = FindNearestVisibleHideoutPortalLabel(target =>
            target.EntityId == portalId ||
            (target.WorldPosition != Vector3.Zero && Distance2D(target.WorldPosition, portalPos) <= MapExitLabelMatchDistance));
        if (label != null)
            return label;

        // No visible label: click the portal itself, but only when it is really on screen (never a clamped edge point).
        return IsWorldPositionClickable(portalPos) ? ToPortalTarget(portal) : null;
    }

    private bool IsWorldPositionClickable(Vector3 worldPos)
    {
        try
        {
            var point = Camera.WorldToScreen(worldPos);
            var window = GameController.Window.GetWindowRectangle();
            const float sideMargin = 60f;
            var bottomMargin = Math.Max(60f, window.Height * 0.18f); // keep clear of the skill bar
            return point.X >= sideMargin && point.Y >= sideMargin &&
                   point.X <= window.Width - sideMargin && point.Y <= window.Height - bottomMargin;
        }
        catch
        {
            return false;
        }
    }

    private static float Distance2D(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));

    private void WriteMapExitDebug(string line)
    {
        try
        {
            if (!(Settings.Debug.DebugMapExitPortalToTxt?.Value ?? false))
                return;

            var dir = Settings.Debug.AutoPartyDebugDirectory?.Value;
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Path.GetTempPath(), "FollowerDebug");

            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, MapExitDebugFileName),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{_mapExitClockMs / 1000:0.0}s] {line}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}

internal enum MapExitPhase
{
    Idle,
    Confirming,
    Approach,
    AwaitZoneChange,
    Done
}

/// <summary>Decisions of the map exit portal logic, kept free of game access so they can be tested.</summary>
internal static class MapExitRules
{
    internal const string HideoutPortalNameSuffix = "Hideout";

    internal static bool IsHideoutPortalName(string renderName) =>
        !string.IsNullOrWhiteSpace(renderName) &&
        renderName.Trim().EndsWith(HideoutPortalNameSuffix, StringComparison.OrdinalIgnoreCase);

    /// <returns>null when the follower should take the portal the leader vanished at, otherwise the reason not to.</returns>
    internal static string WhyNotTakePortal(bool enabled, bool inMap, bool settling, float leaderDistanceToFollower,
        float maxLeaderDistance, float portalDistanceToLeader, float portalRadius)
    {
        if (!enabled)
            return "map exit portal disabled";
        if (!inMap)
            return "not in a map";
        if (settling)
            return "zone settle time";
        if (leaderDistanceToFollower > maxLeaderDistance)
            return $"leader was {leaderDistanceToFollower:0} away when last seen (limit {maxLeaderDistance:0}), probably walked out of view";
        if (portalDistanceToLeader > portalRadius)
            return portalDistanceToLeader >= float.MaxValue
                ? "no hideout portal in this map"
                : $"leader vanished {portalDistanceToLeader:0} from the nearest hideout portal (limit {portalRadius:0})";
        return null;
    }

    /// <returns>null when the party teleport may act now, otherwise why it waits.</returns>
    internal static string WhyHoldTeleport(double settleLeftMs, bool mapExitEnabled, bool inMap, MapExitPhase phase,
        double msSinceClick, int retryWaitMs, bool leaderVisible, double leaderGoneMs, int fallbackMs)
    {
        if (settleLeftMs > 0)
            return $"zone settle, {settleLeftMs / 1000:0.0}s left";
        if (!mapExitEnabled || !inMap)
            return null;

        switch (phase)
        {
            case MapExitPhase.Confirming:
                return "leader left at a portal, confirming";
            case MapExitPhase.Approach:
                return "walking to the map exit portal";
            case MapExitPhase.AwaitZoneChange when msSinceClick < retryWaitMs:
                return "portal clicked, waiting for the zone change";
        }

        if (leaderVisible)
            return null;

        return leaderGoneMs < fallbackMs
            ? $"map: leader gone {leaderGoneMs / 1000:0}s, party TP fallback at {fallbackMs / 1000}s"
            : null;
    }

    /// <summary>After the full wait following a click: click again, or leave it to the party TP fallback.</summary>
    internal static bool MayClickAgain(int clicks, int maxClicks, double leaderGoneMs, int fallbackMs) =>
        clicks < maxClicks && leaderGoneMs < fallbackMs;
}

/// <summary>
/// Remembers where the leader was last seen. A disappearance only counts when the leader was visible in the previous
/// follow-planning tick and is gone now, without a pause in between. After a pause (dead, paused by command, pickup)
/// the old sighting is stale and is never treated as a fresh disappearance.
/// </summary>
internal sealed class LeaderVanishTracker
{
    private readonly double _maxTickGapMs;
    private double _lastUpdateMs = double.NaN;

    internal LeaderVanishTracker(double maxTickGapMs)
    {
        _maxTickGapMs = maxTickGapMs;
    }

    internal bool LeaderVisible { get; private set; }
    internal Vector3 LastSeenPosition { get; private set; }
    internal float LastSeenDistance { get; private set; }
    internal double LastSeenMs { get; private set; }
    internal bool LastUpdateWasContinuous { get; private set; }

    /// <summary>Start of the current absence, if it began right after a sighting (NaN otherwise).</summary>
    internal double VanishedAtMs { get; private set; } = double.NaN;

    internal double AbsentForMs(double nowMs) => double.IsNaN(VanishedAtMs) ? 0 : nowMs - VanishedAtMs;

    internal bool IsLeaderVisibleAt(double nowMs) =>
        LeaderVisible && !double.IsNaN(_lastUpdateMs) && nowMs - _lastUpdateMs <= _maxTickGapMs;

    /// <returns>true on the first tick without the leader right after a tick with the leader.</returns>
    internal bool Update(double nowMs, bool leaderVisible, Vector3 leaderPosition, float leaderDistance)
    {
        LastUpdateWasContinuous = !double.IsNaN(_lastUpdateMs) && nowMs - _lastUpdateMs <= _maxTickGapMs;
        _lastUpdateMs = nowMs;
        if (!LastUpdateWasContinuous)
        {
            LeaderVisible = false;
            VanishedAtMs = double.NaN;
        }

        if (leaderVisible)
        {
            LeaderVisible = true;
            LastSeenPosition = leaderPosition;
            LastSeenDistance = leaderDistance;
            LastSeenMs = nowMs;
            VanishedAtMs = double.NaN;
            return false;
        }

        var vanished = LeaderVisible;
        LeaderVisible = false;
        if (vanished)
            VanishedAtMs = nowMs;
        return vanished;
    }

    internal void Reset(double nowMs)
    {
        LeaderVisible = false;
        LastSeenMs = nowMs;
        VanishedAtMs = double.NaN;
        _lastUpdateMs = double.NaN;
    }
}
