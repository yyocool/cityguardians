using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CityGuardians.Realtime
{
    /// <summary>연결 수의 한도 (전체, 주소마다).</summary>
    public sealed class ConnectionLimits
    {
        private readonly Dictionary<string, int> perIp = new Dictionary<string, int>();
        private readonly object gate = new object();
        private readonly int max;
        private readonly int maxPerIp;
        private int total;

        public ConnectionLimits(int max, int maxPerIp)
        {
            this.max = max;
            this.maxPerIp = maxPerIp;
        }

        public int Total
        {
            get { lock (gate) return total; }
        }

        public bool TryEnter(string ip)
        {
            lock (gate)
            {
                perIp.TryGetValue(ip, out int count);
                if (total >= max || count >= maxPerIp) return false;
                total++;
                perIp[ip] = count + 1;
                return true;
            }
        }

        public void Leave(string ip)
        {
            lock (gate)
            {
                total--;
                if (perIp.TryGetValue(ip, out int count))
                {
                    if (count <= 1) perIp.Remove(ip);
                    else perIp[ip] = count - 1;
                }
            }
        }
    }

    /// <summary>
    /// 실시간 서버 (docs/COOP_DEFENSE_SYSTEM.md 4~5장).
    ///
    ///   GET /health   살아 있는지 (방과 연결의 수만 알려 준다)
    ///   GET /ws       WebSocket. 첫 메시지로 입장권을 내야 한다: {"type":"join","ticket":"..."}
    ///
    /// 입장권은 주소에 넣지 않는다 (주소는 프록시의 접속 기록에 남는다).
    /// </summary>
    public sealed class RealtimeHost
    {
        public WebApplication App { get; private set; }
        public RoomManager Rooms { get; private set; }
        public ConnectionLimits Limits { get; private set; }
        public NonceCache Nonces { get; } = new NonceCache();

        private RealtimeOptions options;
        private Signer signer;
        private Func<long> clock;
        private ILogger log;

        public static RealtimeHost Build(RealtimeOptions options, IGameApi api = null, Func<long> clock = null, Action<ILoggingBuilder> logging = null)
        {
            var host = new RealtimeHost { options = options, clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds()) };
            host.signer = new Signer(options.Secret);
            host.Limits = new ConnectionLimits(options.MaxConnections, options.MaxConnectionsPerIp);

            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls(options.Listen);
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.AddServerHeader = false;
                kestrel.Limits.MaxRequestBodySize = 16 * 1024;
                kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
                kestrel.Limits.MaxConcurrentConnections = options.MaxConnections + 200L;
                kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            });
            builder.Logging.ClearProviders();
            if (logging != null) logging(builder.Logging);
            else builder.Logging.AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            });
            builder.Logging.SetMinimumLevel(LogLevel.Information);
            builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));

            WebApplication app = builder.Build();
            host.App = app;
            host.log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CG.Realtime");
            host.Rooms = new RoomManager(api ?? new HttpGameApi(options, host.signer, host.clock), options, app.Services.GetRequiredService<ILoggerFactory>());

            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
            app.Use(async (context, next) =>
            {
                context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                await next(context);
            });
            app.MapGet("/health", () => Results.Json(new { ok = true, rooms = host.Rooms.RoomCount, connections = host.Limits.Total }));
            app.Map("/ws", host.AcceptAsync);
            app.Lifetime.ApplicationStopping.Register(() => host.Rooms.StopAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult());
            return host;
        }

        private string ClientIp(HttpContext context)
        {
            if (options.TrustProxy)
            {
                // 앞단 프록시가 붙인 마지막 값만 믿는다 (앞쪽 값은 보내는 쪽이 지어낼 수 있다).
                string forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
                int comma = forwarded.LastIndexOf(',');
                string last = (comma >= 0 ? forwarded.Substring(comma + 1) : forwarded).Trim();
                if (last.Length > 0 && last.Length <= 45 && System.Net.IPAddress.TryParse(last, out _)) return last;
            }
            return context.Connection.RemoteIpAddress != null ? context.Connection.RemoteIpAddress.ToString() : "unknown";
        }

        private async Task AcceptAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            // 브라우저는 Origin 을 붙인다. 허용한 곳이 아니면 받지 않는다 (다른 사이트가 사용자의 브라우저로 붙는 것을 막는다).
            string origin = context.Request.Headers["Origin"].ToString().TrimEnd('/');
            if (origin.Length > 0 && !options.AllowedOrigins.Contains(origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            string ip = ClientIp(context);
            if (!Limits.TryEnter(ip))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            try
            {
                using (WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false))
                {
                    var connection = new Connection(socket) { Ip = ip };
                    Task sender = connection.RunSenderAsync();
                    try
                    {
                        await ServeAsync(connection).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        log.LogError(error, "연결을 처리하다 오류가 났습니다.");
                    }
                    connection.Close(WebSocketCloseStatus.NormalClosure, "bye");
                    await sender.ConfigureAwait(false);
                }
            }
            finally
            {
                Limits.Leave(ip);
            }
        }

        private static void Fail(Connection connection, string reason)
        {
            connection.Send(Wire.Bytes(Messages.Error(reason)));
            connection.Close(WebSocketCloseStatus.PolicyViolation, reason);
        }

        private async Task ServeAsync(Connection connection)
        {
            // 첫 메시지(입장권)는 다른 메시지보다 길 수 있다.
            int joinLimit = Signer.MaxTicketLength + 256;
            var buffer = new byte[Math.Max(joinLimit, options.MaxMessageBytes) + 1];

            ArraySegment<byte>? first = await connection.ReceiveAsync(buffer, options.JoinTimeoutMs).ConfigureAwait(false);
            if (first == null) return;
            ClientMessage join = first.Value.Count <= joinLimit ? ClientMessage.Parse(first.Value) : null;
            if (join == null || join.Type != ClientMessage.Join)
            {
                Fail(connection, "bad_request");
                return;
            }
            long now = clock();
            Ticket ticket = signer.VerifyTicket(join.Ticket, now);
            // 한 번 쓴 입장권은 다시 받지 않는다 (가로챈 입장권으로 남의 자리에 들어오지 못하게).
            if (ticket == null || !Nonces.TryUse(ticket, now))
            {
                Fail(connection, "bad_ticket");
                return;
            }
            (Room room, string error) = await Rooms.FindOrCreateAsync(ticket.Sid).ConfigureAwait(false);
            if (room == null)
            {
                Fail(connection, error);
                return;
            }
            string rejected = await room.JoinAsync(ticket, connection).ConfigureAwait(false);
            if (rejected != null)
            {
                Fail(connection, rejected);
                return;
            }

            try
            {
                // 1초에 보낼 수 있는 수를 넘으면 끊는다 (조금 몰아서 보내는 것은 봐준다).
                double tokens = options.MaxMessagesPerSecond * 2.0;
                long last = Stopwatch.GetTimestamp();
                while (true)
                {
                    ArraySegment<byte>? received = await connection.ReceiveAsync(buffer, options.IdleTimeoutMs).ConfigureAwait(false);
                    if (received == null) return;
                    if (received.Value.Count > options.MaxMessageBytes)
                    {
                        connection.Close(WebSocketCloseStatus.MessageTooBig, "too_big");
                        return;
                    }
                    long stamp = Stopwatch.GetTimestamp();
                    tokens = Math.Min(options.MaxMessagesPerSecond * 2.0,
                        tokens + (stamp - last) / (double)Stopwatch.Frequency * options.MaxMessagesPerSecond);
                    last = stamp;
                    if (tokens < 1.0)
                    {
                        log.LogWarning("방 {Session}: {Pid} 가 메시지를 너무 많이 보내 끊습니다.", room.Id, ticket.Pid);
                        connection.Close(WebSocketCloseStatus.PolicyViolation, "flood");
                        return;
                    }
                    tokens -= 1.0;

                    ClientMessage message = ClientMessage.Parse(received.Value);
                    if (message == null || message.Type == ClientMessage.Join)
                    {
                        connection.Send(Wire.Bytes(Messages.Reject("message", "bad_request")));
                        continue;
                    }
                    if (message.Type == ClientMessage.Alive) continue;
                    if (message.Type == ClientMessage.Leave) return;
                    if (!room.Post(new ClientCommand { Pid = ticket.Pid, Connection = connection, Message = message }))
                    {
                        // 방이 닫혔다 (또는 명령이 너무 밀렸다).
                        if (room.Completion.IsCompleted) return;
                    }
                }
            }
            finally
            {
                room.Post(new LeaveCommand { Pid = ticket.Pid, Connection = connection });
            }
        }
    }
}
