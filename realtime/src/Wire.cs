using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CityGuardians.Realtime
{
    /// <summary>
    /// 이름을 snake_case 로 바꾼다. 게임 서버(PHP)의 Wire::snakeKey 와 같은 규칙이다:
    /// 소문자나 숫자 바로 뒤에 대문자가 오면 그 사이에 _ 를 넣고, 모두 소문자로 만든다
    /// (playerShotsPer10s → player_shots_per10s).
    /// </summary>
    public sealed class SnakeCasePolicy : JsonNamingPolicy
    {
        public static readonly SnakeCasePolicy Instance = new SnakeCasePolicy();

        public override string ConvertName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var text = new StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c >= 'A' && c <= 'Z')
                {
                    char before = i > 0 ? name[i - 1] : '\0';
                    if ((before >= 'a' && before <= 'z') || (before >= '0' && before <= '9')) text.Append('_');
                    text.Append((char)(c + 32));
                }
                else
                {
                    text.Append(c);
                }
            }
            return text.ToString();
        }
    }

    /// <summary>주고받는 JSON 의 형식. 게임 서버의 API 와 같다 (snake_case, 한글은 그대로).</summary>
    public static class Wire
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = SnakeCasePolicy.Instance,
            IncludeFields = true,
            MaxDepth = 16,
            // 한글 이름을 \uXXXX 로 바꾸지 않는다. 받는 쪽은 HTML 이 아니라 게임 클라이언트다.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.Strict,
        };

        public static byte[] Bytes(object message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);

        public static T Read<T>(JsonElement element) => element.Deserialize<T>(Options);
    }
}
