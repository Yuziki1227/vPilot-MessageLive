# Changelog

## [2.3.0] - 2026-06-09

### Added
- Remote command handling via `Title=command` or the `!` prefix.
- Commands for status, contacts, squawk ident, Mode C, METAR, ATIS, private messages, and radio text.
- Smart recipient aliases: `last`, `lastpm`, `lastradio`, `atc`, and `lastatc`.
- Optional deletion of processed MessageLive messages through the existing DELETE API.
- Periodic status heartbeat messages.
- Local outbound retry queue for failed MessageLive posts.
- Optional controller-online relay.

### Improved
- Added more INI controls for receive, commands, status, queueing, logging, and controller relay.
- Hardened radio-message relay when vPilot is not connected.

## [2.2.1] - 2026-06-09

### Fixed
- Fixed MessageLive-to-vPilot delivery by no longer dropping every message whose source is `api`.
- Replaced broad source filtering with recent outbound-message loop prevention.
- Added radio-message injection for MessageLive messages whose `Title` is `radio`.

### Improved
- Seed receive polling with existing message IDs so old dashboard messages are not replayed on startup.
- Prevent overlapping receive polls when a previous request is still running.
- Keep a bounded receive de-duplication cache instead of clearing all seen IDs at once.
- Made MessageLive JSON parsing tolerate whitespace around fields.
- Added detailed local log output and a separate local fingerprint trace file.

## [2.1.0] - 2026-06-02

### Added
- SSO support via https://mlive.uk/sso
- Local HTTP server for credential injection (localhost:12345)
- Pink colored logs in vPilot debug window
- Bidirectional communication (send + receive)
- Web Dashboard can send commands to vPilot

### Changed
- Simplified log format (no brackets, no prefix)
- Removed startup message injection
- Improved error messages

### Fixed
- Feedback loop prevention (skip own messages)
- Cloudflare WAF bypass via custom header

## [2.0.0] - 2026-06-02

### Added
- MessageLive-only plugin (replaces vPilot-Pushover)
- Receive messages from MessageLive
- Poll-based message injection
- New INI configuration format

### Changed
- Complete rewrite from vPilot-Pushover
- Simplified architecture
- New plugin namespace: vPilot_MessageLive

## [1.0.0] - 2026-06-01

### Added
- Initial release
- Pushover, Telegram, Gotify, MessageLive drivers
- vPilot event relay (private, radio, SELCAL)
- Hoppie ACARS support
