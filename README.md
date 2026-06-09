# vPilot-MessageLive

MessageLive push notification plugin for vPilot with practical bidirectional communication.

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)

## Features

- **vPilot -> MessageLive**: Capture private messages, radio messages, SELCAL alerts, and connection state.
- **MessageLive -> vPilot**: Send dashboard/API messages back through vPilot.
- **Private reply routing**: A MessageLive `Title` is treated as the vPilot private-message recipient callsign.
- **Radio routing**: `Title` must be `radio` to send radio text on the current transmit frequency.
- **Loop prevention**: Recently relayed vPilot messages are ignored when the receive poll sees their local fingerprint again.
- **Detailed local logs**: Writes a general log and a message fingerprint trace file next to the plugin INI by default.
- **Remote commands**: Run safe vPilot actions from MessageLive, such as `!status`, `!ident`, `!modec on`, `!metar ZBAA`, and `!atis ZBAA_TWR`.
- **Smart recipients**: Use `last`, `lastpm`, `lastradio`, or `atc` as MessageLive titles to reply to recent contacts.
- **Status heartbeat**: Periodically posts plugin status, queue length, last poll time, and recent contacts to MessageLive.
- **Retry queue**: Failed outbound posts are retried locally without changing the server.
- **SSO setup**: One-click configuration via https://mlive.uk/sso.
- **Zero-knowledge**: End-to-end encrypted messaging support.

## Installation

### Quick Setup (SSO)

1. Download `vPilot-MessageLive.dll` and `vPilot-MessageLive.ini`.
2. Copy both files to `<vPilot>\Plugins\`.
3. Start vPilot.
4. Open https://mlive.uk/sso in your browser.
5. Enter your API key and encryption key.
6. Click "Connect to vPilot".

### Manual Setup

1. Download `vPilot-MessageLive.dll` and `vPilot-MessageLive.ini`.
2. Copy both files to `<vPilot>\Plugins\`.
3. Edit `vPilot-MessageLive.ini` with your credentials.
4. Restart vPilot.

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
Controllers = false

[Receive]
Enabled = true
Interval = 3
DeleteAfterProcess = false

[Commands]
Enabled = true
Title = command
Prefix = !

[Status]
Enabled = true
Interval = 60

[Queue]
Enabled = true
RetryInterval = 10
MaxAttempts = 5

[Log]
Enabled = true
Fingerprints = true
File = vPilot-MessageLive.log
FingerprintFile = vPilot-MessageLive.fingerprints.log
```

| Key | Default | Description |
|-----|---------|-------------|
| ApiKey | - | Your MessageLive API key. |
| EncryptionKey | - | Your encryption key. |
| Private | true | Relay private messages from vPilot to MessageLive. |
| Radio | true | Relay radio messages from vPilot to MessageLive. |
| Selcal | true | Relay SELCAL alerts from vPilot to MessageLive. |
| Disconnect | true | Relay disconnect events. |
| Controllers | false | Relay newly seen controller callsigns to MessageLive. |
| Enabled | true | Enable MessageLive-to-vPilot receiving. |
| Interval | 3 | Receive poll interval in seconds. |
| DeleteAfterProcess | false | Delete a MessageLive message after the plugin handles it. |
| Commands/Enabled | true | Enable local command execution from MessageLive. |
| Commands/Title | command | Treat this title as a command message. |
| Commands/Prefix | ! | Treat content starting with this prefix as a command. |
| Status/Enabled | true | Periodically post plugin status to MessageLive. |
| Status/Interval | 60 | Status heartbeat interval in seconds. |
| Queue/Enabled | true | Retry failed outbound MessageLive posts. |
| Queue/RetryInterval | 10 | Retry interval in seconds. |
| Queue/MaxAttempts | 5 | Maximum retry attempts per queued message. |
| Log/Enabled | true | Write a detailed local log file. |
| Log/Fingerprints | true | Write a local fingerprint trace for message routing. |
| Log/File | vPilot-MessageLive.log | General log path, relative to the plugin folder unless absolute. |
| Log/FingerprintFile | vPilot-MessageLive.fingerprints.log | Fingerprint trace path, relative to the plugin folder unless absolute. |

