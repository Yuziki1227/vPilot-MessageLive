using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using Microsoft.Win32;

using RossCarlson.Vatsim.Vpilot.Plugins;
using RossCarlson.Vatsim.Vpilot.Plugins.Events;

namespace vPilot_MessageLive {
    public class Main : IPlugin {

        public static string version = "2.3.0";

        private IBroker vPilot;
        private MessageLiveClient client;
        private SsoServer ssoServer;

        public string Name { get; } = "vPilot MessageLive";
        public string connectedCallsign = null;

        private bool settingsLoaded = false;
        private IniFile settingsFile;

        // Settings
        private string apiKey;
        private string encryptionKey;
        private bool relayPrivate = true;
        private bool relayRadio = true;
        private bool relaySelcal = true;
        private bool relayDisconnect = true;
        private bool receiveEnabled = true;
        private int receiveInterval = 3;
        private bool fileLogEnabled = true;
        private bool fingerprintLogEnabled = true;
        private bool deleteAfterProcess = false;
        private bool commandsEnabled = true;
        private string commandTitle = "command";
        private string commandPrefix = "!";
        private bool statusEnabled = true;
        private int statusInterval = 60;
        private bool queueEnabled = true;
        private int queueRetryInterval = 10;
        private int queueMaxAttempts = 5;
        private bool relayControllers = false;
        private string logFilePath;
        private string fingerprintFilePath;
        private object fileLogLock = new object();

        // Receive polling
        private System.Timers.Timer receiveTimer;
        private System.Timers.Timer statusTimer;
        private System.Timers.Timer queueTimer;
        private HashSet<string> seenMessageIds = new HashSet<string>();
        private Queue<string> seenMessageOrder = new Queue<string>();
        private Dictionary<string, DateTime> recentOutboundMessages = new Dictionary<string, DateTime>();
        private Queue<PendingOutboundMessage> outboundQueue = new Queue<PendingOutboundMessage>();
        private object queueLock = new object();
        private object receiveLock = new object();
        private object outboundLock = new object();
        private bool receiveInProgress = false;
        private bool queueInProgress = false;
        private string lastPrivateFrom = null;
        private string lastRadioFrom = null;
        private string lastController = null;
        private int controllerCount = 0;
        private DateTime connectedAtUtc = DateTime.MinValue;
        private DateTime lastPollUtc = DateTime.MinValue;
        private DateTime lastMessageUtc = DateTime.MinValue;
        private string lastError = "";
        private const int maxSeenMessageIds = 1000;
        private const int outboundLoopWindowSeconds = 90;

        public void Initialize(IBroker broker) {
            log("Plugin loading v" + version + "...");
            vPilot = broker;

            // Load settings
            loadSettings();
            if (!settingsLoaded) {
                log("ERROR: Settings failed to load");
                return;
            }

            // Start SSO server (always, even if not configured)
            startSsoServer();

            // If configured, connect to MessageLive
            if (!string.IsNullOrEmpty(apiKey)) {
                connectToMessageLive();
            } else {
                log("NOT CONFIGURED — Please visit https://mlive.uk/sso to set up");
            }

            // Subscribe to vPilot events
            vPilot.NetworkConnected += onNetworkConnected;
            vPilot.NetworkDisconnected += onNetworkDisconnected;
            if (relayPrivate) vPilot.PrivateMessageReceived += onPrivateMessage;
            if (relayRadio) vPilot.RadioMessageReceived += onRadioMessage;
            if (relaySelcal) vPilot.SelcalAlertReceived += onSelcalAlert;
            vPilot.ControllerAdded += onControllerAdded;

            log("Event handlers registered");
            log("SSO server: http://localhost:12345/");
            startQueueRetry();
            log("Plugin ready");
        }

        private void startSsoServer() {
            try {
                ssoServer = new SsoServer(settingsFile, log, onSsoCredentialsSaved);
                ssoServer.Start();
            } catch (Exception ex) {
                log("SSO server failed: " + ex.Message);
                log("You can still configure manually via INI file");
            }
        }

