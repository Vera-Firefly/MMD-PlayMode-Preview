using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

/// <summary>Optional OBS WebSocket 5.x recording control. Network I/O never blocks playback updates.</summary>
[InitializeOnLoad]
internal static class MmdPreviewObsRecording
{
    private const string EndpointKey = "MmdPlayModePreview.ObsEndpoint";
    // Keep the password in memory only: never serialize it into scenes, prefs or source control.
    internal static string Password = "";
    internal static string Endpoint
    {
        get => EditorPrefs.GetString(EndpointKey, "ws://127.0.0.1:4455");
        set => EditorPrefs.SetString(EndpointKey, value);
    }
    internal static string Status { get; private set; } = "未连接 / Disconnected";
    internal static bool IsConnected => client != null && client.Connected;
    internal static bool IsBusy => pending != 0;
    internal static bool HasSession => owner != null || pending != 0;
    private static readonly ConcurrentQueue<Action> Completions = new ConcurrentQueue<Action>();
    private static Client client;
    private static MmdPlayModePreview owner;
    private static Task tail = Task.CompletedTask;
    private static int pending, generation;
    private static bool allowPlay;

    static MmdPreviewObsRecording()
    {
        MmdPlayModePreview.EditorBeforePlay = BeforePlay;
        MmdPlayModePreview.EditorPlaybackEnded = Stop;
        MmdPlayModePreview.EditorReleaseObs = Stop;
        EditorApplication.update += Pump;
        AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
        EditorApplication.quitting += Shutdown;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingPlayMode) Shutdown();
        };
    }

    private static void Pump()
    {
        while (Completions.TryDequeue(out Action action)) action();
        if (owner != null && (!owner.isActiveAndEnabled || !owner.syncObsRecording || !owner.enableSpoutOutput)) Stop(owner);
    }

    internal static void Connect()
    {
        if (IsBusy || owner != null) return;
        client?.Dispose();
        client = new Client();
        Client connection = client;
        string endpoint = Endpoint, password = Password;
        Status = "正在连接 / Connecting…";
        Queue(async () => await connection.Connect(endpoint, password), () => Status = "已连接 / Connected");
    }

    internal static void Disconnect()
    {
        generation++;
        owner = null;
        Client connection = client;
        Queue(async () =>
        {
            try { if (connection != null) await connection.StopOwned(); }
            finally { connection?.Dispose(); }
        }, () =>
        {
            if (client == connection) client = null;
            Status = "未连接 / Disconnected";
        }, () => { if (client == connection) client = null; });
    }

    private static bool BeforePlay(MmdPlayModePreview preview)
    {
        if (allowPlay || !preview.syncObsRecording) return false;
        if (!preview.enableSpoutOutput || preview.recordVideo)
        {
            Status = "请先启用 Spout2 输出 / Enable Spout2 output first";
            return true;
        }
        if (!IsConnected)
        {
            Status = "请先连接 OBS / Connect to OBS first";
            return true;
        }
        if (IsBusy) return true;
        if (MmdPreviewSpoutOutput.GetSenderName(preview) == null)
        {
            Status = "等待 Spout2 输出就绪，请稍后播放 / Wait for Spout2 output, then press Play";
            return true;
        }
        if (owner != null && owner != preview)
        {
            Status = "另一个预览正在控制 OBS / Another preview controls OBS";
            return true;
        }
        owner = preview;
        int revision = ++generation;
        Client connection = client;
        Status = "等待 OBS 录制 / Waiting for OBS…";
        Queue(async () => await connection.StartOwned(), () =>
        {
            if (generation != revision || preview == null || !preview.isActiveAndEnabled ||
                !preview.syncObsRecording || !preview.enableSpoutOutput || !preview.IsPreviewSessionActive) return;
            Status = "录制中 / Recording";
            allowPlay = true;
            try { preview.PlayPreview(); }
            finally { allowPlay = false; }
        }, () => { if (owner == preview) owner = null; });
        return true;
    }

    private static void Stop(MmdPlayModePreview preview)
    {
        if (owner != preview) return;
        generation++;
        owner = null;
        Client connection = client;
        Queue(async () => await connection.StopOwned(), () => Status = "已停止并保存 / Stopped and saved");
    }

    private static void Queue(Func<Task> operation, Action success, Action failure = null)
    {
        pending++;
        Task previous = tail;
        tail = Task.Run(async () =>
        {
            try
            {
                await previous;
                await operation();
                Completions.Enqueue(() => { pending--; success?.Invoke(); });
            }
            catch (Exception exception)
            {
                // WebSocket exceptions may contain endpoint data; never echo credentials.
                string message = exception is ObsException ? exception.Message : "OBS 连接失败或已断开 / OBS connection failed or disconnected";
                Completions.Enqueue(() =>
                {
                    pending--;
                    Status = message;
                    failure?.Invoke();
                    Debug.LogWarning("MMD Preview OBS: " + message);
                });
            }
        });
    }

    private static void Shutdown()
    {
        generation++;
        owner = null;
        Client connection = client;
        if (connection == null) return;
        Queue(async () => { try { await connection.StopOwned(); } finally { connection.Dispose(); } }, null);
        // Only editor teardown waits, bounded by each request's timeout.
        if (!tail.Wait(3500)) connection.Dispose();
        client = null;
        Password = "";
        Pump();
        Status = "未连接 / Disconnected";
    }

    private sealed class ObsException : Exception { internal ObsException(string message) : base(message) { } }

    private sealed class Client : IDisposable
    {
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private bool owned;
        internal bool Connected => socket.State == WebSocketState.Open && identified;
        private bool identified;
        private int disposed;
        private int requestId;

        internal async Task Connect(string endpoint, string password)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != "ws" && uri.Scheme != "wss") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new ObsException("请输入有效的 ws:// 或 wss:// 地址 / Enter a valid WebSocket address");
            try
            {
                using (var timeout = new CancellationTokenSource(2500))
                {
                    await socket.ConnectAsync(uri, timeout.Token);
                    Envelope hello = await Receive(timeout.Token);
                    if (hello.op != 0 || hello.d == null || hello.d.rpcVersion < 1)
                        throw new ObsException("需要 OBS WebSocket 5.x / OBS WebSocket 5.x required");
                    string auth = null;
                    if (hello.d.authentication != null && !string.IsNullOrEmpty(hello.d.authentication.challenge))
                        auth = Authenticate(password, hello.d.authentication.salt, hello.d.authentication.challenge);
                    await Send(JsonUtility.ToJson(new Identify { d = new Identification { authentication = auth } }), timeout.Token);
                    if ((await Receive(timeout.Token)).op != 2)
                        throw new ObsException("OBS 身份验证失败 / OBS authentication failed");
                    identified = true;
                }
            }
            catch (ObsException) { Dispose(); throw; }
            catch (OperationCanceledException)
            {
                Dispose();
                throw new ObsException("OBS 连接或握手超时，请检查 WebSocket 服务器、地址和端口 / OBS connection or handshake timed out; check the WebSocket server, URL and port");
            }
            catch (Exception exception)
            {
                Dispose();
                for (Exception cause = exception; cause != null; cause = cause.InnerException)
                    if (cause is SocketException socketError && socketError.SocketErrorCode == SocketError.ConnectionRefused)
                        throw new ObsException("OBS 拒绝连接：请在 OBS 的“工具 → WebSocket 服务器设置”启用服务器，并核对地址和端口 / OBS refused the connection: enable Tools → WebSocket Server Settings and check the URL and port");
                throw new ObsException("无法连接 OBS WebSocket，请检查服务器是否启用、地址、端口及密码 / Cannot connect to OBS WebSocket; check server availability, URL, port and password");
            }
        }

        internal async Task StartOwned()
        {
            Response status = await Request("GetRecordStatus");
            if (status.outputActive && !owned)
                throw new ObsException("OBS 已在独立录制，请先停止；不会接管已有录制 / Stop the existing OBS recording first");
            if (!status.outputActive)
            {
                await Request("StartRecord");
                owned = true;
            }
        }

        internal async Task StopOwned()
        {
            if (!owned) return;
            Response status = await Request("GetRecordStatus");
            if (status.outputActive) await Request("StopRecord");
            owned = false;
        }

        private async Task<Response> Request(string type)
        {
            if (!Connected) throw new ObsException("OBS 未连接；请检查录制状态并重新连接 / OBS disconnected; check recording and reconnect");
            using (var timeout = new CancellationTokenSource(2500))
            {
                string id = (++requestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    await Send(JsonUtility.ToJson(new RequestMessage { d = new RequestData { requestType = type, requestId = id } }), timeout.Token);
                    while (true)
                    {
                        Envelope response = await Receive(timeout.Token);
                        if (response.op != 7 || response.d == null || response.d.requestId != id) continue;
                        if (response.d.requestStatus == null || !response.d.requestStatus.result)
                            throw new ObsException("OBS 请求失败 / Request failed: " + type + " (" + (response.d.requestStatus?.code ?? 0) + ")" +
                                (type == "StartRecord" ? "。请检查 OBS 录制路径、编码器及 OBS 日志 / Check the OBS recording path, encoder and OBS log" : ""));
                        return response.d.responseData ?? new Response();
                    }
                }
                catch (ObsException) { throw; }
                catch (OperationCanceledException)
                {
                    Dispose();
                    throw new ObsException("OBS 请求超时 / Request timed out: " + type + "; 请在 OBS 确认录制状态后重新连接 / Check recording in OBS before reconnecting");
                }
                catch { Dispose(); throw; }
            }
        }

        private async Task Send(string text, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }

        private async Task<Envelope> Receive(CancellationToken token)
        {
            byte[] bytes = new byte[4096];
            using (var stream = new MemoryStream())
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), token);
                    if (result.MessageType == WebSocketMessageType.Close && (int?)result.CloseStatus == 4009)
                        throw new ObsException("OBS 身份验证失败，请核对 WebSocket 密码 / OBS authentication failed; check the WebSocket password");
                    if (result.MessageType != WebSocketMessageType.Text)
                        throw new ObsException("OBS 已断开或身份验证失败 / OBS disconnected or authentication failed");
                    stream.Write(bytes, 0, result.Count);
                    if (stream.Length > 1024 * 1024) throw new ObsException("OBS 消息过大 / OBS message too large");
                } while (!result.EndOfMessage);
                return JsonUtility.FromJson<Envelope>(Encoding.UTF8.GetString(stream.ToArray()));
            }
        }

        internal static string Authenticate(string password, string salt, string challenge)
        {
            using (SHA256 sha = SHA256.Create())
            {
                string secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt)));
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            identified = false; socket.Abort(); socket.Dispose();
        }
    }

    [Serializable] private sealed class Identify { public int op = 1; public Identification d; }
    [Serializable] private sealed class Identification { public int rpcVersion = 1; public string authentication; public int eventSubscriptions; }
    [Serializable] private sealed class RequestMessage { public int op = 6; public RequestData d; }
    [Serializable] private sealed class RequestData { public string requestType, requestId; }
#pragma warning disable 0649
    [Serializable] private sealed class Envelope { public int op; public Payload d; }
    [Serializable] private sealed class Payload { public int rpcVersion; public Authentication authentication; public string requestId; public RequestStatus requestStatus; public Response responseData; }
    [Serializable] private sealed class Authentication { public string salt, challenge; }
    [Serializable] private sealed class RequestStatus { public bool result; public int code; }
    [Serializable] private sealed class Response { public bool outputActive; }
#pragma warning restore 0649
}