## Usage

### vPilot to MessageLive

When vPilot receives private messages, matching radio messages, or SELCAL alerts, the plugin posts them to MessageLive. The sender callsign is stored in the MessageLive `Title`, which makes replying easy from the dashboard.

### MessageLive to vPilot

1. Open https://mlive.uk/dashboard.
2. Put the recipient callsign in **Title** for a private message, for example `ATC123`.
3. Use **Title** = `radio` to send radio text on the active transmit frequency.
4. Put the vPilot message text in **Content**.
5. Click **Send**.

The plugin polls MessageLive and sends new dashboard/API messages through vPilot.

### Examples

| Title | Content | vPilot action |
|-------|---------|---------------|
| ATC123 | `.xpdr 1200` | Private message to `ATC123`. |
| ATC123 | `.com 121.500` | Private message to `ATC123`. |
| ATC123 | `Wilco` | Private message to `ATC123`. |
| radio | `Radio check` | Radio text on the current transmit frequency. |
| last | `Wilco` | Private message to the most recent private-message sender. |
| lastradio | `Say again` | Private message to the most recent radio sender. |
| atc | `Request vectors` | Private message to the most recently seen controller. |
| command | `!status` | Return local plugin status to MessageLive. |
| command | `!ident` | Squawk ident. |
| command | `!modec on` | Turn Mode C on. |
| command | `!metar ZBAA` | Request METAR in vPilot. |
| command | `!atis ZBAA_TWR` | Request controller ATIS in vPilot. |
| command | `!pm last Thanks` | Send private message to the recent private-message sender. |
| command | `!radio Radio check` | Send radio text on the current transmit frequency. |

## Building from Source

```bash
cd vPilot-MessageLive
dotnet build src/vPilot-MessageLive.csproj -c Release
```

Output: `src\bin\Release\vPilot-MessageLive.dll`

### Requirements

- .NET Framework 4.8
- vPilot SDK (`RossCarlson.Vatsim.Vpilot.Plugins.dll`)

## How It Works

```text
vPilot event -> Plugin -> POST /api/messages -> MessageLive Server -> Web/Mobile Client

Web/Mobile Client -> POST /api/messages -> MessageLive Server
Plugin poll timer -> GET /api/messages/decrypted -> SendPrivateMessage or SendRadioMessage
```

## API Reference

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/register` | Create account |
| POST | `/api/verify` | Verify credentials |
| POST | `/api/messages` | Send message |
| GET | `/api/messages` | List messages |
| GET | `/api/messages/decrypted` | List decrypted messages |
| GET | `/api/stream` | SSE real-time stream |
| DELETE | `/api/messages/:id` | Delete message |
| GET | `/api/account/export` | Export data |
| DELETE | `/api/account` | Delete account |

## Logging

The plugin writes logs to vPilot's debug window:

```text
07:40:40 Plugin loading v2.3.0...
07:40:40 SSO server ready
07:40:40 Client initialized
07:40:40 Connecting to https://mlive.uk...
07:40:41 Verify: HTTP 200
07:40:41 API key verified OK
07:40:41 Receive baseline loaded (12 existing messages)
07:40:41 Receive polling started (3s)
07:41:00 MessageLive -> PM ATC123: .xpdr 1200 fp=4d25c5e6a6f0
07:41:05 MessageLive -> radio: Radio check fp=f034a7a53b6c
```

The fingerprint trace file contains tab-separated records with direction, action, message id, source, title, target, fingerprint, detail, and a short content preview.

## License

GNU General Public License v3.0 - see [LICENSE](LICENSE) for details.

## Author

Yuziki1227@pm.me

## Links

- Website: https://mlive.uk
- SSO Setup: https://mlive.uk/sso
- Dashboard: https://mlive.uk/dashboard
- API Docs: https://mlive.uk/docs