        private void onSsoCredentialsSaved() {
            log("SSO credentials received — reconnecting...");
            // Reload settings from INI
            loadSettings();
            // Reconnect
            connectToMessageLive();
        }

        private async void connectToMessageLive() {
            if (string.IsNullOrEmpty(apiKey)) {
                log("ERROR: No API key configured");
                return;
            }

            // Stop existing receive
            stopReceive();

            // Create new client
            client = new MessageLiveClient(apiKey, log);
            log("Client initialized");

            // Verify API key
            log("Verifying API key...");
            bool ok = await client.Verify();
            if (ok) {
                log("API key verified OK");
                // Start receive polling
                if (receiveEnabled) {
                    startReceive();
                }
                startStatusHeartbeat();
            } else {
                log("ERROR: API key verification failed — open https://mlive.uk/sso to reconfigure");
            }
        }

        // === Receive: Poll MessageLive → inject into vPilot ===

        private async void startReceive() {
            seenMessageIds.Clear();
            seenMessageOrder.Clear();

            try {
                var existingMessages = await client.GetMessages();
                foreach (var msg in existingMessages) {
                    markMessageSeen(msg.id);
                }
                log("Receive baseline loaded (" + seenMessageIds.Count + " existing messages)");
            } catch (Exception ex) {
                log("Receive baseline failed: " + ex.Message);
            }

            receiveTimer = new System.Timers.Timer(receiveInterval * 1000);
            receiveTimer.Elapsed += onReceiveTick;
            receiveTimer.AutoReset = true;
            receiveTimer.Enabled = true;
            log("Receive polling started (" + receiveInterval + "s)");
        }

        private void stopReceive() {
            if (receiveTimer != null) {
                receiveTimer.Enabled = false;
                receiveTimer.Dispose();
                receiveTimer = null;
            }
        }

        private async void onReceiveTick(object sender, ElapsedEventArgs e) {
            if (vPilot == null || connectedCallsign == null || client == null) return;

            lock (receiveLock) {
                if (receiveInProgress) return;
                receiveInProgress = true;
            }

            try {
                pruneOutboundMessages();

                var messages = await client.GetMessages();
                lastPollUtc = DateTime.UtcNow;
                foreach (var msg in messages) {
                    if (seenMessageIds.Contains(msg.id)) continue;
                    markMessageSeen(msg.id);

                    // Skip empty messages
                    if (string.IsNullOrWhiteSpace(msg.content)) continue;

                    string fingerprint = messageFingerprint(msg.title, msg.content);
                    traceFingerprint("inbound", "seen", msg.id, msg.from, msg.title, msg.content, fingerprint, "new message from MessageLive");

                    if (isRecentOutboundMessage(msg.title, msg.content)) {
                        log("Skipped loopback message: " + describeMessageTarget(msg.title) + " fp=" + shortFingerprint(fingerprint));
                        traceFingerprint("inbound", "skip-loopback", msg.id, msg.from, msg.title, msg.content, fingerprint, "matched recent outbound fingerprint");
                        await deleteProcessedMessage(msg, "loopback");
                        continue;
                    }

                    // Inject into vPilot
                    try {
                        if (isCommandMessage(msg.title, msg.content)) {
                            string result = executeCommand(msg.content);
                            log("MessageLive command: " + preview(msg.content, 80) + " -> " + result + " fp=" + shortFingerprint(fingerprint));
                            traceFingerprint("inbound", "command", msg.id, msg.from, msg.title, msg.content, fingerprint, result);
                            relayToMessageLive("vPilot Command", result);
                        } else if (isRadioTitle(msg.title)) {
                            log("MessageLive -> radio: " + msg.content + " fp=" + shortFingerprint(fingerprint));
                            vPilot.SendRadioMessage(msg.content);
                            traceFingerprint("inbound", "send-radio", msg.id, msg.from, msg.title, msg.content, fingerprint, "sent on current transmit frequency");
                        } else if (!string.IsNullOrWhiteSpace(msg.title)) {
                            string to = resolveRecipient(msg.title);
                            log("MessageLive -> PM " + to + ": " + msg.content + " fp=" + shortFingerprint(fingerprint));
                            vPilot.SendPrivateMessage(to, msg.content);
                            traceFingerprint("inbound", "send-private", msg.id, msg.from, msg.title, msg.content, fingerprint, "sent to " + to);
                        } else {
                            log("MessageLive message skipped: empty title is not a radio route fp=" + shortFingerprint(fingerprint));
                            traceFingerprint("inbound", "skip-empty-title", msg.id, msg.from, msg.title, msg.content, fingerprint, "title must be a callsign or radio");
                        }
                        lastMessageUtc = DateTime.UtcNow;
                        await deleteProcessedMessage(msg, "processed");
                    } catch (Exception ex) {
                        lastError = ex.Message;
                        log("Inject error: " + ex.Message);
                        traceFingerprint("inbound", "inject-error", msg.id, msg.from, msg.title, msg.content, fingerprint, ex.Message);
                    }
                }

            } finally {
                lock (receiveLock) {
                    receiveInProgress = false;
                }
            }
        }

