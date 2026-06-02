using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using Microsoft.Win32;

using RossCarlson.Vatsim.Vpilot.Plugins;
using RossCarlson.Vatsim.Vpilot.Plugins.Events;

namespace vPilot_MessageLive {
    public class Main : IPlugin {

        public static string version = "2.1.0";

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

        // Receive polling
        private System.Timers.Timer receiveTimer;
        private HashSet<string> seenMessageIds = new HashSet<string>();

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

            log("Event handlers registered");
            log("SSO server: http://localhost:12345/");
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
            } else {
                log("ERROR: API key verification failed — open https://mlive.uk/sso to reconfigure");
            }
        }

        // === Receive: Poll MessageLive → inject into vPilot ===

        private void startReceive() {
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

            var messages = await client.GetMessages();
            foreach (var msg in messages) {
                if (seenMessageIds.Contains(msg.id)) continue;
                seenMessageIds.Add(msg.id);

                // Skip empty messages
                if (string.IsNullOrWhiteSpace(msg.content)) continue;

                // Skip messages sent by this plugin (prevent feedback loop)
                if (msg.from == "vPilot" || msg.from == "api" || msg.from == "debug") {
                    continue;
                }

                // Inject into vPilot
                try {
                    // If has a recipient (title), send as private message
                    if (!string.IsNullOrEmpty(msg.title)) {
                        log("PM to " + msg.title + ": " + msg.content);
                        vPilot.SendPrivateMessage(msg.title, msg.content);
                    }
                    // Also log the message
                    log("Received: " + msg.content);
                } catch (Exception ex) {
                    log("Inject error: " + ex.Message);
                }
            }

            // Trim seen IDs to prevent memory leak
            if (seenMessageIds.Count > 500) {
                seenMessageIds.Clear();
            }
        }

        // === Send: vPilot events → MessageLive ===

        private void onNetworkConnected(object sender, NetworkConnectedEventArgs e) {
            connectedCallsign = e.Callsign;
            log("Network connected: " + e.Callsign);
            client?.SendMessage("vPilot", "Connected to network as " + e.Callsign);
        }

        private void onNetworkDisconnected(object sender, EventArgs e) {
            log("Network disconnected");
            connectedCallsign = null;
            if (relayDisconnect) {
                client?.SendMessage("vPilot", "Disconnected from network");
            }
        }

        private void onPrivateMessage(object sender, PrivateMessageReceivedEventArgs e) {
            log("Private message from " + e.From + ": " + e.Message);
            client?.SendMessage(e.From, e.Message);
        }

        private void onRadioMessage(object sender, RadioMessageReceivedEventArgs e) {
            if (e.Message.Contains(connectedCallsign)) {
                log("Radio message from " + e.From + ": " + e.Message);
                client?.SendMessage(e.From, e.Message);
            }
        }

        private void onSelcalAlert(object sender, SelcalAlertReceivedEventArgs e) {
            log("SELCAL alert from " + e.From);
            client?.SendMessage(e.From, "SELCAL Alert");
        }

        // === Logging: Write to vPilot debug window (pink color) ===

        // Pink color for [MLIVE] logs
        private static readonly System.Drawing.Color logColor = System.Drawing.Color.FromArgb(255, 105, 180);

        private void log(string text) {
            string line = DateTime.Now.ToString("HH:mm:ss") + " " + text;
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

            settingsFile = new IniFile(configFile);

            apiKey = settingsFile.KeyExists("ApiKey", "MessageLive") ? settingsFile.Read("ApiKey", "MessageLive") : null;
            encryptionKey = settingsFile.KeyExists("EncryptionKey", "MessageLive") ? settingsFile.Read("EncryptionKey", "MessageLive") : null;
            relayPrivate = settingsFile.KeyExists("Private", "Relay") ? bool.Parse(settingsFile.Read("Private", "Relay")) : true;
            relayRadio = settingsFile.KeyExists("Radio", "Relay") ? bool.Parse(settingsFile.Read("Radio", "Relay")) : true;
            relaySelcal = settingsFile.KeyExists("Selcal", "Relay") ? bool.Parse(settingsFile.Read("Selcal", "Relay")) : true;
            relayDisconnect = settingsFile.KeyExists("Disconnect", "Relay") ? bool.Parse(settingsFile.Read("Disconnect", "Relay")) : true;
            receiveEnabled = settingsFile.KeyExists("Enabled", "Receive") ? bool.Parse(settingsFile.Read("Enabled", "Receive")) : true;
            receiveInterval = settingsFile.KeyExists("Interval", "Receive") ? int.Parse(settingsFile.Read("Interval", "Receive")) : 3;

            settingsLoaded = true;
        }
    }
}
