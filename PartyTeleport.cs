
using System;
using System.Numerics;
using System.Collections.Generic;
using ExileCore2;
using ExileCore2.Shared;

namespace Follower
{

    internal sealed class PartyTeleport
    {
        private readonly Follower _plugin;

        private DateTime _nextAttempt = DateTime.UtcNow.AddMilliseconds(-500);
        private DateTime _nextConfirmCheck = DateTime.UtcNow.AddMilliseconds(-250);
        private int _retryCount;
        private bool _hideoutTpDone;
        private bool _wasInHideout;
        private string _lastLeader = string.Empty;

        // Pending TP verification
        private bool _tpPending;
        private string _tpStartAreaName = string.Empty;
        private DateTime _tpStartTime;
        private int _tpAttemptCount;

        public PartyTeleport(Follower plugin) { _plugin = plugin; }

        public string Status { get; private set; } = "Idle";

        private dynamic UI => _plugin.GameController.IngameState?.IngameUi;

        // PoE2 map names (treated as "leader is in a map")
        private static readonly string[] PoE2MapNames = new [] {
            "Alpine Ridge","Augury","Azmerian Ranges","Backwash","Bastille","Bloodwood","Blooming Field","Bluff",
            "Burial Bog","Caldera","Canyon","Castaway","Cenotes","Channel","Cliffside","Confluence","Creek",
            "Crimson Shores","Crypt","Decay","Derelict Mansion","Deserted","Digsite","Epitaph","Farmlands",
            "Flotsam","Forge","Fortress","Frozen Falls","Grimhaven","Headland","Hidden Grotto","Hive","Ice Cave",
            "Inferno","Lofty Summit","Lost Towers","Marrow","Merchant's Campsite","Mesa","Mineshaft","Mire",
            "Molten Vault","Moment of Zen","Necropolis","Oasis","Ornate Chambers","Outlands","Overgrown",
            "Penitentiary","Ravine","Razed Fields","Riverhold","Riverside","Rockpools","Rugosa","Rupture",
            "Rustbowl","Sacred Reservoir","Sandspit","Savannah","Sealed Vault","Seepage","Sinkhole",
            "Sinking Spire","Slick","Spider Woods","Spring","Steaming Springs","Steppe","Stronghold",
            "Sulphuric Caverns","Sump","Sun Temple","The Assembly","The Ezomyte Megaliths","The Fractured Lake",
            "The Jade Isles","The Silent Cave","The Viridian Wildwood","The Ziggurat Refuge","Trenches",
            "Untainted Paradise","Vaal City","Vaal Village","Vaults of Kamasa","Wayward Isle","Wetlands","Willow","Woodland","Atziri's Temple","Abyssal Depths"
        };

