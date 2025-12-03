using Chat_app_247.Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using Chat_app_247.Models;
using System.Net.Http;
using System.IO;
using System.Threading;

namespace Chat_app_247.Services
{
    public class FirebaseDatabaseService
    {
        private readonly HttpClient _http = new();

        public async Task PutAsync<T>(string path, T data, string idToken)
        {
            var baseUrl = $"{FirebaseConfigFile.DatabaseURL.TrimEnd('/')}/{path}.json";

            var url = string.IsNullOrEmpty(idToken)
                ? baseUrl
                : $"{baseUrl}?auth={idToken}";

            var json = JsonSerializer.Serialize(data);

            var res = await _http.PutAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

            if (!res.IsSuccessStatusCode)
            {
                var error = await res.Content.ReadAsStringAsync();
                throw new Exception($"Không thể ghi dữ liệu: {error}");
            }
        }

        // ✅ FIX: Thêm method DELETE
        public async Task DeleteAsync(string path, string idToken)
        {
            var baseUrl = $"{FirebaseConfigFile.DatabaseURL.TrimEnd('/')}/{path}.json";

            var url = string.IsNullOrEmpty(idToken)
                ? baseUrl
                : $"{baseUrl}?auth={idToken}";

            var res = await _http.DeleteAsync(url);

            if (!res.IsSuccessStatusCode)
            {
                var error = await res.Content.ReadAsStringAsync();
                throw new Exception($"Không thể xóa dữ liệu: {error}");
            }
        }

        public async Task<string> CreateGroupConversationAsync(
            string groupName,
            List<string> participantIds,
            string currentUserId,
            string idToken,
            string groupImageUrl = null)
        {
            if (string.IsNullOrWhiteSpace(groupName))
                throw new ArgumentException("Tên nhóm không được để trống.", nameof(groupName));

            if (participantIds == null)
                participantIds = new List<string>();

            if (!participantIds.Contains(currentUserId))
                participantIds.Add(currentUserId);

            string conversationId = Guid.NewGuid().ToString("N");

            var conversation = new Conversation
            {
                ConversationId = conversationId,
                IsGroupChat = true,
                GroupName = groupName,
                GroupImageUrl = groupImageUrl,
                ParticipantIds = participantIds,
                AdminIds = new List<string> { currentUserId },
                LastMessage = null,
                TypingIndicator = new Dictionary<string, bool>()
            };

            string path = $"Conversations/{conversationId}";
            await PutAsync(path, conversation, idToken);

            return conversationId;
        }

        public async Task SendSignalAsync(string callId, string key, SignalingMessage data, string idToken)
        {
            string path = $"calls/{callId}/{key}";
            await PutAsync(path, data, idToken);
        }

        public async Task ListenToCallSignals(
            string callId,
            string idToken,
            Action<string, SignalingMessage> onSignalReceived,
            CancellationToken token)
        {
            var baseUrl = $"{FirebaseConfigFile.DatabaseURL.TrimEnd('/')}/calls/{callId}.json";
            var url = string.IsNullOrEmpty(idToken) ? baseUrl : $"{baseUrl}?auth={idToken}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

            HttpResponseMessage response = null;
            Stream stream = null;
            StreamReader reader = null;

            try
            {
                response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    token);

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Lỗi kết nối Firebase: {response.StatusCode}");
                }

                stream = await response.Content.ReadAsStreamAsync(token);
                reader = new StreamReader(stream);

                while (!token.IsCancellationRequested)
                {
                    string line = null;

                    try
                    {
                        line = await reader.ReadLineAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Lỗi đọc stream: {ex.Message}");
                        break;
                    }

                    if (line == null)
                    {
                        System.Diagnostics.Debug.WriteLine("Stream đã đóng");
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (line.StartsWith("data: "))
                    {
                        var json = line.Substring(6).Trim();
                        if (json == "null" || json == "keep-alive") continue;

                        try
                        {
                            // Firebase format: {"path":"/key", "data":{...}}
                            using (JsonDocument doc = JsonDocument.Parse(json))
                            {
                                var root = doc.RootElement;

                                if (root.TryGetProperty("path", out var pathElement) &&
                                    root.TryGetProperty("data", out var dataElement))
                                {
                                    string key = pathElement.GetString()?.Trim('/');

                                    if (string.IsNullOrEmpty(key)) continue;

                                    if (dataElement.ValueKind == JsonValueKind.Null)
                                        continue;

                                    var signal = JsonSerializer.Deserialize<SignalingMessage>(
                                        dataElement.GetRawText());

                                    if (signal != null && !string.IsNullOrEmpty(signal.Type))
                                    {
                                        onSignalReceived?.Invoke(key, signal);
                                    }
                                }
                            }
                        }
                        catch (JsonException ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Lỗi parse JSON: {ex.Message}");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Lỗi xử lý signal: {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Bình thường khi cancel
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi ListenToCallSignals: {ex.Message}");
                throw;
            }
            finally
            {
                reader?.Dispose();
                stream?.Dispose();
                response?.Dispose();
            }
        }
    }
}