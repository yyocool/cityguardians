using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CityGuardians.Realtime
{
    /// <summary>
    /// 한 플레이어의 WebSocket 연결. 보내는 것은 줄을 세워 한 번에 하나씩 보낸다.
    /// 받는 쪽이 느려서 줄이 꽉 차면 그 연결을 끊는다 (한 사람 때문에 방 전체가 밀리지 않게).
    /// </summary>
    public sealed class Connection
    {
        private const int OutboxCapacity = 256;
        private static long nextId;

        private readonly WebSocket socket;
        private readonly Channel<byte[]> outbox = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(OutboxCapacity) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource closed = new CancellationTokenSource();
        private int closing;
        private string closeReason = "bye";
        private WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure;

        public long Id { get; } = Interlocked.Increment(ref nextId);
        public string Pid;
        public string Ip;
        public CancellationToken Closed => closed.Token;
        public bool IsClosing => Volatile.Read(ref closing) != 0;

        public Connection(WebSocket socket)
        {
            this.socket = socket;
        }

        /// <summary>보낼 줄에 넣는다. 줄이 꽉 찼으면 연결을 끊는다.</summary>
        public void Send(byte[] utf8)
        {
            if (IsClosing) return;
            if (!outbox.Writer.TryWrite(utf8)) Close(WebSocketCloseStatus.PolicyViolation, "slow");
        }

        /// <summary>남은 것을 보낸 뒤 닫는다. 여러 번 불러도 한 번만 닫힌다.</summary>
        public void Close(WebSocketCloseStatus status, string reason)
        {
            if (Interlocked.Exchange(ref closing, 1) != 0) return;
            closeStatus = status;
            closeReason = reason;
            outbox.Writer.TryComplete();
        }

        /// <summary>보내는 일을 맡는다. 연결이 닫힐 때까지 돈다.</summary>
        public async Task RunSenderAsync()
        {
            try
            {
                while (await outbox.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (outbox.Reader.TryRead(out byte[] message))
                    {
                        if (socket.State != WebSocketState.Open) return;
                        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                            await socket.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
                    }
                }
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        await socket.CloseOutputAsync(closeStatus, closeReason, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is WebSocketException || error is OperationCanceledException || error is ObjectDisposedException
                || error is InvalidOperationException)
            {
                // 상대가 먼저 끊었다.
            }
            finally
            {
                Interlocked.Exchange(ref closing, 1);
                outbox.Writer.TryComplete();
                // 상대가 닫는 인사를 돌려줄 시간을 조금 준 뒤에 받는 쪽도 멈춘다.
                try
                {
                    closed.CancelAfter(TimeSpan.FromSeconds(2));
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        /// <summary>
        /// 메시지 하나를 받는다. 너무 크거나, 글이 아니거나, 시간이 지나도록 오지 않거나, 연결이 닫혔으면 null.
        /// </summary>
        public async Task<ArraySegment<byte>?> ReceiveAsync(byte[] buffer, int timeoutMs)
        {
            int length = 0;
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token))
                {
                    timeout.CancelAfter(timeoutMs);
                    while (true)
                    {
                        if (length >= buffer.Length)
                        {
                            Close(WebSocketCloseStatus.MessageTooBig, "too_big");
                            return null;
                        }
                        WebSocketReceiveResult result = await socket
                            .ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), timeout.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) return null;
                        if (result.MessageType != WebSocketMessageType.Text)
                        {
                            Close(WebSocketCloseStatus.InvalidMessageType, "text_only");
                            return null;
                        }
                        length += result.Count;
                        if (result.EndOfMessage) return new ArraySegment<byte>(buffer, 0, length);
                    }
                }
            }
            catch (Exception error) when (error is WebSocketException || error is OperationCanceledException || error is ObjectDisposedException
                || error is InvalidOperationException)
            {
                return null;
            }
        }
    }
}