        public bool Tick()
        {
            using var __profileScope = _plugin.ProfileScope("PartyTeleport.Tick.Total");
            var s = _plugin.Settings;
            Status = "Idle";
            if (!s.Enable)
            {
                Status = "plugin disabled";
                return false;
            }

            // Pending TP handling: check success or retry. While pending, block follow/pickup input.
            if (_tpPending)
            {
                string leaderNow = (s.General.LeaderName?.Value ?? string.Empty);
                Status = $"pending confirmation/check attempt={_tpAttemptCount}";

                if (IsLoading() || AreaNameChangedSinceAttempt() || InSameAreaAsLeader(leaderNow))
                {
                    _tpPending = false;
                    _tpAttemptCount = 0;
                    _retryCount = 0;
                    Status = "teleport completed or area changed";
                    return false;
                }

                var elapsed = (DateTime.UtcNow - _tpStartTime).TotalMilliseconds;
                if (elapsed >= s.TpTrade.TpConfirmTimeoutMs.Value)
                {
                    if (_tpAttemptCount < s.TpTrade.TpConfirmRetries.Value)
                    {
                        // Re-try: click OK if dialog is open, else open dialog again.
                        if (TryConfirmTeleportDialog())
                        {
                            Status = "retry: confirmed teleport dialog";
                            return true;
                        }

                        if (TryClickPartyTp(leaderNow))
                        {
                            Status = "retry: clicked party TP";
                            return true;
                        }
                    }
                    else
                    {
                        _tpPending = false; // give up for now
                        _tpAttemptCount = 0;
                        Status = "confirm timeout; giving up";
                        return false;
                    }
                }

                return true;
            }

            if (!s.TpTrade.TeleportToLeader.Value)
            {
                Status = "disabled by setting";
                return false;
            }

            // Keep confirming dialog fast if it exists.
            if (s.TpTrade.AutoConfirmTeleportDialog.Value && TryConfirmTeleportDialog())
            {
                Status = "confirmed teleport dialog";
                return true;
            }

            // Throttle TP attempts.
            if ((DateTime.UtcNow - _nextAttempt).TotalMilliseconds < s.TpTrade.TpPollMs.Value)
            {
                Status = "throttled";
                return false;
            }
            _nextAttempt = DateTime.UtcNow;

            var area = _plugin.GameController.Area?.CurrentArea;
            bool inAnyHideout = false;
            try { inAnyHideout = area != null && (area.IsHideout || (area.Name?.IndexOf("Hideout", StringComparison.OrdinalIgnoreCase) >= 0)); } catch { }

            if (_wasInHideout && !inAnyHideout) _hideoutTpDone = false;
            _wasInHideout = inAnyHideout;

            var leader = (s.General.LeaderName?.Value ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(leader) && !string.Equals(_lastLeader, leader, StringComparison.OrdinalIgnoreCase))
            {
                _lastLeader = leader;
                _hideoutTpDone = false;
            }
            if (string.IsNullOrEmpty(leader))
            {
                Status = "no leader configured";
                return false;
            }

            if (InSameAreaAsLeader(leader))
            {
                _retryCount = 0;
                Status = "leader already in same area";
                return false;
            }

            string rowText = GetLeaderRowText(leader) ?? string.Empty;
            string rowLower = rowText.ToLowerInvariant();
            bool leaderInHideout = rowText.IndexOf("Hideout", StringComparison.OrdinalIgnoreCase) >= 0;
            bool leaderInMap = false;
            for (int i = 0; i < PoE2MapNames.Length; i++)
            {
                var nm = PoE2MapNames[i];
                if (nm.Length > 0 && rowLower.Contains(nm.ToLowerInvariant())) { leaderInMap = true; break; }
            }

            // In your hideout: allow a single TP to leader HO if enabled; otherwise don't spam.
            if (inAnyHideout)
            {
                if (s.TpTrade.TeleportFromHideout.Value && !_hideoutTpDone && leaderInHideout)
                {
                    if (TryClickPartyTp(leader))
                    {
                        _hideoutTpDone = true;
                        _retryCount++;
                        Status = "clicked TP from hideout to leader hideout";
                        return true;
                    }
                }

                Status = "in hideout; no TP action";
                return false;
            }

            // If leader is in a map (PoE2), don't TP; use follow/portal.
            if (leaderInMap)
            {
                _retryCount = 0;
                Status = "leader in map; use portal/follow";
                return false;
            }

            // From acts/towns/etc -> try TP.
            if (TryClickPartyTp(leader))
            {
                _retryCount++;
                Status = $"clicked party TP retry={_retryCount}";
                if (_retryCount > s.TpTrade.TpMaxRetries.Value) { _retryCount = 0; }
                return true;
            }

            if (_retryCount > s.TpTrade.TpMaxRetries.Value) { _retryCount = 0; }
            Status = "no usable TP button";
            return false;
        }

        private string GetLeaderRowText(string leaderName)
        {
            using var __profileScope = _plugin.ProfileScope("PartyTeleport.GetLeaderRowText");
            try
            {
                var row = FindLeaderPartyRow(leaderName);
                return row == null ? null : CollectDirectPartyRowText(row);
            }
            catch { return null; }
        }

