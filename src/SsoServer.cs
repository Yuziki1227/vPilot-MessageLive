using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace vPilot_MessageLive {
    internal class SsoServer {

        private HttpListener listener;
        private CancellationTokenSource cts;
        private Action<string> logAction;
        private IniFile settingsFile;
        private Action onCredentialsSaved;

        private string savedApiKey = null;
        private string savedEncryptionKey = null;

        public string ApiKey { get { return savedApiKey; } }
        public string EncryptionKey { get { return savedEncryptionKey; } }

        public SsoServer(IniFile settingsFile, Action<string> logger, Action onCredentialsSaved) {
            this.settingsFile = settingsFile;
            this.logAction = logger;
            this.onCredentialsSaved = onCredentialsSaved;
        }

        public void Start() {
            cts = new CancellationTokenSource();
            listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:12345/");
            listener.Start();
            log("SSO server ready");
            Task.Run(() => listenLoop());
        }

        public void Stop() {
            cts?.Cancel();
            listener?.Stop();
            listener?.Close();
            log("[SSO] Server stopped");
        }

        private async Task listenLoop() {
            while (!cts.Token.IsCancellationRequested) {
                try {
                    var context = await listener.GetContextAsync();
                    _ = Task.Run(() => handleRequest(context));
                } catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex) {
                    log("[SSO] Listen error: " + ex.Message);
                }
            }
        }

        private async Task handleRequest(HttpListenerContext context) {
            var req = context.Request;
            var resp = context.Response;

            // CORS headers
            resp.Headers.Add("Access-Control-Allow-Origin", "*");
            resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (req.HttpMethod == "OPTIONS") {
                resp.StatusCode = 204;
                resp.Close();
                return;
            }

            string path = req.Url.AbsolutePath;

            // GET /health — connection check
            if (path == "/health" && req.HttpMethod == "GET") {
                bool configured = !string.IsNullOrEmpty(savedApiKey);
                string json = "{\"status\":\"ok\",\"configured\":" + configured.ToString().ToLower() + "}";
                await sendJson(resp, json);
                return;
            }

            // POST /sso — save credentials
            if (path == "/sso" && req.HttpMethod == "POST") {
                try {
                    string body;
                    using (var reader = new StreamReader(req.InputStream, req.ContentEncoding)) {
                        body = await reader.ReadToEndAsync();
                    }

                    // Parse JSON: {"api_key":"...","encryption_key":"..."}
                    string apiKey = extractJsonString(body, "api_key");
                    string encKey = extractJsonString(body, "encryption_key");

                    if (string.IsNullOrEmpty(apiKey)) {
                        await sendJson(resp, "{\"success\":false,\"error\":\"api_key required\"}");
                        return;
                    }

                    // Save to INI
                    saveCredentials(apiKey, encKey);
                    savedApiKey = apiKey;
                    savedEncryptionKey = encKey;

                    log("[SSO] Credentials saved for API: " + apiKey.Substring(0, Math.Min(8, apiKey.Length)) + "...");

                    // Notify plugin to reload
                    onCredentialsSaved?.Invoke();

                    await sendJson(resp, "{\"success\":true}");
                } catch (Exception ex) {
                    await sendJson(resp, "{\"success\":false,\"error\":\"" + ex.Message.Replace("\"", "\\\"") + "\"}");
                }
                return;
            }

            // GET / — success page
            if (path == "/" && req.HttpMethod == "GET") {
                await sendHtml(resp, getSuccessPage());
                return;
            }

            resp.StatusCode = 404;
            resp.Close();
        }

        private void saveCredentials(string apiKey, string encKey) {
            // Write to INI file
            settingsFile.Write("ApiKey", apiKey, "MessageLive");
            if (!string.IsNullOrEmpty(encKey)) {
                settingsFile.Write("EncryptionKey", encKey, "MessageLive");
            }
            // Enable all relay options by default
            settingsFile.Write("Private", "true", "Relay");
            settingsFile.Write("Radio", "true", "Relay");
            settingsFile.Write("Selcal", "true", "Relay");
            settingsFile.Write("Disconnect", "true", "Relay");
            settingsFile.Write("Enabled", "true", "Receive");
            settingsFile.Write("Interval", "3", "Receive");
        }

        private async Task sendJson(HttpListenerResponse resp, string json) {
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            resp.ContentType = "application/json";
            resp.ContentLength64 = buffer.Length;
            await resp.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            resp.Close();
        }

        private async Task sendHtml(HttpListenerResponse resp, string html) {
            byte[] buffer = Encoding.UTF8.GetBytes(html);
            resp.ContentType = "text/html; charset=utf-8";
            resp.ContentLength64 = buffer.Length;
            await resp.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            resp.Close();
        }

        private string extractJsonString(string json, string key) {
            string search = "\"" + key + "\":\"";
            int start = json.IndexOf(search);
            if (start < 0) return null;
            start += search.Length;
            int end = start;
            while (end < json.Length) {
                if (json[end] == '\\') { end += 2; continue; }
                if (json[end] == '"') break;
                end++;
            }
            return json.Substring(start, end - start).Replace("\\\"", "\"");
        }

        private string getSuccessPage() {
            return @"<!DOCTYPE html><html><head><meta charset=""UTF-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>Connected — MessageLive</title><style>
*{margin:0;padding:0;box-sizing:border-box}body{font-family:-apple-system,BlinkMacSystemFont,'Helvetica Neue',sans-serif;background:#fff;color:#171A20;min-height:100vh;display:flex;align-items:center;justify-content:center}
.c{text-align:center;padding:40px}
.icon{font-size:64px;margin-bottom:16px}
h1{font-size:24px;font-weight:500;margin-bottom:8px}
p{font-size:14px;color:#5C5E62;margin-bottom:24px}
.btn{display:inline-block;padding:12px 32px;font-size:14px;font-weight:500;text-decoration:none;border-radius:4px;background:#3E6AE1;color:#fff}
</style></head><body><div class=""c""><div class=""icon"">✓</div><h1>Connected!</h1><p>Your credentials have been saved to vPilot.<br>You can close this tab and return to vPilot.</p><a href=""https://mlive.uk/dashboard"" class=""btn"">Open Dashboard</a></div></body></html>";
        }

        private void log(string text) {
            logAction?.Invoke(text);
        }
    }
}
