using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace vPilot_MessageLive {
    internal class MessageLiveClient {

        private const string BASE_URL = "https://mlive.uk";
        private const string USER_AGENT = "vPilot-MessageLive/2.3";

        private static readonly HttpClient client = new HttpClient();

        private string apiKey;
        private Action<string> logAction;

        public MessageLiveClient(string apiKey, Action<string> logger) {
            this.apiKey = apiKey;
            this.logAction = logger;
            // Set default headers for all requests
            if (!client.DefaultRequestHeaders.Contains("User-Agent")) {
                client.DefaultRequestHeaders.Add("User-Agent", USER_AGENT);
            }
            // Cloudflare WAF bypass header
            if (!client.DefaultRequestHeaders.Contains("vpilot-plugins")) {
                client.DefaultRequestHeaders.Add("vpilot-plugins", "true");
            }
        }

        private string escapeJson(string s) {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        /// <summary>
        /// Send a message to MessageLive
        /// </summary>
        public async Task<bool> SendMessage(string title, string content) {
            try {
                string json = "{\"title\":\"" + escapeJson(title) + "\",\"content\":\"" + escapeJson(content) + "\"}";
                var request = new HttpRequestMessage(HttpMethod.Post, BASE_URL + "/api/messages");
                request.Headers.Add("X-API-Key", apiKey);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.SendAsync(request);
                bool ok = response.IsSuccessStatusCode;
                if (!ok) {
                    logAction?.Invoke("Send failed: HTTP " + (int)response.StatusCode);
                }
                return ok;
            } catch (Exception ex) {
                logAction?.Invoke("Send error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Get decrypted messages from MessageLive
        /// </summary>
        public async Task<List<MessageLiveMessage>> GetMessages() {
            try {
                var request = new HttpRequestMessage(HttpMethod.Get, BASE_URL + "/api/messages/decrypted");
                request.Headers.Add("X-API-Key", apiKey);

                var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode) {
                    logAction?.Invoke("Poll failed: HTTP " + (int)response.StatusCode);
                    return new List<MessageLiveMessage>();
                }

                string body = await response.Content.ReadAsStringAsync();
                return parseMessages(body);
            } catch (Exception ex) {
                logAction?.Invoke("Poll error: " + ex.Message);
                return new List<MessageLiveMessage>();
            }
        }

        /// <summary>
        /// Delete a processed message from MessageLive
        /// </summary>
        public async Task<bool> DeleteMessage(string id) {
            if (string.IsNullOrWhiteSpace(id)) return false;

            try {
                var request = new HttpRequestMessage(HttpMethod.Delete, BASE_URL + "/api/messages/" + Uri.EscapeDataString(id));
                request.Headers.Add("X-API-Key", apiKey);

                var response = await client.SendAsync(request);
                bool ok = response.IsSuccessStatusCode;
                if (!ok) {
                    logAction?.Invoke("Delete failed: HTTP " + (int)response.StatusCode);
                }
                return ok;
            } catch (Exception ex) {
                logAction?.Invoke("Delete error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Verify API key is valid
        /// </summary>
        public async Task<bool> Verify() {
            try {
                logAction?.Invoke("Connecting to " + BASE_URL + "...");
                var request = new HttpRequestMessage(HttpMethod.Get, BASE_URL + "/api/messages/decrypted");
                request.Headers.Add("X-API-Key", apiKey);

                var response = await client.SendAsync(request);
                int code = (int)response.StatusCode;
                logAction?.Invoke("Verify: HTTP " + code);

                if (code == 403) {
                    logAction?.Invoke("Hint: check WAF rules");
                }

                return response.IsSuccessStatusCode;
            } catch (Exception ex) {
                logAction?.Invoke("Verify error: " + ex.Message);
                return false;
            }
        }

        private List<MessageLiveMessage> parseMessages(string json) {
            var messages = new List<MessageLiveMessage>();
            try {
                int msgStart = json.IndexOf("[");
                int msgEnd = json.LastIndexOf("]");
                if (msgStart < 0 || msgEnd < 0) return messages;

                string arr = json.Substring(msgStart + 1, msgEnd - msgStart - 1);
                if (string.IsNullOrWhiteSpace(arr)) return messages;

                var items = splitJsonArray(arr);

                foreach (var item in items) {
                    var msg = new MessageLiveMessage();
                    msg.id = extractString(item, "id");
                    msg.title = extractString(item, "title");
                    msg.content = extractString(item, "content");
                    msg.from = extractString(item, "from");
                    msg.created_at = extractLong(item, "created_at");
                    if (!string.IsNullOrEmpty(msg.id)) {
                        messages.Add(msg);
                    }
                }
            } catch { }
            return messages;
        }

        private List<string> splitJsonArray(string arr) {
            var items = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < arr.Length; i++) {
                if (arr[i] == '{') {
                    if (depth == 0) start = i;
                    depth++;
                } else if (arr[i] == '}') {
                    depth--;
                    if (depth == 0) {
                        items.Add(arr.Substring(start, i - start + 1));
                    }
                }
            }
            return items;
        }

        private string extractString(string json, string key) {
            int start = findValueStart(json, key);
            if (start < 0 || start >= json.Length || json[start] != '"') return "";

            start++;
            int end = start;
            while (end < json.Length) {
                if (json[end] == '\\') { end += 2; continue; }
                if (json[end] == '"') break;
                end++;
            }
            return unescapeJson(json.Substring(start, end - start));
        }

        private long extractLong(string json, string key) {
            int start = findValueStart(json, key);
            if (start < 0) return 0;

            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            long.TryParse(json.Substring(start, end - start), out long val);
            return val;
        }

        private int findValueStart(string json, string key) {
            string search = "\"" + key + "\"";
            int start = json.IndexOf(search);
            if (start < 0) return -1;

            start += search.Length;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length || json[start] != ':') return -1;
            start++;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            return start;
        }

        private string unescapeJson(string value) {
            return value
                .Replace("\\\\", "\\")
                .Replace("\\\"", "\"")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t");
        }
    }

    internal class MessageLiveMessage {
        public string id;
        public string title;
        public string content;
        public string from;
        public long created_at;
    }
}
