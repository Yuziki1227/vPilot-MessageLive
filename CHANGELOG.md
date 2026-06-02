# Changelog

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