        private string CollectDirectPartyRowText(dynamic row)
        {
            using var __profileScope = _plugin.ProfileScope("PartyTeleport.CollectDirectPartyRowText");
            try
            {
                if (row == null) return null;

                var name = TextOf(GetChild(row, 0));
                var location = TextOf(GetChild(row, 3));

                if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(location))
                    return string.Concat(name ?? string.Empty, " ", location ?? string.Empty).Trim();

                return CollectAllText(row);
            }
            catch { return null; }
        }

        private string CollectAllText(dynamic node)
        {
            using var __profileScope = _plugin.ProfileScope("PartyTeleport.CollectAllText");
            try
            {
                string acc = string.Empty;
                string t = null; try { t = (string)node.Text; } catch { }
                if (!string.IsNullOrEmpty(t)) acc += t + " ";
                dynamic kids = null; try { kids = node.Children; } catch { }
                int n = 0; try { n = (int)kids.Count; } catch { }
                for (int i = 0; i < n; i++)
                {
                    dynamic ch = null; try { ch = kids[i]; } catch { }
                    if (ch == null) continue;
                    var s = CollectAllText(ch);
                    if (!string.IsNullOrEmpty(s)) acc += s + " ";
                }
                return acc;
            }
            catch { return null; }
        }

        private bool InSameAreaAsLeader(string leaderName)
        {
            using var __profileScope = _plugin.ProfileScope("PartyTeleport.InSameAreaAsLeader");
            try
            {
                foreach (var ent in _plugin.GameController.Entities)
                {
                    try
                    {
                        if (ent.Type != ExileCore2.Shared.Enums.EntityType.Player) continue;
                        var comp = ent.GetComponent<ExileCore2.PoEMemory.Components.Player>();
                        if (comp == null) continue;
                        if (string.Equals(comp.PlayerName, leaderName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        private bool TryClickPartyTp(string leaderName)
        {
            using var __profileScope = _plugin.ProfileScope("PartyTeleport.TryClickPartyTp");
            try
            {
                dynamic row = FindLeaderPartyRow(leaderName);
                if (row == null)
                    return false;

                dynamic teleportButton = GetChild(row, 4);
                if (!IsUsableElement(teleportButton))
                    return false;

                RectangleF rect = RectOf(teleportButton);
                if (rect.Width <= 0 || rect.Height <= 0)
                    return false;

                ClickCenter(rect);
                MarkTeleportAttemptStarted();
                return true;
            }
            catch (Exception ex)
            {
                _plugin.LogMessage("PartyTeleport direct PartyElement error: " + ex.Message, 2);
                return false;
            }
        }

        private bool TryConfirmTeleportDialog()
        {
            using var __profileScope = _plugin.ProfileScope("PartyTeleport.TryConfirmTeleportDialog");
            var now = DateTime.UtcNow;
            if (now < _nextConfirmCheck)
                return false;

            _nextConfirmCheck = now.AddMilliseconds(250);
            return TryConfirmTeleportByPath() || TryConfirmTeleportByGeometry();
        }

        private dynamic FindLeaderPartyRow(string leaderName)
        {
            using var __profileScope = _plugin.ProfileScope("PartyTeleport.FindLeaderPartyRow");
            try
            {
                if (string.IsNullOrWhiteSpace(leaderName))
                    return null;

                dynamic party = UI?.PartyElement;
                if (!IsUsableElement(party))
                    return null;

                // Current PoE2 party UI shape observed from DebugWindow:
                // IngameState.IngameUi.PartyElement -> Children[0] -> Children[0] = party row
                // row.Children[0] = player name, [3] = location, [4] = teleport button.
                dynamic first = GetChild(party, 0);
                dynamic directRow = GetChild(first, 0);
                if (IsLeaderPartyRow(directRow, leaderName))
                    return directRow;

                // Multi-member / slightly shifted layouts: rows may be direct children of the wrapper.
                dynamic wrapperChildren = GetChildren(first);
                int wrapperCount = CountOf(wrapperChildren);
                for (int i = 0; i < wrapperCount; i++)
                {
                    dynamic candidate = GetChild(first, i);
                    if (IsLeaderPartyRow(candidate, leaderName))
                        return candidate;

                    dynamic nested = GetChild(candidate, 0);
                    if (IsLeaderPartyRow(nested, leaderName))
                        return nested;
                }

                // Last cheap fallback: only inspect direct PartyElement children, no global UI DFS.
                dynamic partyChildren = GetChildren(party);
                int partyCount = CountOf(partyChildren);
                for (int i = 0; i < partyCount; i++)
                {
                    dynamic candidate = GetChild(party, i);
                    if (IsLeaderPartyRow(candidate, leaderName))
                        return candidate;

                    dynamic nested = GetChild(GetChild(candidate, 0), 0);
                    if (IsLeaderPartyRow(nested, leaderName))
                        return nested;
                }
            }
            catch { }

            return null;
        }

        private bool IsLeaderPartyRow(dynamic row, string leaderName)
        {
            using var __profileScope = _plugin.ProfileScope("PartyTeleport.IsLeaderPartyRow");
            try
            {
                if (!IsUsableElement(row))
                    return false;

                dynamic nameNode = GetChild(row, 0);
                string text = TextOf(nameNode);

                if (string.IsNullOrWhiteSpace(text))
                    text = TextOf(GetChild(nameNode, 0));

                return !string.IsNullOrWhiteSpace(text) &&
                       text.IndexOf(leaderName, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static dynamic GetChildren(dynamic node)
        {
            try { return node?.Children; } catch { return null; }
        }

        private static dynamic GetChild(dynamic node, int index)
        {
            try
            {
                dynamic children = node?.Children;
                if (children == null) return null;
                int count = CountOf(children);
                if (index < 0 || index >= count) return null;
                return children[index];
            }
            catch { return null; }
        }

        private static int CountOf(dynamic children)
        {
            try { return children == null ? 0 : (int)children.Count; } catch { return 0; }
        }

        private static bool IsUsableElement(dynamic node)
        {
            try
            {
                if (node == null) return false;
                try { if (node.IsValid == false) return false; } catch { }
                try { if (node.IsVisibleLocal == false) return false; } catch { }
                try { if (node.IsVisible == false) return false; } catch { }
                try { if (node.IsActive == false) return false; } catch { }
                return true;
            }
            catch { return false; }
        }

        private static string TextOf(dynamic node)
        {
            try { return (string)node?.Text; } catch { return null; }
        }

        private static RectangleF RectOf(dynamic node)
        {
            try { return node.GetClientRect(); } catch { return new RectangleF(); }
        }

        private bool TryConfirmTeleportByPath()
        {
            using var __profileScope = _plugin.ProfileScope("PartyTeleport.TryConfirmTeleportByPath");
            try
            {
                dynamic popup = UI?.PopUpWindow;
                if (popup == null) return false;
                bool vis = false; try { vis = (bool)popup.IsVisible; } catch { vis = false; }
                if (!vis) return false;

                dynamic node = null;
                try { node = popup.Children[0]?.Children[0]; } catch { node = null; }
                if (node == null) return false;

	                string text = null;
	                try { text = (string)node.Children[0]?.Text; } catch { /* ignore */ }
	                if (string.IsNullOrWhiteSpace(text)) return false;

	                // UI text is localized/variable; keep a loose check.
	                var looksLikeConfirm =
	                    text.IndexOf("teleport", StringComparison.OrdinalIgnoreCase) >= 0 &&
	                    (text.IndexOf("sure", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("confirm", StringComparison.OrdinalIgnoreCase) >= 0);
	                if (!looksLikeConfirm) return false;

	                // Prefer an explicit OK/Yes button; otherwise click the right-most bottom button.
	                dynamic bestBtn = null;
	                RectangleF bestRect = default;
	
	                void Consider(dynamic btn)
	                {
	                    if (btn == null) return;
	                    bool vis; try { vis = btn.IsVisible == true; } catch { vis = true; }
	                    if (!vis) return;
	                    RectangleF r; try { r = btn.GetClientRect(); } catch { return; }
	                    if (r.Width < 60 || r.Height < 18) return;
	
	                    string bt = null; try { bt = (string)btn.Text; } catch { bt = null; }
	                    if (!string.IsNullOrEmpty(bt) &&
	                        (bt.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
	                         bt.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
	                         bt.IndexOf("accept", StringComparison.OrdinalIgnoreCase) >= 0))
	                    {
	                        bestBtn = btn;
	                        bestRect = r;
	                        return;
	                    }

	                    // Fallback heuristic: keep the right-most candidate.
	                    if (bestBtn == null || r.X > bestRect.X)
	                    {
	                        bestBtn = btn;
	                        bestRect = r;
	                    }
	                }

	                try
	                {
	                    dynamic btnContainer = null;
	                    try { btnContainer = node.Children[3]; } catch { btnContainer = null; }
	                    if (btnContainer != null)
	                    {
	                        dynamic kids = null; try { kids = btnContainer.Children; } catch { kids = null; }
	                        int n = 0; try { n = (int)kids.Count; } catch { n = 0; }
	                        for (int i = 0; i < n; i++)
	                        {
	                            dynamic b = null; try { b = kids[i]; } catch { b = null; }
	                            Consider(b);
	                        }
	                    }
	                }
	                catch { /* ignore */ }

	                if (bestBtn == null)
	                {
	                    // Last resort: scan a few descendants and pick something button-shaped.
	                    dynamic kids = null; try { kids = node.Children; } catch { kids = null; }
	                    int n = 0; try { n = (int)kids.Count; } catch { n = 0; }
	                    for (int i = 0; i < n; i++)
	                    {
	                        dynamic b = null; try { b = kids[i]; } catch { b = null; }
	                        Consider(b);
	                    }
	                }

	                if (bestBtn == null) return false;
	                ClickCenter(bestRect);
	                MarkTeleportAttemptStarted();
	                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Geometry fallback: find the dialog by its text and click the rightmost button on the baseline.
        /// Returns true if clicked.
        /// </summary>
        private bool TryConfirmTeleportByGeometry()
        {
            using var __profileScope = _plugin.ProfileScope("PartyTeleport.TryConfirmTeleportByGeometry");
            try
            {
                dynamic popup = UI?.PopUpWindow;
                if (popup == null) return false;
                try { if (popup.IsVisible == false) return false; } catch { return false; }

                bool Visible(dynamic n) { try { return (bool)n.IsVisible; } catch { return false; } }
                RectangleF RectOfLocal(dynamic n) { try { return n.GetClientRect(); } catch { return new RectangleF(); } }

                dynamic textNode = FindNodeContaining(popup, "ARE YOU SURE YOU WANT TO TELEPORT");
                if (textNode == null) textNode = FindNodeContaining(popup, "sure you want to teleport");
                if (textNode == null) return false;

                dynamic container = textNode;
                for (int up = 0; up < 4; up++)
                {
                    try { container = container.Parent; } catch { container = null; }
                    if (container == null) break;
                    var r = RectOfLocal(container);
                    if (r.Width >= 400 && r.Height >= 150) break;
                }
                if (container == null) return false;

                var candidates = new List<RectangleF>();
                dynamic kids = null; try { kids = container.Children; } catch { }
                int n = 0; try { n = (int)kids.Count; } catch { }
                for (int i = 0; i < n; i++)
                {
                    dynamic ch = null; try { ch = kids[i]; } catch { }
                    if (ch == null || !Visible(ch)) continue;
                    var r = RectOfLocal(ch);
                    if (r.Width < 80 || r.Width > 350 || r.Height < 24 || r.Height > 90) continue;
                    var cr = RectOfLocal(container);
                    if (r.Y < cr.Y + cr.Height * 0.55f) continue;
                    candidates.Add(r);
                }

                if (candidates.Count == 0) return false;

                // take rightmost with similar baseline
                RectangleF best = candidates[0];
                foreach (var c in candidates)
                {
                    if (Math.Abs(c.Y - best.Y) < 10f && c.X > best.X) best = c;
                }

                ClickCenter(best);
                MarkTeleportAttemptStarted();
                return true;
            }
            catch { return false; }
        }

        private dynamic FindNodeContaining(dynamic node, string fragment)
        {
            try
            {
                if (node == null) return null;
                bool Visible(dynamic n) { try { return (bool)n.IsVisible; } catch { return false; } }
                if (!Visible(node)) return null;
                string t = null; try { t = (string)node.Text; } catch { }
                if (!string.IsNullOrEmpty(t) && t.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return node;
                dynamic kids = null; try { kids = node.Children; } catch { }
                int n = 0; try { n = (int)kids.Count; } catch { }
                for (int i = 0; i < n; i++)
                {
                    dynamic ch = null; try { ch = kids[i]; } catch { }
                    var res = FindNodeContaining(ch, fragment);
                    if (res != null) return res;
                }
            }
            catch { }
            return null;
        }

        private bool FindTextRecursive(dynamic node, string needle)
        {
            try
            {
                if (node == null) return false;
                bool Visible(dynamic n) { try { return (bool)n.IsVisible; } catch { return false; } }
                if (!Visible(node)) return false;
                string t = null; try { t = (string)node.Text; } catch { }
                if (!string.IsNullOrEmpty(t) && t.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                dynamic kids = null; try { kids = node.Children; } catch { }
                if (kids == null) return false;
                int n = 0; try { n = (int)kids.Count; } catch { }
                for (int i = 0; i < n; i++)
                {
                    dynamic ch = null; try { ch = kids[i]; } catch { }
                    if (ch == null) continue;
                    if (FindTextRecursive(ch, needle)) return true;
                }
            }
            catch { }
            return false;
        }

        private void ClickCenter(RectangleF rect)
        {
            using var __profileScope = _plugin.ProfileScope("PartyTeleport.ClickCenter");
            try
            {
                var c = _plugin.ClientToScreen(new Vector2(rect.Center.X, rect.Center.Y));
                _plugin.PrepareForPluginMouseAction("PartyTeleport.ClickCenter.Prepare");
                Mouse.SetCursorPosAndLeftClick(c, 0);
                _plugin.CompletePluginMouseAction("PartyTeleport.ClickCenter.Complete");
            }
            catch { }
        }

        private void MarkTeleportAttemptStarted()
        {
            using var __profileScope = _plugin.ProfileScope("PartyTeleport.MarkTeleportAttemptStarted");
            try
            {
                _tpPending = true;
                _tpStartTime = DateTime.UtcNow;
                _tpAttemptCount++;
                _tpStartAreaName = _plugin.GameController.Area?.CurrentArea?.Name ?? string.Empty;
            }
            catch { _tpPending = true; }
        }

        private bool AreaNameChangedSinceAttempt()
        {
            using var __profileScope = _plugin.ProfileScope("PartyTeleport.AreaNameChangedSinceAttempt");
            try
            {
                var nm = _plugin.GameController.Area?.CurrentArea?.Name ?? string.Empty;
                return _tpPending && !string.Equals(nm, _tpStartAreaName, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private bool IsLoading()
        {
            using var __profileScope = _plugin.ProfileScope("PartyTeleport.IsLoading");
            // Avoid strong-typed properties that may not exist; try dynamic and UI heuristics.
            try
            {
                dynamic ingameDyn = _plugin.GameController.IngameState;
                if (ingameDyn != null)
                {
                    try { if (ingameDyn.IsLoading == true) return true; } catch { /* property may not exist */ }
                }
            }
            catch { }

            try
            {
                dynamic ui = _plugin.GameController.IngameState?.IngameUi;
                if (ui == null) return false;

                try { if (ui.WaitForLoading != null && ui.WaitForLoading.IsVisible == true) return true; } catch { }
                try { if (ui.LoadingMode != null && ui.LoadingMode.IsVisible == true) return true; } catch { }
                try { if (ui.WaitTillWorldLoad != null && ui.WaitTillWorldLoad.IsVisible == true) return true; } catch { }
                try { if (ui.WorldMapLoading != null && ui.WorldMapLoading.IsVisible == true) return true; } catch { }
                try { if (ui.LoadingState != null && ui.LoadingState.IsVisible == true) return true; } catch { }
            }
            catch { }

            return false;
        }
    }
}
