// Unity 없이 규칙 테스트를 돌릴 때만 쓰는 대역 (tools/check/rules). 게임 빌드에는 들어가지 않는다.
// UnityEngine.JsonUtility 가 하는 일 가운데 이 프로젝트가 쓰는 부분만 흉내 낸다:
//  - 공개 필드만 읽고 쓴다 (속성은 건드리지 않는다)
//  - 잘못된 JSON 이면 ArgumentException
using System;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace UnityEngine
{
    public static class JsonUtility
    {
        private sealed class FieldsOnlyResolver : DefaultContractResolver
        {
            protected override System.Collections.Generic.List<MemberInfo> GetSerializableMembers(Type objectType)
            {
                var members = new System.Collections.Generic.List<MemberInfo>();
                foreach (FieldInfo field in objectType.GetFields(BindingFlags.Instance | BindingFlags.Public))
                    if (field.GetCustomAttribute<NonSerializedAttribute>() == null) members.Add(field);
                return members;
            }

            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                JsonProperty property = base.CreateProperty(member, memberSerialization);
                // JsonUtility 는 비어 있는(null) 글자를 "" 로 쓴다.
                if (property.PropertyType == typeof(string))
                    property.ValueProvider = new NullAsEmptyString(property.ValueProvider);
                return property;
            }
        }

        private sealed class NullAsEmptyString : IValueProvider
        {
            private readonly IValueProvider _inner;
            public NullAsEmptyString(IValueProvider inner) => _inner = inner;
            public object GetValue(object target) => _inner.GetValue(target) ?? string.Empty;
            public void SetValue(object target, object value) => _inner.SetValue(target, value);
        }

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new FieldsOnlyResolver(),
            ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Include,
            DateParseHandling = DateParseHandling.None,
        };

        public static string ToJson(object obj) => JsonConvert.SerializeObject(obj, Settings);

        public static T FromJson<T>(string json)
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(json, Settings);
            }
            catch (JsonException e)
            {
                throw new ArgumentException("JSON parse error: " + e.Message, e);
            }
        }
    }
}
