namespace EverQuestStreamDeck
{
    using System;
    using System.Collections.Generic;
    using System.Net.WebSockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    // The Stream Deck software launches this plugin as its own process and talks to it
    // over a local WebSocket, one JSON object per message. That is the whole protocol:
    // register once with the token passed on the command line, then exchange events.
    //
    // Deliberately hand-written rather than pulled from a wrapper library. The surface
    // actually used here is five event names and three commands, and a dependency that
    // has to be shipped alongside the plugin would be more moving parts than the code
    // it replaces.
    internal sealed class StreamDeckClient : IDisposable
    {
        private readonly ClientWebSocket _ws = new ClientWebSocket();
        private readonly Int32 _port;
        private readonly String _uuid;
        private readonly String _registerEvent;
        // One writer at a time: ClientWebSocket allows a single outstanding send, and key
        // images are pushed from the reader loop and the refresh timer at once.
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        public StreamDeckClient(Int32 port, String uuid, String registerEvent)
        {
            this._port = port;
            this._uuid = uuid;
            this._registerEvent = registerEvent;
        }

        public event Action<String, String, JsonElement> KeyDown;      // action, context, payload
        public event Action<String, String, JsonElement> WillAppear;   // action, context, payload
        public event Action<String, String, JsonElement> WillDisappear;
        public event Action<String, String, JsonElement> SettingsChanged;

        public async Task RunAsync(CancellationToken token)
        {
            await this._ws.ConnectAsync(new Uri($"ws://127.0.0.1:{this._port}"), token).ConfigureAwait(false);
            await this.SendAsync(new Dictionary<String, Object>
            {
                ["event"] = this._registerEvent,
                ["uuid"] = this._uuid,
            }, token).ConfigureAwait(false);

            var buffer = new Byte[64 * 1024];
            var message = new StringBuilder();
            while (!token.IsCancellationRequested && this._ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                message.Clear();
                do
                {
                    result = await this._ws.ReceiveAsync(new ArraySegment<Byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) { return; }
                    message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                this.Dispatch(message.ToString());
            }
        }

        private void Dispatch(String json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("event", out var evt)) { return; }

                var name = evt.GetString();
                var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
                var context = root.TryGetProperty("context", out var c) ? c.GetString() : null;
                // Explicitly typed, not var: `default` in a conditional has no type to
                // infer from without one. Cloned because the document is disposed on the
                // way out of this method.
                JsonElement payload = root.TryGetProperty("payload", out var p) ? p.Clone() : default;

                switch (name)
                {
                    case "keyDown": this.KeyDown?.Invoke(action, context, payload); break;
                    case "willAppear": this.WillAppear?.Invoke(action, context, payload); break;
                    case "willDisappear": this.WillDisappear?.Invoke(action, context, payload); break;
                    case "didReceiveSettings": this.SettingsChanged?.Invoke(action, context, payload); break;
                }
            }
            catch (JsonException)
            {
                // A malformed frame is never worth taking the plugin down for.
            }
        }

        // --- Commands ----------------------------------------------------------

        // Returns false if the frame did not go out, so the caller can retry rather than
        // remember it as sent. Silently recording a push that never happened is how a key
        // ends up permanently showing the wrong spell.
        public Task<Boolean> SetImageAsync(String context, Byte[] png, CancellationToken token)
        {
            // Stream Deck wants the image inline as a data URI, not a file path: the key
            // art here is generated per read and never exists on disk.
            var image = png == null ? "" : "data:image/png;base64," + Convert.ToBase64String(png);
            return this.SendAsync(new Dictionary<String, Object>
            {
                ["event"] = "setImage",
                ["context"] = context,
                ["payload"] = new Dictionary<String, Object>
                {
                    ["image"] = image,   // empty string restores the action's default art
                    ["target"] = 0,      // both hardware and software
                },
            }, token);
        }

        public Task SetTitleAsync(String context, String title, CancellationToken token) =>
            this.SendAsync(new Dictionary<String, Object>
            {
                ["event"] = "setTitle",
                ["context"] = context,
                ["payload"] = new Dictionary<String, Object> { ["title"] = title ?? "", ["target"] = 0 },
            }, token);

        public Task LogAsync(String message, CancellationToken token) =>
            this.SendAsync(new Dictionary<String, Object>
            {
                ["event"] = "logMessage",
                ["payload"] = new Dictionary<String, Object> { ["message"] = message ?? "" },
            }, token);

        private async Task<Boolean> SendAsync(Object o, CancellationToken token)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(o);
            await this._sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (this._ws.State != WebSocketState.Open) { return false; }
                await this._ws.SendAsync(new ArraySegment<Byte>(bytes), WebSocketMessageType.Text, true, token)
                    .ConfigureAwait(false);
                return true;
            }
            catch (WebSocketException)
            {
                // The software closed the socket; the process is about to be killed.
                return false;
            }
            finally
            {
                this._sendLock.Release();
            }
        }

        public void Dispose()
        {
            this._sendLock.Dispose();
            this._ws.Dispose();
        }
    }
}
