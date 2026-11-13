using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CityGuardians.Realtime
{
    public static class Program
    {
        /// <summary>
        /// 실시간 서버를 띄운다. 설정은 환경변수로만 받는다 (docs/SERVER_DEPLOYMENT.md):
        ///   REALTIME_SECRET   게임 서버와 나눠 가진 비밀값 (32자 이상)
        ///   GAME_API_URL      게임 서버 API 의 주소 (예: https://game.example/api/v1)
        ///   REALTIME_LISTEN   듣는 주소 (기본 http://127.0.0.1:8090. TLS 는 앞단의 프록시가 맡는다)
        /// </summary>
        public static async Task<int> Main(string[] args)
        {
            RealtimeOptions options = RealtimeOptions.FromEnvironment();
            List<string> problems = options.FindProblems();
            if (problems.Count > 0)
            {
                Console.Error.WriteLine("CITY GUARDIANS 실시간 서버를 띄울 수 없습니다:");
                foreach (string problem in problems) Console.Error.WriteLine(" - " + problem);
                return 2;
            }
            RealtimeHost host = RealtimeHost.Build(options);
            await host.App.RunAsync().ConfigureAwait(false);
            return 0;
        }
    }
}
