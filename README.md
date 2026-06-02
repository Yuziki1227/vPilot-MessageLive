# vPilot-MessageLive

MessageLive push notification plugin for vPilot with bidirectional communication.

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)

## Features

- **vPilot → MessageLive**: Capture private messages, radio messages, SELCAL alerts and push to MessageLive
- **MessageLive → vPilot**: Receive messages from MessageLive and inject into vPilot
- **SSO Setup**: One-click configuration via https://mlive.uk/sso
- **Zero-Knowledge**: End-to-end encrypted messaging

## Installation

### Quick Setup (SSO)

1. Download `vPilot-MessageLive.dll` and `vPilot-MessageLive.ini`
2. Copy both files to `<vPilot>\Plugins\`
3. Start vPilot
4. Open https://mlive.uk/sso in your browser
5. Enter your API key and encryption key
6. Click "Connect to vPilot"

### Manual Setup

1. Download `vPilot-MessageLive.dll` and `vPilot-MessageLive.ini`
2. Copy both files to `<vPilot>\Plugins\`
3. Edit `vPilot-MessageLive.ini` with your credentials
4. Restart vPilot

## Configuration

Edit `vPilot-MessageLive.ini`:

```ini
[MessageLive]
ApiKey = your-api-key
EncryptionKey = your-encryption-key

[Relay]
Private = true
Radio = true
Selcal = true
Disconnect = true

[Receive]
Enabled = true
Interval = 3
```

### Settings

| Key | Default | Description |
|-----|---------|-------------|
| ApiKey | - | Your MessageLive API key (required) |
| EncryptionKey | - | Your encryption key (optional) |
| Private | true | Relay private messages |
| Radio | true | Relay radio messages |
| Selcal | true | Relay SELCAL alerts |
| Disconnect | true | Relay disconnect events |
| Enabled | true | Enable receiving messages |
| Interval | 3 | Poll interval in seconds |

## Usage

### Receiving Messages

When a message is sent to your MessageLive account, the plugin will inject it into vPilot:

- Messages with a recipient → Private message
- Messages without recipient → Radio message

### Sending Messages from Web

1. Open https://mlive.uk/dashboard
2. Enter recipient callsign in **Title** field (e.g., `ATC123`)
3. Enter message in **Content** field (e.g., `.xpdr 1200`)
4. Click **Send**

The message will be delivered to the specified callsign in vPilot.

### ATC Command Examples

| Title | Content | Description |
|-------|---------|-------------|
| ATC123 | .xpdr 1200 | Set squawk code |
| ATC123 | .com 121.5 | Switch frequency |
| ATC123 | .morl | Request |
| (empty) | Text message | Radio broadcast |

## Building from Source

```bash
cd vPilot-MessageLive
dotnet build -c Release
```

Output: `bin\Release\vPilot-MessageLive.dll`

### Requirements

- .NET Framework 4.8
- vPilot SDK (`RossCarlson.Vatsim.Vpilot.Plugins.dll`)

## How It Works

```
vPilot ──event──→ Plugin ──POST──→ MessageLive Server
                                            │
                                            ↓ SSE
                                       Web/Mobile Client

Web/Mobile Client ──POST──→ MessageLive Server
                                            │
                                            ↓ GET /decrypted
Plugin ←──SendPrivateMessage── Poll Timer
```

## API Reference

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | /api/register | Create account |
| POST | /api/verify | Verify credentials |
| POST | /api/messages | Send message |
| GET | /api/messages | List messages |
| GET | /api/messages/decrypted | List decrypted messages |
| GET | /api/stream | SSE real-time stream |
| DELETE | /api/messages/:id | Delete message |
| GET | /api/account/export | Export data |
| DELETE | /api/account | Delete account |

## Logging

The plugin writes logs to vPilot's debug window in pink color:

```
07:40:40 Plugin loading v2.1.0...
07:40:40 SSO server ready
07:40:40 Client initialized
07:40:40 Connecting to https://mlive.uk...
07:40:41 Verify: HTTP 200
07:40:41 API key verified OK
07:40:41 Receive polling started (3s)
07:41:00 Received: Squawk 7200
```

## License

GNU General Public License v3.0 - see [LICENSE](LICENSE) for details.

## Author

Yuziki1227@pm.me

## Links

- Website: https://mlive.uk
- SSO Setup: https://mlive.uk/sso
- Dashboard: https://mlive.uk/dashboard
- API Docs: https://mlive.uk/docs
