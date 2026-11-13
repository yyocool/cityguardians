// 게임 서버(PHP)의 응답과 클라이언트의 DTO 가 서로 맞는지 대조한다 (docs/API_SPEC.md 1장, docs/TEST_PLAN.md).
//
// 클라이언트에는 아직 서버에 붙는 HTTP 구현이 없어서, 응답 형식이 어긋나도 알아챌 방법이 없었다.
// 서버의 테스트(server/tests/ClientWireTest.php)가 v9.0 응답의 모양(이름과 종류)을 server/tests/fixtures/client-wire.json 에 남기고,
// 이 테스트가 그것으로 DTO 의 필드를 하나씩 확인한다:
//
//   DTO 의 모든 공개 필드는 서버의 응답에 같은 이름으로 있어야 하고, 종류가 맞아야 한다.
//   (서버가 더 보내는 값은 괜찮다. 클라이언트가 읽지 않을 뿐이다.)
//
// 이 파일은 Unity 프로젝트에 들어가지 않는다 (저장소의 server 폴더를 읽기 때문이다). dotnet test 로만 돈다.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CityGuardians.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    public class WireContractTests
    {
        /// <summary>응답마다 클라이언트가 읽는 DTO.</summary>
        private static readonly Dictionary<string, Type> Endpoints = new Dictionary<string, Type>
        {
            { "GET /cities/growth", typeof(CityGrowthListDto) },
            { "GET /cities/{code}/growth", typeof(CityGrowthDto) },
            { "GET /residency", typeof(ResidencyInfoDto) },
            { "POST /residency/move", typeof(MoveResultDto) },
            { "GET /cities/content", typeof(CityContentListDto) },
            { "GET /tax/policies", typeof(TaxPoliciesDto) },
            { "GET /tax/transactions", typeof(TaxTransactionsDto) },
            { "GET /cities/{code}/treasury", typeof(TreasuryDto) },
            { "GET /cities/{code}/treasury/expenditures", typeof(ExpendituresDto) },
            { "GET /cities/budget/options", typeof(BudgetOptionsDto) },
            { "GET /cities/{code}/budget/proposals", typeof(BudgetListDto) },
            { "POST /cities/budget/proposals", typeof(BudgetProposalResultDto) },
            { "POST /cities/budget/proposals/{id}/vote", typeof(BudgetProposalResultDto) },
            { "GET /coop/config", typeof(CoopConfigDto) },
            { "GET /coop/sessions", typeof(CoopSessionsDto) },
            { "POST /coop/sessions", typeof(CoopSessionResultDto) },
            { "POST /coop/sessions/{id}/join", typeof(CoopSessionResultDto) },
            { "POST /coop/sessions/{id}/leave", typeof(CoopSessionResultDto) },
            { "POST /coop/sessions/{id}/ticket", typeof(CoopTicketDto) },
            { "GET /coop/sessions/{id}/result", typeof(CoopResultDto) },
            // v10.0 실시간 대시보드
            { "GET /dashboard/summary", typeof(DashboardSummaryDto) },
        };

        /// <summary>
        /// 한 DTO 를 두 응답이 함께 쓰는데 한쪽에만 오는 값. "응답 → 오지 않아도 되는 필드"로 적는다.
        /// 여기에 없는 필드가 응답에 없으면 실패한다.
        /// </summary>
        private static readonly Dictionary<string, string[]> OnlyElsewhere = new Dictionary<string, string[]>
        {
            // 목록에서는 도시마다 요약만 온다. 누적·증가량·추이·단계별 달성은 상세에서 온다.
            { "GET /cities/growth", new[] { "cities[].cumulativeResidents", "cities[].growth7d", "cities[].growth30d", "cities[].milestones",
                                            "cities[].trend", "milestones[].reached" } },
            // 상세의 단계에는 달성 여부만 붙는다. 설명은 목록의 단계표에 있다.
            { "GET /cities/{code}/growth", new[] { "milestones[].description", "milestones[].opensStreet" } },
        };

        private static JObject _fixture;

        private static JObject Fixture()
        {
            if (_fixture != null) return _fixture;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string path = Path.Combine(dir.FullName, "server", "tests", "fixtures", "client-wire.json");
                if (File.Exists(path)) return _fixture = JObject.Parse(File.ReadAllText(path));
                dir = dir.Parent;
            }
            Assert.Fail("server/tests/fixtures/client-wire.json 을 찾지 못했다. 서버 테스트를 CG_UPDATE_FIXTURES=1 로 돌려 만든다.");
            return null;
        }

        [Test]
        public void EveryV9ResponseHasADto_AndEveryDtoIsChecked()
        {
            JObject fixture = Fixture();
            foreach (string name in Endpoints.Keys) Assert.IsNotNull(fixture[name], name + " 의 표본이 없다");
            // 표본에는 있는데 여기서 대조하지 않는 응답 (클라이언트가 아직 쓰지 않는 것).
            string[] unread = fixture.Properties().Select(p => p.Name).Where(n => !Endpoints.ContainsKey(n)).ToArray();
            CollectionAssert.AreEquivalent(new[] { "GET /cities/budget/proposals/{id}" }, unread);
        }

        [Test]
        public void DtoFields_AreAllSentByTheServer_WithTheSameKind()
        {
            JObject fixture = Fixture();
            var problems = new List<string>();
            foreach (KeyValuePair<string, Type> endpoint in Endpoints)
            {
                string[] optional = OnlyElsewhere.TryGetValue(endpoint.Key, out string[] found) ? found : new string[0];
                var used = new HashSet<string>();
                Check(endpoint.Key, "", endpoint.Value, fixture[endpoint.Key], optional, used, problems);
                foreach (string path in optional)
                    if (!used.Contains(path)) problems.Add($"{endpoint.Key}: 예외로 적어 둔 {path} 는 이제 서버가 보낸다 (예외에서 뺀다)");
            }
            Assert.IsEmpty(problems, "서버의 응답과 클라이언트 DTO 가 다르다:\n" + string.Join("\n", problems));
        }

        private static void Check(string endpoint, string path, Type type, JToken shape, string[] optional, HashSet<string> used, List<string> problems)
        {
            if (!(shape is JObject obj))
            {
                problems.Add($"{endpoint}: {Show(path)} 는 객체여야 하는데 서버는 {Describe(shape)} 를 보낸다");
                return;
            }
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                string here = path + field.Name;
                JToken sent = obj[field.Name];
                if (sent == null)
                {
                    if (optional.Contains(here)) used.Add(here);
                    else problems.Add($"{endpoint}: {here} 를 서버가 보내지 않는다");
                    continue;
                }
                CheckKind(endpoint, here, field.FieldType, sent, optional, used, problems);
            }
        }

        private static void CheckKind(string endpoint, string path, Type type, JToken sent, string[] optional, HashSet<string> used, List<string> problems)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                if (!(sent is JArray list))
                {
                    problems.Add($"{endpoint}: {path} 는 목록이어야 하는데 서버는 {Describe(sent)} 를 보낸다");
                    return;
                }
                if (list.Count == 0)
                {
                    problems.Add($"{endpoint}: {path} 의 표본이 비어 있어 확인할 수 없다 (서버 테스트의 상황에 값을 넣는다)");
                    return;
                }
                CheckKind(endpoint, path + "[]", type.GetGenericArguments()[0], list[0], optional, used, problems);
                return;
            }
            if (type == typeof(string) || type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(bool))
            {
                string kinds = sent.Type == JTokenType.String ? (string)sent : null;
                if (kinds == null)
                {
                    problems.Add($"{endpoint}: {path} 는 값이어야 하는데 서버는 {Describe(sent)} 를 보낸다");
                    return;
                }
                foreach (string kind in kinds.Split('|'))
                {
                    bool fits = type == typeof(string) ? kind == "string" || kind == "null"
                        : type == typeof(bool) ? kind == "bool"
                        : type == typeof(float) ? kind == "int" || kind == "float"
                        : kind == "int";
                    if (!fits) problems.Add($"{endpoint}: {path} 는 {type.Name} 인데 서버는 {kinds} 를 보낸다");
                }
                return;
            }
            if (typeof(IEnumerable).IsAssignableFrom(type))
            {
                problems.Add($"{endpoint}: {path} 의 형식 {type.Name} 은 이 테스트가 모른다");
                return;
            }
            Check(endpoint, path + ".", type, sent, optional, used, problems);
        }

        private static string Show(string path) => path.Length == 0 ? "응답" : path.TrimEnd('.');

        private static string Describe(JToken token) =>
            token == null ? "없음" : token.Type == JTokenType.String ? (string)token : token.Type == JTokenType.Array ? "목록" : "객체";
    }
}
