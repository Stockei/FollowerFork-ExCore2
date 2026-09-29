# Changelog

Changes in this fork of the Follower plugin, newest first. Each version is a git tag.

## v1.0.0 - 2026-09-29

### Added
- **Party chat commands work again.** The core's `ChatPanel.ChatBox` currently resolves to address 0, so the plugin reads the chat line list directly (ChatPanel > 1 > 2 > 1, newest 30 lines). Old chat history is skipped only on plugin start or reload; commands written during a loading screen still arrive.
- **Plain-word commands.** The command texts are settings, for example *Stop command* = `shortbreak` and *Start command* = `goback`.
- **Follow the leader through the end-of-map portal.** In maps, when the leader disappears right at a portal that leads to a hideout, the follower walks there and clicks the portal once instead of teleporting through the party panel. It clicks again only after a full wait (default 8 s), at most 3 clicks per zone.
- **Party teleport in maps is the fallback**, by default 20 s after the leader was last seen, and never while a portal click is pending.
- **Zone settle time.** After every zone change, the party teleport and the portal logic wait (default 8 s).
- New settings under *Transition*, plus two debug options: *party chat commands to txt* and *map exit portal to txt*.

### Fixed
- Builds on ExileCore2 versions without `StdVector.Size` (2026-09-25).
- Clicks land correctly when the game runs in windowed mode (2026-09-25).
