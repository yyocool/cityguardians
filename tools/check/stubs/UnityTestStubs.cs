// 형식 검사(tools/check/unity)에만 쓰는 대역. 게임 빌드에는 들어가지 않는다.
// UnityEngine.TestRunner 가운데 PlayMode 테스트가 쓰는 선언만 둔다.
using System;
using System.Text.RegularExpressions;

namespace UnityEngine.TestTools
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class UnityTestAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class UnitySetUpAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class UnityTearDownAttribute : Attribute { }

    public static class LogAssert
    {
        public static bool ignoreFailingMessages { get; set; }
        public static void Expect(LogType type, string message) { }
        public static void Expect(LogType type, Regex message) { }
        public static void NoUnexpectedReceived() { }
    }
}
