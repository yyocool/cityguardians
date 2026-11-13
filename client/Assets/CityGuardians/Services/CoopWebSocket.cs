using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CityGuardians.Services
{
    /// <summary>
    /// 실시간 서버와의 WebSocket 연결. 주고받는 일은 뒤에서 하고, 게임은 매 프레임 받은 것을 꺼내 간다.
    ///
    /// - 주소는 wss:// 만 받는다 (기기 안의 개발 서버만 ws://). 입장권이 평문으로 지나가지 않게 한다.
    /// - 입장권은 주소에 넣지 않는다. 연결된 뒤 첫 메시지로 보낸다 (RemoteCoopLink).
    /// - WebGL 에서는 쓸 수 없다 (브라우저의 WebSocket 을 쓰는 구현이 따로 필요하다. 준비 중).
    /// </summary>
    public sealed class WebSocketCoopSocket : ICoopSocket, IDisposable
    {
        private const int MaxMessageBytes = 1 << 20;
        private const int MaxOutbox = 256;

        private readonly ClientWebSocket _socket = new ClientWebSocket();
        private readonly ConcurrentQueue<string> _inbox = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _outbox = new ConcurrentQueue<string>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private volatile bool _open;
        private volatile bool _closed;
        private volatile string _closeReason;
        private int _queued;

        public bool IsOpen => _open && !_closed;
        public bool IsClosed => _closed;
        public string CloseReason => _closeReason;

        /// <summary>쓸 수 없는 주소면 그 이유. 쓸 수 있으면 null.</summary>
        public static string UrlProblem(string url)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return "주소가 올바르지 않아요.";
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                return "주소가 올바르지 않아요.";
            if (uri.Scheme == "wss") return null;
            if (uri.Scheme == "ws" && uri.IsLoopback) return null;
            return "안전한 연결(wss)만 쓸 수 있어요.";
        }

        public WebSocketCoopSocket(string url)
        {
            string problem = UrlProblem(url);
            if (problem != null)
            {
                _closeReason = "bad_url";
                _closed = true;
                return;
            }
            var uri = new Uri(url);
            Task.Run(() => RunAsync(uri));
        }

        private async Task RunAsync(Uri uri)
        {
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await _socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
                }
                _open = true;
                Task sender = SendLoopAsync();
                await ReceiveLoopAsync().ConfigureAwait(false);
                _cancel.Cancel();
                await sender.ConfigureAwait(false);
            }
            catch (Exception error) when (error is WebSocketException || error is OperationCanceledException || error is ObjectDisposedException
                                          || error is InvalidOperationException || error is System.IO.IOException
                                          || error is System.Net.Sockets.SocketException)
            {
                if (_closeReason == null && !_open) _closeReason = "unreachable";
            }
            finally
            {
                _closed = true;
                _open = false;
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[16 * 1024];
            var message = new System.IO.MemoryStream();
            while (!_cancel.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancel.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _closeReason = string.IsNullOrEmpty(result.CloseStatusDescription) ? _closeReason : result.CloseStatusDescription;
                    try
                    {
                        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is WebSocketException || error is OperationCanceledException)
                    {
                        // 이미 끊겼다.
                    }
                    return;
                }
                if (result.MessageType != WebSocketMessageType.Text) continue;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                {
                    _closeReason = "too_big";
                    return;
                }
                if (!result.EndOfMessage) continue;
                _inbox.Enqueue(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                message.SetLength(0);
            }
        }

        private async Task SendLoopAsync()
        {
            try
            {
                while (!_cancel.IsCancellationRequested)
                {
                    await _signal.WaitAsync(_cancel.Token).ConfigureAwait(false);
                    while (_outbox.TryDequeue(out string json))
                    {
                        Interlocked.Decrement(ref _queued);
                        byte[] bytes = Encoding.UTF8.GetBytes(json);
                        await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cancel.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception error) when (error is WebSocketException || error is OperationCanceledException || error is ObjectDisposedException
                                          || error is InvalidOperationException)
            {
                // 받는 쪽이 닫힘을 알린다.
            }
        }

        public void Send(string json)
        {
            if (!IsOpen || string.IsNullOrEmpty(json)) return;
            // 보내지 못한 것이 너무 쌓이면 버린다 (연결이 막힌 것이다).
            if (Interlocked.Increment(ref _queued) > MaxOutbox)
            {
                Interlocked.Decrement(ref _queued);
                return;
            }
            _outbox.Enqueue(json);
            _signal.Release();
        }

        public bool TryReceive(out string json) => _inbox.TryDequeue(out json);

        public void Close()
        {
            if (_closed && !_open) return;
            Task.Run(async () =>
            {
                try
                {
                    // 보내려던 것(나간다는 인사)이 나갈 틈을 준다.
                    for (int i = 0; i < 20 && Volatile.Read(ref _queued) > 0; i++) await Task.Delay(10).ConfigureAwait(false);
                    if (_socket.State == WebSocketState.Open)
                    {
                        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (error is WebSocketException || error is OperationCanceledException || error is ObjectDisposedException
                                              || error is InvalidOperationException)
                {
                    // 이미 끊겼다.
                }
                finally
                {
                    _cancel.Cancel();
                    _closed = true;
                    _open = false;
                }
            });
        }

        public void Dispose()
        {
            _cancel.Cancel();
            _closed = true;
            _open = false;
            _socket.Dispose();
        }
    }
}