        private void markMessageSeen(string id) {
            if (string.IsNullOrEmpty(id) || seenMessageIds.Contains(id)) return;

            seenMessageIds.Add(id);
            seenMessageOrder.Enqueue(id);

            while (seenMessageOrder.Count > maxSeenMessageIds) {
                seenMessageIds.Remove(seenMessageOrder.Dequeue());
            }
        }

        private async Task deleteProcessedMessage(MessageLiveMessage msg, string reason) {
            if (!deleteAfterProcess || client == null || string.IsNullOrEmpty(msg.id)) return;

            bool ok = await client.DeleteMessage(msg.id);
            log("Delete processed message " + msg.id + " reason=" + reason + " ok=" + ok);
        }

        private bool isCommandMessage(string title, string content) {
            if (!commandsEnabled) return false;
            if (string.Equals((title ?? "").Trim(), commandTitle, StringComparison.OrdinalIgnoreCase)) return true;
            return !string.IsNullOrEmpty(commandPrefix) && (content ?? "").TrimStart().StartsWith(commandPrefix);
        }

        private string resolveRecipient(string title) {
            string t = (title ?? "").Trim();
            if (string.Equals(t, "last", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "lastpm", StringComparison.OrdinalIgnoreCase)) {
                if (!string.IsNullOrEmpty(lastPrivateFrom)) return lastPrivateFrom;
            }
            if (string.Equals(t, "lastradio", StringComparison.OrdinalIgnoreCase)) {
                if (!string.IsNullOrEmpty(lastRadioFrom)) return lastRadioFrom;
            }
            if (string.Equals(t, "atc", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "lastatc", StringComparison.OrdinalIgnoreCase)) {
                if (!string.IsNullOrEmpty(lastController)) return lastController;
            }
            return t;
        }

