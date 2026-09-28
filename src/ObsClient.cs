using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ObsNetworkViewer
{
    enum RecordState { Unknown, NotRunning, WebSocketUnavailable, PasswordRequired, AuthFailed, Idle, Recording, Paused }

    /// <summary>Queries the local OBS instance through its built-in obs-websocket (v5) server.</summary>
    sealed class ObsClient : IDisposable
    {
        const int WebSocketCloseAuthFailed = 4009;
        static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
        static readonly string[] ObsProcessNames = { "obs64", "obs32", "obs" };

        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        ClientWebSocket socket;
        int nextRequestId;

        public int Port { get; set; }
        public string Password { get; set; }

        public async Task<RecordState> GetRecordStateAsync()
        {
            if (!IsObsProcessRunning())
            {
                Disconnect();
                return RecordState.NotRunning;
            }

            try
            {
                if (socket == null || socket.State != WebSocketState.Open)
                {
                    Disconnect();
                    RecordState? failure = await ConnectAsync();
                    if (failure.HasValue)
                        return failure.Value;
                }

                string requestId = (++nextRequestId).ToString();
                await SendAsync(new Dictionary<string, object>
                {
                    { "op", 6 },
                    { "d", new Dictionary<string, object> { { "requestType", "GetRecordStatus" }, { "requestId", requestId } } }
                });

                while (true)
                {
                    Dictionary<string, object> message = await ReceiveAsync();
                    if (message == null)
                    {
                        Disconnect();
                        return RecordState.WebSocketUnavailable;
                    }
                    if (GetInt(message, "op") != 7)
                        continue;

                    Dictionary<string, object> d = GetObject(message, "d");
                    if (d == null || !requestId.Equals(d["requestId"]))
                        continue;

                    Dictionary<string, object> data = GetObject(d, "responseData");
                    if (data == null)
                        return RecordState.Unknown;
                    if (!GetBool(data, "outputActive"))
                        return RecordState.Idle;
                    return GetBool(data, "outputPaused") ? RecordState.Paused : RecordState.Recording;
                }
            }
            catch (Exception)
            {
                Disconnect();
                return RecordState.WebSocketUnavailable;
            }
        }

        public void Disconnect()
        {
            if (socket == null)
                return;
            try { socket.Abort(); } catch (Exception) { }
            socket.Dispose();
            socket = null;
        }

        public void Dispose()
        {
            Disconnect();
        }

        async Task<RecordState?> ConnectAsync()
        {
            socket = new ClientWebSocket();
            socket.Options.AddSubProtocol("obswebsocket.json");
            using (var cts = new CancellationTokenSource(Timeout))
                await socket.ConnectAsync(new Uri("ws://127.0.0.1:" + Port), cts.Token);

            Dictionary<string, object> hello = await ReceiveAsync();
            if (hello == null || GetInt(hello, "op") != 0)
                throw new IOException("Unexpected obs-websocket handshake.");

            var identify = new Dictionary<string, object> { { "rpcVersion", 1 }, { "eventSubscriptions", 0 } };
            Dictionary<string, object> auth = GetObject(GetObject(hello, "d"), "authentication");
            if (auth != null)
            {
                if (string.IsNullOrEmpty(Password))
                {
                    Disconnect();
                    return RecordState.PasswordRequired;
                }
                string secret = Sha256Base64(Password + auth["salt"]);
                identify["authentication"] = Sha256Base64(secret + auth["challenge"]);
            }

            await SendAsync(new Dictionary<string, object> { { "op", 1 }, { "d", identify } });

            Dictionary<string, object> identified = await ReceiveAsync();
            if (identified == null)
            {
                bool rejected = socket.CloseStatus.HasValue && (int)socket.CloseStatus.Value == WebSocketCloseAuthFailed;
                Disconnect();
                return rejected ? RecordState.AuthFailed : RecordState.WebSocketUnavailable;
            }
            if (GetInt(identified, "op") != 2)
                throw new IOException("obs-websocket did not identify the client.");
            return null;
        }

        async Task SendAsync(Dictionary<string, object> message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(message));
            using (var cts = new CancellationTokenSource(Timeout))
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
        }

        /// <summary>Returns null when the server closed the connection.</summary>
        async Task<Dictionary<string, object>> ReceiveAsync()
        {
            var buffer = new byte[8192];
            using (var stream = new MemoryStream())
            using (var cts = new CancellationTokenSource(Timeout))
            {
                while (true)
                {
                    WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return null;
                    stream.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage)
                        break;
                }
                return json.DeserializeObject(Encoding.UTF8.GetString(stream.ToArray())) as Dictionary<string, object>;
            }
        }

        static bool IsObsProcessRunning()
        {
            foreach (string name in ObsProcessNames)
            {
                Process[] processes = Process.GetProcessesByName(name);
                foreach (Process p in processes)
                    p.Dispose();
                if (processes.Length > 0)
                    return true;
            }
            return false;
        }

        static string Sha256Base64(string text)
        {
            using (SHA256 sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        static Dictionary<string, object> GetObject(Dictionary<string, object> obj, string key)
        {
            object value;
            if (obj == null || !obj.TryGetValue(key, out value))
                return null;
            return value as Dictionary<string, object>;
        }

        static int GetInt(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj.TryGetValue(key, out value) && value != null ? Convert.ToInt32(value) : -1;
        }

        static bool GetBool(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj.TryGetValue(key, out value) && value is bool && (bool)value;
        }
    }
}
