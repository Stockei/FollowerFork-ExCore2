using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using ImGuiNET;
using ItemFilterLibrary;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Windows.Forms;
using ComponentStack = ExileCore2.PoEMemory.Components.Stack;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace Follower;

internal sealed class FollowerPickUpManager
{
    private const int HoverDelayMs = 85;
    private const int PostClickPickupSettleMs = 650;
    private const int MissingTargetRetryMs = 125;
    private const int MissingTargetGraceMs = 900;
    private const int MinMovementActionGapMs = 95;
    private const int MaxLabelsToInspect = 220;
    private const int PlayerAllocationOwnerTokenOffset = 0x1D0;
    private const int FixedScanIntervalMs = 250;
    private const int FixedPauseBetweenPickupClicksMs = 650;
    private const int FixedMinimumFreeInventorySlots = 0;
    private const int FixedFailedItemBlacklistMs = 12000;

    private readonly Follower _plugin;
    private readonly Random _random = new Random();
    private readonly Dictionary<uint, DateTime> _blacklistedUntilByEntityId = new Dictionary<uint, DateTime>();

    private PickupState _state = PickupState.Idle;
    private PickupTarget _currentTarget;
    private DateTime _nextScanAt = DateTime.MinValue;
    private DateTime _nextActionAt = DateTime.MinValue;
    private DateTime _activeStartedAt = DateTime.MinValue;
    private DateTime _hoverClickAt = DateTime.MinValue;
    private DateTime _lastPickupClickAt = DateTime.MinValue;
    private DateTime _targetMissingSince = DateTime.MinValue;
    private uint _hoverEntityId;

    private string _rulesSignature = string.Empty;
    private List<CompiledPickUpRule> _compiledRules = new List<CompiledPickUpRule>();
    private string _lastCompileErrors = string.Empty;
    private string _lastLoggedCompileErrors = string.Empty;

    private string _newRuleName = string.Empty;
    private readonly Dictionary<string, bool> _categoryOpenStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    private enum PickupState
    {
        Idle,
        MovingToItem,
        HoveringItem,
        WaitingAfterClick
    }

    public FollowerPickUpManager(Follower plugin)
    {
        _plugin = plugin;
    }

    public bool IsActive => _state != PickupState.Idle;

    public string Status => _state == PickupState.Idle
        ? "Idle"
        : $"{_state}: {_currentTarget?.DisplayName ?? "no target"} attempts={_currentTarget?.Attempts ?? 0}";

    private int GetPickupClickDelayMs()
    {
        try
        {
            var delayMs = Math.Clamp(
                _plugin.Settings?.PickUp?.PickupClickDelayMs?.Value ?? FixedPauseBetweenPickupClicksMs,
                90,
                750);

            var allowFastDelay = _plugin.Settings?.PickUp?.AllowPickupClickDelayBelowSafeMinimum?.Value ?? false;
            if (!allowFastDelay)
                delayMs = Math.Max(PostClickPickupSettleMs, delayMs);

            return delayMs;
        }
        catch
        {
            return FixedPauseBetweenPickupClicksMs;
        }
    }

    public void Reset(string reason)
    {
        _state = PickupState.Idle;
        _currentTarget = null;
        _nextActionAt = DateTime.MinValue;
        _activeStartedAt = DateTime.MinValue;
        _hoverClickAt = DateTime.MinValue;
        _lastPickupClickAt = DateTime.MinValue;
        _targetMissingSince = DateTime.MinValue;
        _hoverEntityId = 0;
        _plugin.ReleaseAllPluginInputsNow(force: true, reason: "Follower.PickUp.Reset." + reason);
    }

    public void DrawSettings()
    {
        var settings = _plugin.Settings.PickUp;
        EnsureRuleCategories(settings);

        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("PickUp - rule table"))
            return;

        DrawPickUpRuleCategories(settings);