        private string executeCommand(string content) {
            string text = (content ?? "").Trim();
            if (!string.IsNullOrEmpty(commandPrefix) && text.StartsWith(commandPrefix)) {
                text = text.Substring(commandPrefix.Length).Trim();
            }
            if (string.IsNullOrWhiteSpace(text)) return "Command failed: empty command";

            string[] parts = text.Split(new char[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string command = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1 ? parts[1].Trim() : "";

            switch (command) {
                case "help":
                    return "Commands: !status, !contacts, !ident, !modec on|off, !metar ICAO, !atis CALLSIGN, !pm CALLSIGN TEXT, !radio TEXT";
                case "status":
                    return buildStatusText();
                case "contacts":
                    return "Contacts: lastpm=" + valueOrDash(lastPrivateFrom) + ", lastradio=" + valueOrDash(lastRadioFrom) + ", atc=" + valueOrDash(lastController);
                case "ident":
                    vPilot.SquawkIdent();
                    return "Command OK: squawk ident";
                case "modec":
                    if (string.Equals(arg, "on", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "true", StringComparison.OrdinalIgnoreCase)) {
                        vPilot.SetModeC(true);
                        return "Command OK: Mode C on";
                    }
                    if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "false", StringComparison.OrdinalIgnoreCase)) {
                        vPilot.SetModeC(false);
                        return "Command OK: Mode C off";
                    }
                    return "Command failed: use !modec on or !modec off";
                case "metar":
                    if (string.IsNullOrWhiteSpace(arg)) return "Command failed: use !metar ICAO";
                    vPilot.RequestMetar(arg.ToUpperInvariant());
                    return "Command OK: requested METAR " + arg.ToUpperInvariant();
                case "atis":
                    if (string.IsNullOrWhiteSpace(arg)) return "Command failed: use !atis CALLSIGN";
                    vPilot.RequestAtis(arg.ToUpperInvariant());
                    return "Command OK: requested ATIS " + arg.ToUpperInvariant();
                case "pm":
                    return executePrivateCommand(arg);
                case "radio":
                    if (string.IsNullOrWhiteSpace(arg)) return "Command failed: use !radio TEXT";
                    vPilot.SendRadioMessage(arg);
                    return "Command OK: sent radio text";
                default:
                    return "Command failed: unknown command '" + command + "'";
            }
        }