        if (!string.IsNullOrWhiteSpace(_lastCompileErrors))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), "Rule compile errors:");
            ImGui.TextWrapped(_lastCompileErrors);
        }
    }

    private void DrawPickUpRuleCategories(PickUpSettings settings)
    {
        var categories = settings.RuleCategories ??= FollowerPickUpCategory.CreateDefaultCategories();
        for (var i = 0; i < categories.Count; i++)
        {
            var category = categories[i] ?? new FollowerPickUpCategory();
            categories[i] = category;
            category.Rules ??= new List<FollowerPickUpRule>();
            NormalizeRuleList(category.Rules);

            var enabledCount = category.Rules.Count(rule => rule?.Enabled == true);
            var header = $"{category.Name} ({enabledCount}/{category.Rules.Count})##FollowerPickUpCategory_{category.Name}";

            var categoryStateKey = GetCategoryStateKey(category, i);

            ImGui.PushID("FollowerPickUpCategory" + category.Name);
            if (_categoryOpenStates.TryGetValue(categoryStateKey, out var shouldBeOpen))
                ImGui.SetNextItemOpen(shouldBeOpen, ImGuiCond.Always);

            var isOpen = ImGui.CollapsingHeader(header);
            _categoryOpenStates[categoryStateKey] = isOpen;

            if (isOpen)
            {
                DrawCategoryToolbar(category, i);

                if (category.AllowCustomItems)
                    DrawOwnRuleAddLine(category.Rules, i);

                DrawCategoryRulesTable(category, i);
            }
            ImGui.PopID();
        }
    }

    private void DrawCategoryToolbar(FollowerPickUpCategory category, int categoryIndex)
    {
        ImGui.PushItemWidth(Math.Min(280f, Math.Max(140f, ImGui.GetContentRegionAvail().X * 0.35f)));
        var search = category.SearchText ?? string.Empty;
        if (ImGui.InputText("Search", ref search, 256))
        {
            category.SearchText = search;
            KeepCategoryOpen(categoryIndex);
        }
        ImGui.PopItemWidth();

        ImGui.SameLine();
        var showOnlyAllowed = category.ShowOnlyAllowed;
        if (ImGui.Checkbox("show only allowed", ref showOnlyAllowed))
        {
            category.ShowOnlyAllowed = showOnlyAllowed;
            KeepCategoryOpen(categoryIndex);
        }

        ImGui.SameLine();
        if (ImGui.Button("allow all"))
        {
            SetCategoryEnabled(category, true);
            KeepCategoryOpen(categoryIndex);
            ForceRecompileRules();
        }

        ImGui.SameLine();
        if (ImGui.Button("clear"))
        {
            SetCategoryEnabled(category, false);
            KeepCategoryOpen(categoryIndex);
            ForceRecompileRules();
        }
    }

    private void DrawOwnRuleAddLine(List<FollowerPickUpRule> rules, int categoryIndex)
    {
        ImGui.Spacing();
        ImGui.PushItemWidth(Math.Max(260f, ImGui.GetContentRegionAvail().X - 60f));
        ImGui.InputText("##FollowerPickUpOwnNewItemName", ref _newRuleName, 256);
        ImGui.PopItemWidth();
        ImGui.SameLine();

        if (ImGui.Button("Add") && !string.IsNullOrWhiteSpace(_newRuleName))
        {
            var itemName = _newRuleName.Trim();
            if (!rules.Any(rule => string.Equals(rule?.GetItemName(), itemName, StringComparison.OrdinalIgnoreCase)))
                rules.Add(new FollowerPickUpRule(itemName));

            _newRuleName = string.Empty;
            KeepCategoryOpen(categoryIndex);
            ForceRecompileRules();
        }
    }

    private void DrawCategoryRulesTable(FollowerPickUpCategory category, int categoryIndex)
    {
        var rules = category.Rules ??= new List<FollowerPickUpRule>();
        var isOwn = category.AllowCustomItems;
        var columns = isOwn ? 3 : 2;
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable;

        ImGui.Spacing();
        if (!ImGui.BeginTable($"FollowerPickUpCategoryRulesTable{categoryIndex}", columns, flags))
            return;

        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 38f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.None);
        if (isOwn)
            ImGui.TableSetupColumn("Del", ImGuiTableColumnFlags.WidthFixed, 42f);
        ImGui.TableHeadersRow();

        var removeIndex = -1;
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i] ?? new FollowerPickUpRule();
            rules[i] = rule;
            rule.NormalizeToBaseNameRule();

            if (!ShouldShowRule(category, rule))
                continue;

            ImGui.PushID(i);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            var enabled = rule.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
            {
                rule.Enabled = enabled;
                KeepCategoryOpen(categoryIndex);
                ForceRecompileRules();
            }

            ImGui.TableSetColumnIndex(1);
            var name = rule.GetItemName();
            if (isOwn)
            {
                ImGui.PushItemWidth(-1f);
                if (ImGui.InputText("##name", ref name, 256))
                {
                    rule.Name = name.Trim();
                    rule.Expression = FollowerPickUpRule.BuildBaseNameExpression(rule.Name);
                    KeepCategoryOpen(categoryIndex);
                    ForceRecompileRules();
                }
                ImGui.PopItemWidth();
            }
            else
            {
                ImGui.TextUnformatted(name);
            }

            if (isOwn)
            {
                ImGui.TableSetColumnIndex(2);
                if (ImGui.SmallButton("X"))
                {
                    removeIndex = i;
                    KeepCategoryOpen(categoryIndex);
                }
            }

            ImGui.PopID();
        }

        ImGui.EndTable();

        if (removeIndex >= 0 && removeIndex < rules.Count)
        {
            rules.RemoveAt(removeIndex);
            KeepCategoryOpen(categoryIndex);
            ForceRecompileRules();
        }
    }

    private void KeepCategoryOpen(int categoryIndex)
    {
        var categories = _plugin.Settings.PickUp.RuleCategories;
        if (categories == null || categoryIndex < 0 || categoryIndex >= categories.Count)
            return;

        _categoryOpenStates[GetCategoryStateKey(categories[categoryIndex], categoryIndex)] = true;
    }

    private static string GetCategoryStateKey(FollowerPickUpCategory category, int fallbackIndex)
    {
        var name = category?.Name;
        return string.IsNullOrWhiteSpace(name) ? "Category_" + fallbackIndex : name.Trim();
    }

    private static bool ShouldShowRule(FollowerPickUpCategory category, FollowerPickUpRule rule)
    {
        if (rule == null)
            return false;

        if (category.ShowOnlyAllowed && !rule.Enabled)
            return false;

        var search = category.SearchText?.Trim();
        if (string.IsNullOrWhiteSpace(search))
            return true;

        return rule.GetItemName().Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static void SetCategoryEnabled(FollowerPickUpCategory category, bool enabled)
    {
        if (category?.Rules == null)
            return;

        foreach (var rule in category.Rules)
        {
            if (rule != null)
                rule.Enabled = enabled;
        }
    }

    private static void NormalizeRuleList(List<FollowerPickUpRule> rules)
    {
        if (rules == null)
            return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = rules.Count - 1; i >= 0; i--)
        {
            var rule = rules[i] ?? new FollowerPickUpRule();
            rule.NormalizeToBaseNameRule();
            rules[i] = rule;

            var itemName = rule.GetItemName();
            if (string.IsNullOrWhiteSpace(itemName) || !seen.Add(itemName))
                rules.RemoveAt(i);
        }
    }

    public bool Tick()
    {
        using var __profileScope = _plugin.ProfileScope("Follower.PickUp.Tick");

        var settings = _plugin.Settings.PickUp;
        if (!(settings.Enabled?.Value ?? false))
        {
            if (_state != PickupState.Idle)
                Reset("Disabled");
            return false;
        }

        if (!IsSafeAutomationContext())
        {
            if (_state != PickupState.Idle)
                Reset("UnsafeContext");
            return false;
        }

        PruneBlacklist();
        CompileRulesIfNeeded();
        LogCompileErrorsIfNeeded();

        var now = DateTime.Now;
        if (_state == PickupState.Idle)
            return TickIdle(now);

        return TickActive(now);
    }

    private bool TickIdle(DateTime now)
    {
        if (now < _nextScanAt)
            return false;

        _nextScanAt = now.AddMilliseconds(FixedScanIntervalMs);

        if (!IsPickUpEverythingEnabled() && _compiledRules.Count == 0)
            return false;

        if (!CanStartPickupNearLeader())
            return false;

        var target = FindBestTarget();
        if (target == null)
            return false;

        _currentTarget = target;
        _state = PickupState.MovingToItem;
        _activeStartedAt = now;
        _nextActionAt = now;
        _hoverClickAt = DateTime.MinValue;
        _lastPickupClickAt = DateTime.MinValue;
        _targetMissingSince = DateTime.MinValue;
        _hoverEntityId = 0;

        _plugin.ReleaseAllPluginInputsNow(force: true, reason: "Follower.PickUp.Start.ReleaseFollowMovement");
        try { _plugin.LogMessage($"PickUp: queued {target.DisplayName} via {target.RuleName}; dist={target.Distance:0}.", 3); } catch { }
        return true;
    }

    private bool TickActive(DateTime now)
    {
        if (_currentTarget == null)
        {
            Reset("NoCurrentTarget");
            return false;
        }

        if ((now - _activeStartedAt).TotalMilliseconds > Math.Max(1000, _plugin.Settings.PickUp.MaxActivePickupMs.Value))
        {
            BlacklistCurrent("active timeout");
            Reset("ActiveTimeout");
            return false;
        }

        if (now < _nextActionAt)
            return true;

        var refreshed = FindTargetByEntityId(_currentTarget.EntityId);
        if (refreshed == null)
            return TickMissingTarget(now);

        refreshed.Attempts = _currentTarget.Attempts;
        _currentTarget = refreshed;
        _targetMissingSince = DateTime.MinValue;

        var distance = refreshed.Distance;
        var maxRange = Math.Max(50, _plugin.Settings.PickUp.ItemPickupRange.Value);
        if (distance > maxRange * 1.35f)
        {
            BlacklistCurrent("moved out of range");
            Reset("OutOfRange");
            return false;
        }

        if (_state == PickupState.WaitingAfterClick && _currentTarget.Attempts >= Math.Max(1, _plugin.Settings.PickUp.MaxAttemptsPerItem.Value))
        {
            BlacklistCurrent("max attempts and item still visible");
            Reset("MaxAttempts");
            return false;
        }

        // Prefer clicking the ground label whenever it is visible. The client will path to the item,
        // and the follower keeps normal following suppressed until the pickup finishes or times out.
        if (IsLabelClickable(_currentTarget.LabelElement, _currentTarget.ClientRect))
            return HoverAndClickCurrentTarget(now);

        var clickDistance = Math.Max(10, _plugin.Settings.PickUp.ItemClickDistance.Value);
        if (distance > clickDistance)
            return MoveTowardCurrentTarget(now);

        _currentTarget.Attempts++;
        if (_currentTarget.Attempts >= Math.Max(1, _plugin.Settings.PickUp.MaxAttemptsPerItem.Value))
        {
            BlacklistCurrent("label not clickable");
            Reset("LabelNotClickable");
            return false;
        }

        _nextActionAt = now.AddMilliseconds(GetPickupClickDelayMs());
        return true;
    }

    private bool TickMissingTarget(DateTime now)
    {
        if (_lastPickupClickAt != DateTime.MinValue && (now - _lastPickupClickAt).TotalMilliseconds >= 250)
        {
            CompletePickup("item disappeared after click");
            return false;
        }

        if (_targetMissingSince == DateTime.MinValue)
            _targetMissingSince = now;

        if ((now - _targetMissingSince).TotalMilliseconds < MissingTargetGraceMs)
        {
            _nextActionAt = now.AddMilliseconds(MissingTargetRetryMs);
            return true;
        }

        if (_lastPickupClickAt != DateTime.MinValue)
        {
            CompletePickup("item disappeared");
            return false;
        }

        BlacklistCurrent("target label lost before click");
        Reset("TargetLostBeforeClick");
        return false;
    }

    private bool MoveTowardCurrentTarget(DateTime now)
    {
        _state = PickupState.MovingToItem;
        var screenPos = _plugin.WorldToValidScreenPositionForAutomation(_currentTarget.WorldPosition);
        _plugin.TryMoveCursorForAutomation(screenPos);
        _plugin.QueueMovementKeyTapForAutomation(30, 25);
        _nextActionAt = now.AddMilliseconds(MinMovementActionGapMs + _random.Next(45));
        return true;
    }

    private bool HoverAndClickCurrentTarget(DateTime now)
    {
        _state = PickupState.HoveringItem;

        if (!IsLabelClickable(_currentTarget.LabelElement, _currentTarget.ClientRect))
        {
            _currentTarget.Attempts++;
            if (_currentTarget.Attempts >= Math.Max(1, _plugin.Settings.PickUp.MaxAttemptsPerItem.Value))
            {
                BlacklistCurrent("label not clickable");
                Reset("LabelNotClickable");
                return false;
            }

            _nextActionAt = now.AddMilliseconds(GetPickupClickDelayMs());
            return true;
        }

        var clickPos = GetClickPosition(_currentTarget.LabelElement, _currentTarget.ClientRect);
        if (clickPos == Vector2.Zero)
        {
            BlacklistCurrent("invalid click position");
            Reset("InvalidClickPosition");
            return false;
        }

        if (_hoverEntityId != _currentTarget.EntityId || _hoverClickAt == DateTime.MinValue)
        {
            _plugin.PrepareForPluginMouseAction("Follower.PickUp.Hover.Prepare");
            if (!Mouse.IsGuardLocked)
                Mouse.SetCursorPosHuman2(clickPos);
            _hoverEntityId = _currentTarget.EntityId;
            _hoverClickAt = now.AddMilliseconds(HoverDelayMs);
            _nextActionAt = now.AddMilliseconds(HoverDelayMs);
            return true;
        }

        if (now < _hoverClickAt)
            return true;

        _plugin.PrepareForPluginMouseAction("Follower.PickUp.Click.Prepare");
        if (!Mouse.IsGuardLocked)
            Mouse.SetCursorPosAndLeftClickHuman(clickPos, 0);
        _plugin.CompletePluginMouseAction("Follower.PickUp.Click.Complete");

        _currentTarget.Attempts++;
        _lastPickupClickAt = now;
        _targetMissingSince = DateTime.MinValue;
        _state = PickupState.WaitingAfterClick;
        _nextActionAt = now.AddMilliseconds(GetPickupClickDelayMs());
        _hoverClickAt = DateTime.MinValue;
        _hoverEntityId = 0;
        return true;
    }

    private void CompletePickup(string reason)
    {
        try { _plugin.LogMessage("PickUp: finished, " + reason + "; resuming follow.", 3); } catch { }
        Reset("Complete");
        _nextScanAt = DateTime.MinValue;
    }

    private bool IsSafeAutomationContext()
    {
        try
        {
            if (!_plugin.GameController.Window.IsForeground())
                return false;

            if (_plugin.GameController.Player == null || !_plugin.GameController.Player.IsAlive)
                return false;

            var area = _plugin.GameController.Area?.CurrentArea;
            if (area != null && (area.IsTown || area.IsHideout))
                return false;

            if ((_plugin.Settings.PickUp.NoPickupWhileEnemyClose?.Value ?? false) && AnyNearbyMonsters())
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool CanStartPickupNearLeader()
    {
        if (!(_plugin.Settings.PickUp.RequireLeaderNearbyToStart?.Value ?? true))
            return true;

        var leaderName = _plugin.Settings.General.LeaderName.Value?.Trim();
        if (string.IsNullOrWhiteSpace(leaderName))
            return false;

        var leader = FindLeaderEntity(leaderName);
        if (leader == null)
            return false;

        return Vector3.Distance(_plugin.GameController.Player.Pos, leader.Pos) <= Math.Max(100, _plugin.Settings.PickUp.MaxDistanceFromLeaderToStart.Value);
    }

    private Entity FindLeaderEntity(string leaderName)
    {
        IEnumerable<Entity> entities = null;
        try { entities = _plugin.GameController.EntityListWrapper?.Entities; } catch { }
        var leader = FindLeaderIn(entities, leaderName);
        if (leader != null)
            return leader;

        try { entities = _plugin.GameController.Entities; } catch { entities = null; }
        return FindLeaderIn(entities, leaderName);
    }

    private static Entity FindLeaderIn(IEnumerable<Entity> entities, string leaderName)
    {
        if (entities == null)
            return null;

        Entity[] snapshot;
        try { snapshot = entities.ToArray(); }
        catch { return null; }

        foreach (var entity in snapshot)
        {
            try
            {
                if (entity?.Type != EntityType.Player)
                    continue;

                var playerName = entity.GetComponent<Player>()?.PlayerName;
                if (string.Equals(playerName?.Trim(), leaderName, StringComparison.OrdinalIgnoreCase))
                    return entity;
            }
            catch
            {
            }
        }

        return null;
    }

    private bool AnyNearbyMonsters()
    {
        try
        {
            return _plugin.GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster]
                .Any(x => x?.GetComponent<Monster>() != null && x.IsValid && x.IsHostile && x.IsAlive && !x.IsHidden &&
                          Vector3.Distance(_plugin.GameController.Player.Pos, x.GetComponent<Render>().Pos) < _plugin.Settings.PickUp.MonsterCheckRange.Value);
        }
        catch
        {
            return false;
        }
    }

    private PickupTarget FindBestTarget()
    {
        PickupTarget best = null;
        foreach (var target in EnumerateMatchingTargets())
        {
            if (best == null || target.Distance < best.Distance)
                best = target;
        }

        return best;
    }

    private PickupTarget FindTargetByEntityId(uint entityId)
    {
        foreach (var target in EnumerateMatchingTargets())
        {
            if (target.EntityId == entityId)
            {
                target.Attempts = _currentTarget?.Attempts ?? 0;
                return target;
            }
        }

        return null;
    }

    private IEnumerable<PickupTarget> EnumerateMatchingTargets()
    {
        var labels = GetVisibleGroundLabels();
        if (labels == null)
            yield break;

        var inspected = 0;
        foreach (var raw in labels)
        {
            if (++inspected > MaxLabelsToInspect)
                yield break;

            var target = TryCreateTarget(raw);
            if (target != null)
                yield return target;
        }
    }

    private IEnumerable GetVisibleGroundLabels()
    {
        try
        {
            dynamic ingameUi = _plugin.GameController.Game?.IngameState?.IngameUi ?? _plugin.GameController.IngameState?.IngameUi;
            if (ingameUi == null)
                return null;

            dynamic labelsElement = null;
            try { labelsElement = ingameUi.ItemsOnGroundLabelElement; } catch { labelsElement = null; }

            object visible = null;
            try { visible = labelsElement?.VisibleGroundItemLabels; } catch { visible = null; }
            if (visible is IEnumerable visibleEnumerable)
                return visibleEnumerable;

            try { visible = ingameUi.ItemsOnGroundLabelsVisible; } catch { visible = null; }
            if (visible is IEnumerable visibleGroundEnumerable)
                return visibleGroundEnumerable;

            try { visible = ingameUi.ItemsOnGroundLabels; } catch { visible = null; }
            return visible as IEnumerable;
        }
        catch
        {
            return null;
        }
    }

    private PickupTarget TryCreateTarget(object rawLabel)
    {
        try
        {
            dynamic raw = rawLabel;

            Entity groundEntity = null;
            try { groundEntity = (Entity)raw.Entity; } catch { }
            if (groundEntity == null)
            {
                try { groundEntity = (Entity)raw.ItemOnGround; } catch { }
            }

            if (groundEntity == null || !groundEntity.IsValid || groundEntity.IsHidden || !groundEntity.IsTargetable)
                return null;

            if (groundEntity.Path == null || groundEntity.Type != EntityType.WorldItem)
                return null;

            var distance = groundEntity.DistancePlayer;
            if (distance > Math.Max(50, _plugin.Settings.PickUp.ItemPickupRange.Value))
                return null;

            if (IsBlacklisted(groundEntity.Id))
                return null;

            var worldItem = groundEntity.GetComponent<WorldItem>();
            var itemEntity = worldItem?.ItemEntity;
            if (itemEntity == null || !itemEntity.IsValid)
                return null;

            object labelElement = null;
            try { labelElement = raw.Label; } catch { }
            if (labelElement == null)
                return null;

            RectangleF? clientRect = null;
            try { clientRect = (RectangleF)raw.ClientRect; } catch { }

            if (!IsLabelClickable(labelElement, clientRect))
                return null;

            if (IsAllocatedToOtherPlayer(groundEntity, itemEntity, labelElement, worldItem))
                return null;

            var itemData = new ItemData(itemEntity, groundEntity, _plugin.GameController);
            var ruleName = "PickUp Everything";
            if (!IsPickUpEverythingEnabled() && !MatchesRules(itemData, itemEntity, labelElement, out ruleName))
                return null;

            if (!CanFitInventory(itemData))
                return null;

            return new PickupTarget
            {
                EntityId = groundEntity.Id,
                GroundEntity = groundEntity,
                ItemEntity = itemEntity,
                LabelElement = labelElement,
                ClientRect = clientRect,
                WorldPosition = groundEntity.Pos,
                Distance = distance,
                DisplayName = GetItemDisplayName(itemData, itemEntity),
                RuleName = ruleName
            };
        }
        catch
        {
            return null;
        }
    }

    private bool IsPickUpEverythingEnabled()
    {
        try
        {
            return _plugin.Settings.PickUp.PickUpEverything?.Value ?? false;
        }
        catch
        {
            return false;
        }
    }


    private bool MatchesRules(ItemData itemData, Entity itemEntity, object labelElement, out string ruleName)
    {
        var candidateNames = BuildCandidateItemNames(itemData, itemEntity, labelElement);
        foreach (var compiledRule in _compiledRules)
        {
            if (MatchesCompiledRule(compiledRule, itemData, candidateNames))
            {
                ruleName = compiledRule.Name;
                return true;
            }
        }

        ruleName = string.Empty;
        return false;
    }

    private static bool MatchesCompiledRule(CompiledPickUpRule compiledRule, ItemData itemData, IReadOnlyList<string> candidateNames)
    {
        if (compiledRule == null)
            return false;

        if (compiledRule.Filter != null)
        {
            try
            {
                if (compiledRule.Filter.Matches(itemData))
                    return true;
            }
            catch
            {
            }
        }

        if (candidateNames == null || compiledRule.MatchNames == null)
            return false;

        foreach (var candidateName in candidateNames)
        foreach (var matchName in compiledRule.MatchNames)
        {
            if (IsDirectNameMatch(candidateName, matchName))
                return true;
        }

        return false;
    }

    private static bool IsDirectNameMatch(string candidateName, string matchName)
    {
        candidateName = NormalizeNameForComparison(candidateName);
        matchName = NormalizeNameForComparison(matchName);

        if (candidateName.Length == 0 || matchName.Length == 0)
            return false;

        if (candidateName.Equals(matchName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (matchName.Length >= 6 && candidateName.Contains(matchName, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private List<string> BuildCandidateItemNames(ItemData itemData, Entity itemEntity, object labelElement)
    {
        var names = new List<string>();

        try { AddCandidateName(names, itemData?.BaseName); } catch { }
        try { AddCandidateName(names, itemEntity?.RenderName); } catch { }
        try { AddCandidateName(names, UiTextOf(labelElement)); } catch { }
        try { AddCandidateName(names, itemEntity?.Path); } catch { }

        return names;
    }

    private static void AddCandidateName(List<string> names, string value)
    {
        var normalized = NormalizeNameForComparison(value);
        if (normalized.Length == 0)
            return;

        if (!names.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
            names.Add(normalized);
    }

    private static string NormalizeNameForComparison(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = NormalizeUiText(value)
            .Replace('’', '\'')
            .Replace("×", "x")
            .Trim();

        while (normalized.Contains("  "))
            normalized = normalized.Replace("  ", " ");

        return normalized;
    }

    private void CompileRulesIfNeeded()
    {
        var settings = _plugin.Settings.PickUp;
        EnsureRuleCategories(settings);

        var enabledRules = EnumerateEnabledRules(settings).ToList();
        var signature = string.Join("\n", enabledRules.Select((entry, index) =>
            $"{index}|{entry.CategoryName}|{entry.Rule.GetItemName()}|{entry.Rule.Expression}"));

        if (string.Equals(signature, _rulesSignature, StringComparison.Ordinal))
            return;

        _rulesSignature = signature;
        _compiledRules = new List<CompiledPickUpRule>();
        var errors = new List<string>();

        foreach (var entry in enabledRules)
        {
            var rule = entry.Rule;
            var itemName = rule.GetItemName();
            var expression = rule.Expression?.Trim() ?? string.Empty;
            ItemFilter filter = null;

            try
            {
                if (!string.IsNullOrWhiteSpace(expression))
                    filter = ItemFilter.LoadFromString(expression);
            }
            catch (Exception ex)
            {
                errors.Add($"{entry.CategoryName} / {itemName}: {ex.Message}");
            }

            _compiledRules.Add(new CompiledPickUpRule
            {
                Name = itemName,
                CategoryName = entry.CategoryName,
                Expression = expression,
                Filter = filter,
                MatchNames = rule.GetMatchNames()
            });
        }

        _lastCompileErrors = string.Join("\n", errors);
    }

    private static IEnumerable<(string CategoryName, FollowerPickUpRule Rule)> EnumerateEnabledRules(PickUpSettings settings)
    {
        foreach (var category in settings.RuleCategories ?? Enumerable.Empty<FollowerPickUpCategory>())
        {
            if (category?.Rules == null)
                continue;

            NormalizeRuleList(category.Rules);
            foreach (var rule in category.Rules)
            {
                if (rule?.Enabled == true && !string.IsNullOrWhiteSpace(rule.GetItemName()))
                    yield return (category.Name ?? string.Empty, rule);
            }
        }
    }

    private static void EnsureRuleCategories(PickUpSettings settings)
    {
        if (settings == null)
            return;

        var defaultCategories = FollowerPickUpCategory.CreateDefaultCategories();
        settings.RuleCategories ??= new List<FollowerPickUpCategory>();
        settings.RuleCategories = DeduplicateRuleCategories(settings.RuleCategories, defaultCategories);

        foreach (var defaultCategory in defaultCategories)
        {
            var existing = settings.RuleCategories.FirstOrDefault(category =>
                string.Equals(category?.Name, defaultCategory.Name, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                settings.RuleCategories.Add(defaultCategory);
                continue;
            }

            existing.Name = defaultCategory.Name;
            existing.AllowCustomItems = defaultCategory.AllowCustomItems;
            existing.Rules ??= new List<FollowerPickUpRule>();
            MergeMissingRules(existing.Rules, defaultCategory.Rules);
        }

        foreach (var category in settings.RuleCategories)
            NormalizeRuleList(category?.Rules);

        MigrateLegacyRules(settings);
        ClearOldAutoSampleSelectionsOnce(settings);
        OrderCategories(settings, defaultCategories);
    }

    private static readonly string[] OldAutoSampleRuleNames =
    {
        "Exalted Orb",
        "Orb of Chance",
        "Orb Of Chance",
        "Simulacrum Splinter",
        "Breach Precursor Tablet",
        "Tablet",
        "Precursor Tablet"
    };

    private static List<FollowerPickUpCategory> DeduplicateRuleCategories(
        List<FollowerPickUpCategory> categories,
        List<FollowerPickUpCategory> defaultCategories)
    {
        if (categories == null || categories.Count == 0)
            return categories ?? new List<FollowerPickUpCategory>();

        var defaultNamesByKey = defaultCategories
            .Where(category => !string.IsNullOrWhiteSpace(category?.Name))
            .GroupBy(category => category.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name.Trim(), StringComparer.OrdinalIgnoreCase);

        var byName = new Dictionary<string, FollowerPickUpCategory>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<FollowerPickUpCategory>();
        var unknownIndex = 0;

        foreach (var rawCategory in categories)
        {
            if (rawCategory == null)
                continue;

            var rawName = (rawCategory.Name ?? string.Empty).Trim();
            if (rawName.Length == 0)
                rawName = $"Custom {++unknownIndex}";

            var canonicalName = defaultNamesByKey.TryGetValue(rawName, out var defaultName) ? defaultName : rawName;
            rawCategory.Name = canonicalName;
            rawCategory.Rules ??= new List<FollowerPickUpRule>();

            if (byName.TryGetValue(canonicalName, out var existing))
            {
                MergeDuplicateCategory(existing, rawCategory);
                continue;
            }

            byName[canonicalName] = rawCategory;
            ordered.Add(rawCategory);
        }

        return ordered;
    }

    private static void MergeDuplicateCategory(FollowerPickUpCategory target, FollowerPickUpCategory duplicate)
    {
        if (target == null || duplicate == null)
            return;

        target.AllowCustomItems |= duplicate.AllowCustomItems;
        target.ShowOnlyAllowed |= duplicate.ShowOnlyAllowed;

        if (string.IsNullOrWhiteSpace(target.SearchText) && !string.IsNullOrWhiteSpace(duplicate.SearchText))
            target.SearchText = duplicate.SearchText;

        target.Rules ??= new List<FollowerPickUpRule>();
        duplicate.Rules ??= new List<FollowerPickUpRule>();
        MergeRuleSelections(target.Rules, duplicate.Rules);
    }

    private static void MergeRuleSelections(List<FollowerPickUpRule> targetRules, List<FollowerPickUpRule> sourceRules)
    {
        if (targetRules == null || sourceRules == null)
            return;

        foreach (var sourceRule in sourceRules)
        {
            if (sourceRule == null)
                continue;

            sourceRule.NormalizeToBaseNameRule();
            var itemName = sourceRule.GetItemName();
            if (string.IsNullOrWhiteSpace(itemName))
                continue;

            var existing = targetRules.FirstOrDefault(rule =>
                string.Equals(rule?.GetItemName(), itemName, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                targetRules.Add(new FollowerPickUpRule(itemName, enabled: sourceRule.Enabled));
                continue;
            }

            existing.Enabled |= sourceRule.Enabled;
            existing.NormalizeToBaseNameRule();
        }
    }

    private static void MergeMissingRules(List<FollowerPickUpRule> targetRules, List<FollowerPickUpRule> defaultRules)
    {
        if (targetRules == null || defaultRules == null)
            return;

        foreach (var defaultRule in defaultRules)
        {
            var itemName = defaultRule.GetItemName();
            if (targetRules.Any(rule => string.Equals(rule?.GetItemName(), itemName, StringComparison.OrdinalIgnoreCase)))
                continue;

            targetRules.Add(new FollowerPickUpRule(itemName, enabled: defaultRule.Enabled));
        }
    }

    private static void ClearOldAutoSampleSelectionsOnce(PickUpSettings settings)
    {
        if (settings == null || settings.AutoSampleRulesCleanupV5Done)
            return;

        foreach (var category in settings.RuleCategories ?? Enumerable.Empty<FollowerPickUpCategory>())
        {
            if (category?.Rules == null)
                continue;

            foreach (var rule in category.Rules)
            {
                if (rule == null)
                    continue;

                var itemName = rule.GetItemName();
                if (OldAutoSampleRuleNames.Any(sample => string.Equals(sample, itemName, StringComparison.OrdinalIgnoreCase)))
                    rule.Enabled = false;
            }
        }

        settings.AutoSampleRulesCleanupV4Done = true;
        settings.AutoSampleRulesCleanupV5Done = true;
    }

    private static void MigrateLegacyRules(PickUpSettings settings)
    {
        if (settings.LegacyRulesMigrated)
        {
            settings.Rules = new List<FollowerPickUpRule>();
            return;
        }

        if (settings.Rules == null || settings.Rules.Count == 0)
        {
            settings.LegacyRulesMigrated = true;
            return;
        }

        var ownCategory = settings.RuleCategories.FirstOrDefault(category =>
            string.Equals(category?.Name, "Own", StringComparison.OrdinalIgnoreCase));

        if (ownCategory == null)
        {
            ownCategory = new FollowerPickUpCategory("Own", Array.Empty<string>(), allowCustomItems: true);
            settings.RuleCategories.Add(ownCategory);
        }

        ownCategory.Rules ??= new List<FollowerPickUpRule>();

        foreach (var legacyRule in settings.Rules)
        {
            if (legacyRule == null)
                continue;

            legacyRule.NormalizeToBaseNameRule();
            var itemName = legacyRule.GetItemName();
            if (string.IsNullOrWhiteSpace(itemName))
                continue;

            var builtInRule = settings.RuleCategories
                .Where(category => !string.Equals(category?.Name, "Own", StringComparison.OrdinalIgnoreCase))
                .SelectMany(category => category?.Rules ?? Enumerable.Empty<FollowerPickUpRule>())
                .FirstOrDefault(rule => string.Equals(rule?.GetItemName(), itemName, StringComparison.OrdinalIgnoreCase));

            if (builtInRule != null)
            {
                builtInRule.Enabled |= legacyRule.Enabled;
                continue;
            }

            var existingOwnRule = ownCategory.Rules.FirstOrDefault(rule =>
                string.Equals(rule?.GetItemName(), itemName, StringComparison.OrdinalIgnoreCase));

            if (existingOwnRule != null)
                existingOwnRule.Enabled |= legacyRule.Enabled;
            else
                ownCategory.Rules.Add(new FollowerPickUpRule(itemName, enabled: legacyRule.Enabled));
        }

        settings.Rules = new List<FollowerPickUpRule>();
        settings.LegacyRulesMigrated = true;
    }

    private static void OrderCategories(PickUpSettings settings, List<FollowerPickUpCategory> defaultCategories)
    {
        var ordered = new List<FollowerPickUpCategory>();
        var defaultNames = defaultCategories.Select(category => category.Name).ToList();

        foreach (var defaultName in defaultNames)
        {
            var category = settings.RuleCategories.FirstOrDefault(existing =>
                string.Equals(existing?.Name, defaultName, StringComparison.OrdinalIgnoreCase));
            if (category != null && !ordered.Contains(category))
                ordered.Add(category);
        }

        foreach (var category in settings.RuleCategories)
        {
            if (category == null || ordered.Contains(category))
                continue;

            var ownIndex = ordered.FindIndex(existing => string.Equals(existing.Name, "Own", StringComparison.OrdinalIgnoreCase));
            if (ownIndex >= 0)
                ordered.Insert(ownIndex, category);
            else
                ordered.Add(category);
        }

        settings.RuleCategories = ordered;
    }

    private void ForceRecompileRules()
    {
        _rulesSignature = string.Empty;
        CompileRulesIfNeeded();
    }

    private void LogCompileErrorsIfNeeded()
    {
        if (string.IsNullOrWhiteSpace(_lastCompileErrors) || string.Equals(_lastCompileErrors, _lastLoggedCompileErrors, StringComparison.Ordinal))
            return;

        _lastLoggedCompileErrors = _lastCompileErrors;
        try { _plugin.LogMessage("PickUp rule compile errors: " + _lastCompileErrors, 5); } catch { }
    }

    private bool IsInventoryReserveReached()
    {
        try
        {
            var reserveCells = Math.Max(0, FixedMinimumFreeInventorySlots);
            if (reserveCells <= 0)
                return false;

            var inventory = GetPlayerInventory();
            if (inventory == null)
                return false;

            var occupied = GetContainer2DArray(inventory);
            if (occupied == null)
                return false;

            var free = 0;
            for (var y = 0; y < inventory.Rows; y++)
            for (var x = 0; x < inventory.Columns; x++)
            {
                if (!occupied[y, x])
                    free++;
                if (free > reserveCells)
                    return false;
            }

            return free <= reserveCells;
        }
        catch
        {
            return false;
        }
    }

    private bool CanFitInventory(ItemData item)
    {
        var inventory = GetPlayerInventory();
        if (inventory == null)
            return false;

        try
        {
            var inventoryItems = inventory.InventorySlotItems;
            if (inventoryItems.Any(x => CanItemBeStacked(item, x)))
                return true;

            var itemHeight = Math.Clamp(item.Height, 1, Math.Max(1, inventory.Rows));
            var itemWidth = Math.Clamp(item.Width, 1, Math.Max(1, inventory.Columns));
            if (WouldViolateInventoryReserve(inventory, itemHeight, itemWidth))
                return false;

            return FindSpotInventory(itemHeight, itemWidth, inventory) != null;
        }
        catch
        {
            return true;
        }
    }

    private bool WouldViolateInventoryReserve(ServerInventory inventory, int itemHeight, int itemWidth)
    {
        try
        {
            var reserveCells = Math.Max(0, FixedMinimumFreeInventorySlots);
            if (reserveCells <= 0)
                return false;

            var occupied = GetContainer2DArray(inventory);
            if (occupied == null)
                return false;

            var free = 0;
            for (var y = 0; y < inventory.Rows; y++)
            for (var x = 0; x < inventory.Columns; x++)
                if (!occupied[y, x])
                    free++;

            var neededCells = Math.Max(1, itemHeight * itemWidth);
            return free - neededCells <= reserveCells;
        }
        catch
        {
            return false;
        }
    }

    private ServerInventory GetPlayerInventory()
    {
        try
        {
            var inventories = _plugin.GameController.Game?.IngameState?.Data?.ServerData?.PlayerInventories;
            if (inventories == null || inventories.Count == 0)
                return null;

            return inventories[0].Inventory;
        }
        catch
        {
            return null;
        }
    }

    private Vector2? FindSpotInventory(int itemHeight, int itemWidth, ServerInventory inventory)
    {
        var inventorySlots = GetContainer2DArray(inventory);
        if (inventorySlots == null)
            return null;

        for (var y = 0; y <= inventory.Rows - itemHeight; y++)
        {
            for (var x = 0; x <= inventory.Columns - itemWidth; x++)
            {
                var obstructed = false;

                for (var xWidth = 0; xWidth < itemWidth && !obstructed; xWidth++)
                for (var yHeight = 0; yHeight < itemHeight && !obstructed; yHeight++)
                    obstructed |= inventorySlots[y + yHeight, x + xWidth];

                if (!obstructed)
                    return new Vector2(x, y);
            }
        }

        return null;
    }

    private static bool CanItemBeStacked(ItemData item, ServerInventory.InventSlotItem inventoryItem)
    {
        try
        {
            if (item.Entity.Path != inventoryItem.Item.Path)
                return false;

            if (!item.Entity.HasComponent<ComponentStack>() || !inventoryItem.Item.HasComponent<ComponentStack>())
                return false;

            var itemStackComp = item.Entity.GetComponent<ComponentStack>();
            var inventoryItemStackComp = inventoryItem.Item.GetComponent<ComponentStack>();

            return inventoryItemStackComp.Size + itemStackComp.Size <= inventoryItemStackComp.Info.MaxStackSize;
        }
        catch
        {
            return false;
        }
    }

    private bool[,] GetContainer2DArray(ServerInventory inventory)
    {
        var containerCells = new bool[inventory.Rows, inventory.Columns];

        try
        {
            foreach (var item in inventory.InventorySlotItems)
            {
                var startX = Math.Max(0, item.PosX);
                var startY = Math.Max(0, item.PosY);
                var endX = Math.Min(inventory.Columns, item.PosX + item.SizeX);
                var endY = Math.Min(inventory.Rows, item.PosY + item.SizeY);

                for (var y = startY; y < endY; y++)
                for (var x = startX; x < endX; x++)
                    containerCells[y, x] = true;
            }
        }
        catch (Exception ex)
        {
            try { _plugin.LogMessage("PickUp inventory fit check error: " + ex.Message, 5); } catch { }
        }

        return containerCells;
    }

    private bool IsLabelClickable(object labelElement, RectangleF? customRect)
    {
        if (labelElement == null || !IsVisibleElement(labelElement))
            return false;

        var rect = GetRect(labelElement, customRect);
        if (rect.Width <= 1 || rect.Height <= 1)
            return false;

        var center = rect.Center;
        try
        {
            var windowRect = _plugin.GameController.Window.GetWindowRectangleTimeCache;
            var localWindowRect = new RectangleF(0, 0, windowRect.Width, windowRect.Height);
            localWindowRect.Inflate(-36, -36);
            windowRect.Inflate(-36, -36);
            return localWindowRect.Contains(center.X, center.Y) || windowRect.Contains(center.X, center.Y);
        }
        catch
        {
            return center.X > 0 && center.Y > 0;
        }
    }

    private Vector2 GetClickPosition(object labelElement, RectangleF? customRect)
    {
        var rect = GetRect(labelElement, customRect);
        if (rect.Width <= 1 || rect.Height <= 1)
            return Vector2.Zero;

        var center = rect.Center;
        var offset = Math.Max(0, _plugin.Settings.General.RandomClickOffset.Value);
        var xJitter = offset > 0 ? _random.Next(-offset, offset + 1) : 0;
        var yJitter = offset > 0 ? _random.Next(-Math.Max(1, offset / 2), Math.Max(1, offset / 2) + 1) : 0;
        return new Vector2(center.X + xJitter, center.Y + yJitter);
    }

    private static RectangleF GetRect(object labelElement, RectangleF? customRect)
    {
        if (customRect.HasValue)
            return customRect.Value;

        try
        {
            dynamic element = labelElement;
            return (RectangleF)element.GetClientRect();
        }
        catch
        {
            return new RectangleF(0, 0, 0, 0);
        }
    }

    private static bool IsVisibleElement(object element)
    {
        try { if (GetBool(element, "IsVisible")) return true; } catch { }
        try { if (GetBool(element, "IsVisibleLocal")) return true; } catch { }
        return true;
    }

    private bool IsAllocatedToOtherPlayer(Entity groundEntity, Entity itemEntity, object labelElement, WorldItem worldItem)
    {
        // Prefer the fixed ExileCore2 PoE2 allocation data when available.
        // WorldItem.AllocatedToPlayer contains an allocation owner token. The local player's
        // matching token is stored on the Player component at +0x1D0 in the current ExileCore2 layout.
        // Do not rely on AllocatedToSomeoneElse alone: it can be true for any allocated item,
        // including loot allocated to this local client.
        if (TryIsWorldItemAllocatedToOtherByOwnerToken(worldItem, out var isAllocatedToOtherByToken))
            return isAllocatedToOtherByToken;

        var localPlayerName = GetLocalPlayerName();
        if (string.IsNullOrWhiteSpace(localPlayerName))
            return false;

        var labelText = NormalizeUiText(UiTextOf(labelElement));
        if (LooksLikeAllocationText(labelText))
            return !labelText.Contains(localPlayerName, StringComparison.OrdinalIgnoreCase);

        foreach (var source in new object[] { groundEntity, itemEntity, labelElement, worldItem })
        {
            if (TryReadAllocationOwner(source, out var ownerName) && !string.IsNullOrWhiteSpace(ownerName))
            {
                if (IsFreeForAllOwner(ownerName))
                    return false;

                return !ownerName.Contains(localPlayerName, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private bool TryIsWorldItemAllocatedToOtherByOwnerToken(WorldItem worldItem, out bool isAllocatedToOther)
    {
        isAllocatedToOther = false;

        if (worldItem == null)
            return false;

        if (!TryReadUInt32Property(worldItem, "AllocatedToPlayer", out var allocatedToPlayer))
            return false;

        // 0 means no owner token / public loot.
        if (allocatedToPlayer == 0)
        {
            isAllocatedToOther = false;
            return true;
        }

        // If the allocation is no longer active/public, do not block the item.
        if (!IsWorldItemAllocationStillReserved(worldItem))
        {
            isAllocatedToOther = false;
            return true;
        }

        if (TryReadLocalPlayerAllocationOwnerTokenLow32(out var localOwnerToken) && localOwnerToken != 0)
        {
            isAllocatedToOther = allocatedToPlayer != localOwnerToken;
            return true;
        }

        // If the local token cannot be resolved, keep the legacy behavior instead of hard-skipping.
        return false;
    }

    private bool IsWorldItemAllocationStillReserved(WorldItem worldItem)
    {
        if (TryReadBoolProperty(worldItem, "IsPermanentlyAllocated", out var isPermanentlyAllocated) && isPermanentlyAllocated)
            return true;

        if (TryReadInt32Property(worldItem, "AllocatedToOtherTime", out var allocatedToOtherTime) && allocatedToOtherTime > 0)
            return true;

        if (TryReadDateTimeProperty(worldItem, "PublicTime", out var publicTime) && publicTime > DateTime.Now)
            return true;

        if (TryReadBoolProperty(worldItem, "AllocatedToSomeoneElse", out var allocatedToSomeoneElse) && allocatedToSomeoneElse)
            return true;

        return false;
    }

    private bool TryReadLocalPlayerAllocationOwnerTokenLow32(out uint ownerTokenLow32)
    {
        ownerTokenLow32 = 0;

        try
        {
            var localPlayer = _plugin.GameController.Player;
            if (localPlayer == null)
                return false;

            var playerComponent = localPlayer.GetComponent<Player>();
            if (playerComponent == null)
                return false;

            var playerComponentAddress = TryGetInt64PropertyValue(playerComponent, "Address");
            if (playerComponentAddress <= 0)
                return false;

            return TryReadUInt32At(playerComponentAddress + PlayerAllocationOwnerTokenOffset, out ownerTokenLow32);
        }
        catch
        {
            ownerTokenLow32 = 0;
            return false;
        }
    }

    private bool TryReadUInt32At(long address, out uint value)
    {
        value = 0;
        if (address <= 0)
            return false;

        try
        {
            var bytes = _plugin.GameController.Memory.ReadBytes(address, 4);
            if (bytes == null || bytes.Length < 4)
                return false;

            value = BitConverter.ToUInt32(bytes, 0);
            return true;
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private static bool TryReadUInt32Property(object source, string propertyName, out uint value)
    {
        value = 0;
        try
        {
            var raw = source == null ? null : GetPropertyValue(source, propertyName);
            if (raw == null)
                return false;

            value = Convert.ToUInt32(raw);
            return true;
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private static bool TryReadInt32Property(object source, string propertyName, out int value)
    {
        value = 0;
        try
        {
            var raw = source == null ? null : GetPropertyValue(source, propertyName);
            if (raw == null)
                return false;

            value = Convert.ToInt32(raw);
            return true;
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private static bool TryReadBoolProperty(object source, string propertyName, out bool value)
    {
        value = false;
        try
        {
            var raw = source == null ? null : GetPropertyValue(source, propertyName);
            if (raw == null)
                return false;

            value = Convert.ToBoolean(raw);
            return true;
        }
        catch
        {
            value = false;
            return false;
        }
    }

    private static bool TryReadDateTimeProperty(object source, string propertyName, out DateTime value)
    {
        value = DateTime.MinValue;
        try
        {
            var raw = source == null ? null : GetPropertyValue(source, propertyName);
            if (raw == null)
                return false;

            if (raw is DateTime dateTime)
            {
                value = dateTime;
                return true;
            }

            return DateTime.TryParse(raw.ToString(), out value);
        }
        catch
        {
            value = DateTime.MinValue;
            return false;
        }
    }

    private static long TryGetInt64PropertyValue(object source, string propertyName)
    {
        try
        {
            var value = source == null ? null : GetPropertyValue(source, propertyName);
            if (value == null)
                return 0;

            return Convert.ToInt64(value);
        }
        catch
        {
            return 0;
        }
    }

    private string GetLocalPlayerName()
    {
        try { return _plugin.GameController.Player?.GetComponent<Player>()?.PlayerName; }
        catch { return null; }
    }

    private static bool LooksLikeAllocationText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("allocated", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("allocation", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("reserved", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("owner", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFreeForAllOwner(string ownerName)
    {
        return ownerName.Equals("None", StringComparison.OrdinalIgnoreCase) ||
               ownerName.Equals("FreeForAll", StringComparison.OrdinalIgnoreCase) ||
               ownerName.Equals("Free For All", StringComparison.OrdinalIgnoreCase) ||
               ownerName.Equals("FFA", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadAllocationOwner(object source, out string ownerName)
    {
        ownerName = null;
        if (source == null)
            return false;

        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var property in source.GetType().GetProperties(flags))
            {
                if (property.GetIndexParameters().Length != 0)
                    continue;

                var propertyName = property.Name;
                var isAllocationProperty =
                    propertyName.Contains("Alloc", StringComparison.OrdinalIgnoreCase) ||
                    propertyName.Equals("OwnerName", StringComparison.OrdinalIgnoreCase) ||
                    propertyName.Contains("AssignedTo", StringComparison.OrdinalIgnoreCase) ||
                    propertyName.Contains("ReservedFor", StringComparison.OrdinalIgnoreCase);

                if (!isAllocationProperty)
                    continue;

                object value;
                try { value = property.GetValue(source); }
                catch { continue; }

                if (value == null)
                    continue;

                if (value is string stringValue && !string.IsNullOrWhiteSpace(stringValue))
                {
                    ownerName = NormalizeUiText(stringValue);
                    return true;
                }

                var nestedName = TryReadNameLikeProperty(value);
                if (!string.IsNullOrWhiteSpace(nestedName))
                {
                    ownerName = NormalizeUiText(nestedName);
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static string TryReadNameLikeProperty(object source)
    {
        if (source == null)
            return null;

        foreach (var name in new[] { "PlayerName", "Name", "OwnerName", "AllocatedTo", "AllocatedToPlayer" })
        {
            try
            {
                var property = source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.GetValue(source) is string value && !string.IsNullOrWhiteSpace(value))
                    return value;
            }
            catch
            {
            }
        }

        return null;
    }

    private static string UiTextOf(object element)
    {
        if (element == null)
            return null;

        try
        {
            string textNoTags = null;
            try { textNoTags = (string)GetPropertyValue(element, "TextNoTags"); } catch { }
            if (!string.IsNullOrWhiteSpace(textNoTags))
                return textNoTags;

            string text = null;
            try { text = (string)GetPropertyValue(element, "Text"); } catch { }
            return text;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeUiText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

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

    private static object GetPropertyValue(object source, string propertyName)
    {
        var property = source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return property?.GetValue(source);
    }

    private static bool GetBool(object source, string propertyName)
    {
        try
        {
            var value = GetPropertyValue(source, propertyName);
            return value is bool b && b;
        }
        catch
        {
            return false;
        }
    }

    private string GetItemDisplayName(ItemData itemData, Entity itemEntity)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(itemData.BaseName))
                return itemData.BaseName;
        }
        catch
        {
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(itemEntity.RenderName))
                return itemEntity.RenderName;
        }
        catch
        {
        }

        return itemEntity?.Path ?? "item";
    }

    private bool IsBlacklisted(uint entityId)
    {
        return _blacklistedUntilByEntityId.TryGetValue(entityId, out var until) && until > DateTime.Now;
    }

    private void BlacklistCurrent(string reason)
    {
        if (_currentTarget == null)
            return;

        _blacklistedUntilByEntityId[_currentTarget.EntityId] = DateTime.Now.AddMilliseconds(FixedFailedItemBlacklistMs);
        try { _plugin.LogMessage($"PickUp: skipped {_currentTarget.DisplayName}; {reason}.", 3); } catch { }
    }

    private void PruneBlacklist()
    {
        if (_blacklistedUntilByEntityId.Count == 0)
            return;

        var now = DateTime.Now;
        foreach (var entityId in _blacklistedUntilByEntityId.Where(x => x.Value <= now).Select(x => x.Key).ToList())
            _blacklistedUntilByEntityId.Remove(entityId);
    }

    private sealed class PickupTarget
    {
        public uint EntityId { get; set; }
        public Entity GroundEntity { get; set; }
        public Entity ItemEntity { get; set; }
        public object LabelElement { get; set; }
        public RectangleF? ClientRect { get; set; }
        public Vector3 WorldPosition { get; set; }
        public float Distance { get; set; }
        public int Attempts { get; set; }
        public string DisplayName { get; set; }
        public string RuleName { get; set; }
    }

    private sealed class CompiledPickUpRule
    {
        public string Name { get; set; }
        public string CategoryName { get; set; }
        public string Expression { get; set; }
        public ItemFilter Filter { get; set; }
        public List<string> MatchNames { get; set; } = new List<string>();
    }
}