        private string executePrivateCommand(string arg) {
            string[] parts = (arg ?? "").Split(new char[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return "Command failed: use !pm CALLSIGN TEXT";

            string to = resolveRecipient(parts[0]);
            if (string.IsNullOrWhiteSpace(to)) return "Command failed: recipient unavailable";

            vPilot.SendPrivateMessage(to, parts[1]);
            return "Command OK: sent PM to " + to;
        }

        private string valueOrDash(string value) {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        // === Send: vPilot events → MessageLive ===

        private void onNetworkConnected(object sender, NetworkConnectedEventArgs e) {
            connectedCallsign = e.Callsign;
            connectedAtUtc = DateTime.UtcNow;
            log("Network connected: " + e.Callsign);
            relayToMessageLive("vPilot", "Connected to network as " + e.Callsign);
        }

        private void onNetworkDisconnected(object sender, EventArgs e) {
            log("Network disconnected");
            connectedCallsign = null;
            connectedAtUtc = DateTime.MinValue;
            if (relayDisconnect) {
                relayToMessageLive("vPilot", "Disconnected from network");
            }
        }

        private void onPrivateMessage(object sender, PrivateMessageReceivedEventArgs e) {
            lastPrivateFrom = e.From;
            lastMessageUtc = DateTime.UtcNow;
            log("Private message from " + e.From + ": " + e.Message);
            relayToMessageLive(e.From, e.Message);
        }

        private void onRadioMessage(object sender, RadioMessageReceivedEventArgs e) {
            if (!string.IsNullOrEmpty(connectedCallsign) && e.Message.Contains(connectedCallsign)) {
                lastRadioFrom = e.From;
                lastMessageUtc = DateTime.UtcNow;
                log("Radio message from " + e.From + ": " + e.Message);
                relayToMessageLive(e.From, e.Message);
            }
        }

        private void onSelcalAlert(object sender, SelcalAlertReceivedEventArgs e) {
            log("SELCAL alert from " + e.From);
            relayToMessageLive(e.From, "SELCAL Alert");
        }

        private void onControllerAdded(object sender, ControllerAddedEventArgs e) {
            controllerCount++;
            lastController = e.Callsign;
            log("Controller added: " + e.Callsign + " freq=" + e.Frequency);
            if (relayControllers) {
                relayToMessageLive("vPilot ATC", "Controller online: " + e.Callsign + " freq=" + e.Frequency);
            }
        }

        private async void relayToMessageLive(string title, string content) {
            if (client == null || string.IsNullOrWhiteSpace(content)) return;

            string fingerprint = messageFingerprint(title, content);
            traceFingerprint("outbound", "send-start", null, connectedCallsign, title, content, fingerprint, "posting to MessageLive");
            bool ok = await client.SendMessage(title, content);
            if (ok) {
                rememberOutboundMessage(title, content);
                log("vPilot -> MessageLive " + describeMessageTarget(title) + " fp=" + shortFingerprint(fingerprint));
                traceFingerprint("outbound", "send-ok", null, connectedCallsign, title, content, fingerprint, "posted to MessageLive");
            } else {
                traceFingerprint("outbound", "send-failed", null, connectedCallsign, title, content, fingerprint, "MessageLive client returned false");
                enqueueOutbound(title, content, fingerprint, "initial send failed");
            }
        }

        private void enqueueOutbound(string title, string content, string fingerprint, string reason) {
            if (!queueEnabled || string.IsNullOrWhiteSpace(content)) return;

            lock (queueLock) {
                outboundQueue.Enqueue(new PendingOutboundMessage {
                    Title = title ?? "",
                    Content = content,
                    Fingerprint = fingerprint,
                    Attempts = 0,
                    CreatedAtUtc = DateTime.UtcNow,
                    LastError = reason
                });
            }
            log("Queued outbound message fp=" + shortFingerprint(fingerprint) + " reason=" + reason);
        }

        private void startQueueRetry() {
            if (!queueEnabled) return;

            if (queueTimer != null) {
                queueTimer.Enabled = false;
                queueTimer.Dispose();
            }

            queueTimer = new System.Timers.Timer(Math.Max(3, queueRetryInterval) * 1000);
            queueTimer.Elapsed += onQueueRetryTick;
            queueTimer.AutoReset = true;
            queueTimer.Enabled = true;
            log("Outbound retry queue started (" + queueRetryInterval + "s)");
        }

        private async void onQueueRetryTick(object sender, ElapsedEventArgs e) {
            if (client == null) return;

            lock (queueLock) {
                if (queueInProgress) return;
                queueInProgress = true;
            }

            try {
                PendingOutboundMessage item = null;
                lock (queueLock) {
                    if (outboundQueue.Count > 0) item = outboundQueue.Dequeue();
                }
                if (item == null) return;

                item.Attempts++;
                bool ok = await client.SendMessage(item.Title, item.Content);
                if (ok) {
                    rememberOutboundMessage(item.Title, item.Content);
                    log("Retry send OK fp=" + shortFingerprint(item.Fingerprint) + " attempts=" + item.Attempts);
                    traceFingerprint("outbound", "retry-ok", null, connectedCallsign, item.Title, item.Content, item.Fingerprint, "attempts=" + item.Attempts);
                } else if (item.Attempts < queueMaxAttempts) {
                    item.LastError = "retry failed";
                    lock (queueLock) {
                        outboundQueue.Enqueue(item);
                    }
                    log("Retry send failed fp=" + shortFingerprint(item.Fingerprint) + " attempts=" + item.Attempts + "/" + queueMaxAttempts);
                } else {
                    lastError = "Outbound retry exhausted";
                    log("Retry exhausted fp=" + shortFingerprint(item.Fingerprint));
                    traceFingerprint("outbound", "retry-exhausted", null, connectedCallsign, item.Title, item.Content, item.Fingerprint, "attempts=" + item.Attempts);
                }
            } finally {
                lock (queueLock) {
                    queueInProgress = false;
                }
            }
        }

        private void rememberOutboundMessage(string title, string content) {
            lock (outboundLock) {
                recentOutboundMessages[messageFingerprint(title, content)] = DateTime.UtcNow;
            }
        }

        private bool isRecentOutboundMessage(string title, string content) {
            lock (outboundLock) {
                return recentOutboundMessages.ContainsKey(messageFingerprint(title, content));
            }
        }

        private void pruneOutboundMessages() {
            lock (outboundLock) {
                var expired = new List<string>();
                DateTime cutoff = DateTime.UtcNow.AddSeconds(-outboundLoopWindowSeconds);
                foreach (var item in recentOutboundMessages) {
                    if (item.Value < cutoff) {
                        expired.Add(item.Key);
                    }
                }
                foreach (string key in expired) {
                    recentOutboundMessages.Remove(key);
                }
            }
        }

        private void startStatusHeartbeat() {
            if (!statusEnabled || client == null) return;

            if (statusTimer != null) {
                statusTimer.Enabled = false;
                statusTimer.Dispose();
            }

            statusTimer = new System.Timers.Timer(Math.Max(15, statusInterval) * 1000);
            statusTimer.Elapsed += onStatusTick;
            statusTimer.AutoReset = true;
            statusTimer.Enabled = true;
            log("Status heartbeat started (" + statusInterval + "s)");
            relayToMessageLive("vPilot Status", buildStatusText());
        }

        private void onStatusTick(object sender, ElapsedEventArgs e) {
            if (client == null) return;
            relayToMessageLive("vPilot Status", buildStatusText());
        }

        private string buildStatusText() {
            int queueCount;
            lock (queueLock) {
                queueCount = outboundQueue.Count;
            }

            string uptime = connectedAtUtc == DateTime.MinValue ? "-" : formatAge(DateTime.UtcNow - connectedAtUtc);
            return "vPilot MessageLive v" + version
                + " | callsign=" + valueOrDash(connectedCallsign)
                + " | connected=" + (connectedCallsign != null)
                + " | uptime=" + uptime
                + " | lastpm=" + valueOrDash(lastPrivateFrom)
                + " | lastradio=" + valueOrDash(lastRadioFrom)
                + " | atc=" + valueOrDash(lastController)
                + " | controllersSeen=" + controllerCount
                + " | queue=" + queueCount
                + " | lastPoll=" + formatUtc(lastPollUtc)
                + " | lastMessage=" + formatUtc(lastMessageUtc)
                + " | lastError=" + valueOrDash(lastError);
        }

        private string formatUtc(DateTime value) {
            return value == DateTime.MinValue ? "-" : value.ToString("yyyy-MM-dd HH:mm:ss") + "Z";
        }

        private string formatAge(TimeSpan value) {
            if (value.TotalHours >= 1) return ((int)value.TotalHours) + "h" + value.Minutes + "m";
            if (value.TotalMinutes >= 1) return ((int)value.TotalMinutes) + "m" + value.Seconds + "s";
            return ((int)value.TotalSeconds) + "s";
        }

        private string messageFingerprint(string title, string content) {
            string raw = normalizeTitle(title) + "\n" + (content ?? "").Trim();
            using (SHA256 sha = SHA256.Create()) {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes) {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        private string normalizeTitle(string title) {
            return (title ?? "").Trim().ToUpperInvariant();
        }

        private string describeMessageTarget(string title) {
            if (isRadioTitle(title)) return "radio";
            return string.IsNullOrWhiteSpace(title) ? "empty-title" : "PM " + title.Trim();
        }

        private bool isRadioTitle(string title) {
            return string.Equals((title ?? "").Trim(), "radio", StringComparison.OrdinalIgnoreCase);
        }

        private string shortFingerprint(string fingerprint) {
            if (string.IsNullOrEmpty(fingerprint)) return "";
            return fingerprint.Length <= 12 ? fingerprint : fingerprint.Substring(0, 12);
        }

        private void traceFingerprint(string direction, string action, string messageId, string from, string title, string content, string fingerprint, string detail) {
            if (!fingerprintLogEnabled || string.IsNullOrEmpty(fingerprintFilePath)) return;

            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                + "\tdirection=" + safeLogValue(direction)
                + "\taction=" + safeLogValue(action)
                + "\tfingerprint=" + fingerprint
                + "\tmessageId=" + safeLogValue(messageId)
                + "\tfrom=" + safeLogValue(from)
                + "\ttitle=" + safeLogValue(title)
                + "\ttarget=" + safeLogValue(describeMessageTarget(title))
                + "\tdetail=" + safeLogValue(detail)
                + "\tpreview=" + safeLogValue(preview(content, 160));

            appendFileLine(fingerprintFilePath, line);
        }

        private string preview(string text, int maxLength) {
            if (string.IsNullOrEmpty(text)) return "";
            string cleaned = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            return cleaned.Length <= maxLength ? cleaned : cleaned.Substring(0, maxLength) + "...";
        }

        private string safeLogValue(string value) {
            return (value ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        }

        private void appendFileLine(string path, string line) {
            try {
                lock (fileLogLock) {
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                }
            } catch { }
        }

        // === Logging: Write to vPilot debug window (pink color) ===

        // Pink color for [MLIVE] logs
        private static readonly System.Drawing.Color logColor = System.Drawing.Color.FromArgb(255, 105, 180);

        private void log(string text) {
            string line = DateTime.Now.ToString("HH:mm:ss") + " " + text;
            if (fileLogEnabled && !string.IsNullOrEmpty(logFilePath)) {
                appendFileLine(logFilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\t" + safeLogValue(text));
            }
            try {
                foreach (Form form in Application.OpenForms) {
                    appendToReadonlyTextBoxes(form, line);
                }
            } catch { }
        }

        private void appendToReadonlyTextBoxes(Form form, string line) {
            try {
                foreach (Control control in form.Controls) {
                    appendColoredText(control, line);
                    if (control.HasChildren) {
                        appendRecursive(control, line);
                    }
                }
            } catch { }
        }

        private void appendRecursive(Control parent, string line) {
            try {
                foreach (Control control in parent.Controls) {
                    appendColoredText(control, line);
                    if (control.HasChildren) {
                        appendRecursive(control, line);
                    }
                }
            } catch { }
        }

        private void appendColoredText(Control control, string line) {
            try {
                // Try RichTextBox first (supports colored text)
                RichTextBox rtb = control as RichTextBox;
                if (rtb != null && rtb.Multiline && rtb.ReadOnly && rtb.Visible) {
                    if (rtb.InvokeRequired) {
                        rtb.BeginInvoke(new Action(() => {
                            rtb.SelectionStart = rtb.TextLength;
                            rtb.SelectionColor = logColor;
                            rtb.AppendText("\r\n" + line);
                            rtb.SelectionColor = rtb.ForeColor;
                        }));
                    } else {
                        rtb.SelectionStart = rtb.TextLength;
                        rtb.SelectionColor = logColor;
                        rtb.AppendText("\r\n" + line);
                        rtb.SelectionColor = rtb.ForeColor;
                    }
                    return;
                }

                // Fallback to TextBox (no color support)
                TextBoxBase textBox = control as TextBoxBase;
                if (textBox != null && textBox.Multiline && textBox.ReadOnly && textBox.Visible) {
                    if (textBox.InvokeRequired) {
                        textBox.BeginInvoke(new Action(() => textBox.AppendText("\r\n" + line)));
                    } else {
                        textBox.AppendText("\r\n" + line);
                    }
                }
            } catch { }
        }

        // === INI Config ===

        private void loadSettings() {
            RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\vPilot");
            if (registryKey == null) {
                log("ERROR: Registry key not found");
                return;
            }

            string vPilotPath = (string)registryKey.GetValue("Install_Dir");
            string configFile = vPilotPath + "\\Plugins\\vPilot-MessageLive.ini";
            string pluginPath = Path.GetDirectoryName(configFile);

            settingsFile = new IniFile(configFile);

            apiKey = settingsFile.KeyExists("ApiKey", "MessageLive") ? settingsFile.Read("ApiKey", "MessageLive") : null;
            encryptionKey = settingsFile.KeyExists("EncryptionKey", "MessageLive") ? settingsFile.Read("EncryptionKey", "MessageLive") : null;
            relayPrivate = settingsFile.KeyExists("Private", "Relay") ? bool.Parse(settingsFile.Read("Private", "Relay")) : true;
            relayRadio = settingsFile.KeyExists("Radio", "Relay") ? bool.Parse(settingsFile.Read("Radio", "Relay")) : true;
            relaySelcal = settingsFile.KeyExists("Selcal", "Relay") ? bool.Parse(settingsFile.Read("Selcal", "Relay")) : true;
            relayDisconnect = settingsFile.KeyExists("Disconnect", "Relay") ? bool.Parse(settingsFile.Read("Disconnect", "Relay")) : true;
            receiveEnabled = settingsFile.KeyExists("Enabled", "Receive") ? bool.Parse(settingsFile.Read("Enabled", "Receive")) : true;
            receiveInterval = settingsFile.KeyExists("Interval", "Receive") ? int.Parse(settingsFile.Read("Interval", "Receive")) : 3;
            deleteAfterProcess = settingsFile.KeyExists("DeleteAfterProcess", "Receive") ? bool.Parse(settingsFile.Read("DeleteAfterProcess", "Receive")) : false;
            commandsEnabled = settingsFile.KeyExists("Enabled", "Commands") ? bool.Parse(settingsFile.Read("Enabled", "Commands")) : true;
            commandTitle = settingsFile.KeyExists("Title", "Commands") ? settingsFile.Read("Title", "Commands") : "command";
            commandPrefix = settingsFile.KeyExists("Prefix", "Commands") ? settingsFile.Read("Prefix", "Commands") : "!";
            statusEnabled = settingsFile.KeyExists("Enabled", "Status") ? bool.Parse(settingsFile.Read("Enabled", "Status")) : true;
            statusInterval = settingsFile.KeyExists("Interval", "Status") ? int.Parse(settingsFile.Read("Interval", "Status")) : 60;
            queueEnabled = settingsFile.KeyExists("Enabled", "Queue") ? bool.Parse(settingsFile.Read("Enabled", "Queue")) : true;
            queueRetryInterval = settingsFile.KeyExists("RetryInterval", "Queue") ? int.Parse(settingsFile.Read("RetryInterval", "Queue")) : 10;
            queueMaxAttempts = settingsFile.KeyExists("MaxAttempts", "Queue") ? int.Parse(settingsFile.Read("MaxAttempts", "Queue")) : 5;
            relayControllers = settingsFile.KeyExists("Controllers", "Relay") ? bool.Parse(settingsFile.Read("Controllers", "Relay")) : false;
            fileLogEnabled = settingsFile.KeyExists("Enabled", "Log") ? bool.Parse(settingsFile.Read("Enabled", "Log")) : true;
            fingerprintLogEnabled = settingsFile.KeyExists("Fingerprints", "Log") ? bool.Parse(settingsFile.Read("Fingerprints", "Log")) : true;
            string configuredLogFile = settingsFile.KeyExists("File", "Log") ? settingsFile.Read("File", "Log") : "vPilot-MessageLive.log";
            string configuredFingerprintFile = settingsFile.KeyExists("FingerprintFile", "Log") ? settingsFile.Read("FingerprintFile", "Log") : "vPilot-MessageLive.fingerprints.log";
            logFilePath = resolveLogPath(pluginPath, configuredLogFile);
            fingerprintFilePath = resolveLogPath(pluginPath, configuredFingerprintFile);

            settingsLoaded = true;
            log("Settings loaded. Log=" + logFilePath + " Fingerprints=" + fingerprintFilePath);
        }

        private string resolveLogPath(string pluginPath, string configuredPath) {
            if (string.IsNullOrWhiteSpace(configuredPath)) {
                return Path.Combine(pluginPath, "vPilot-MessageLive.log");
            }

            return Path.IsPathRooted(configuredPath) ? configuredPath : Path.Combine(pluginPath, configuredPath);
        }
    }

    internal class PendingOutboundMessage {
        public string Title;
        public string Content;
        public string Fingerprint;
        public int Attempts;
        public DateTime CreatedAtUtc;
        public string LastError;
    }
}
