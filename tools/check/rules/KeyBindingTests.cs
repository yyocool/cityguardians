// 키가 겹치지 않는지 소스에서 확인한다 (docs/HUD_LAYOUT_SYSTEM.md 5장, docs/TEST_PLAN.md 16장).
//
// 이 프로젝트의 키는 모두 Input/PlayerInput.cs 의 KeyboardMouseInput 한 곳에서 읽는다 (게임 코드는 "의도"만 읽는다).
// 그래서 그 파일에서 대시보드의 키(F6~F9, 왼쪽 Alt)가 한 번씩만 쓰이는지, 그리고 다른 파일이 키보드를 직접 읽지 않는지를 보면
// 기존 키와 겹치지 않는다는 것을 알 수 있다.
//
// 이 파일은 Unity 프로젝트에 들어가지 않는다 (저장소의 소스 파일을 읽기 때문이다). dotnet test 로만 돈다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    public class KeyBindingTests
    {
        private static string ClientRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string path = Path.Combine(dir.FullName, "client", "Assets", "CityGuardians");
                if (Directory.Exists(path)) return path;
                dir = dir.Parent;
            }
            Assert.Fail("client/Assets/CityGuardians 를 찾지 못했다.");
            return null;
        }

        [Test]
        public void DashboardKeys_AreUsedOnce_AndByNothingElse()
        {
            string source = File.ReadAllText(Path.Combine(ClientRoot(), "Input", "PlayerInput.cs"));

            // 키마다, 그 키를 읽는 곳의 수.
            var uses = new Dictionary<string, int>();
            foreach (Match match in Regex.Matches(source, @"Pressed\(Key\.(\w+)\)"))
            {
                string key = match.Groups[1].Value;
                uses[key] = uses.TryGetValue(key, out int count) ? count + 1 : 1;
            }

            foreach (string key in new[] { "F6", "F7", "F8", "F9" })
            {
                Assert.IsTrue(uses.ContainsKey(key), key + " 가 대시보드에 묶여 있지 않다");
                Assert.AreEqual(1, uses[key], key + " 를 읽는 곳이 하나가 아니다 (다른 기능과 겹친다)");
            }
            // 다른 F 키는 아직 아무 데도 쓰지 않는다. 쓰게 되면 이 테스트가 알려 준다 (대시보드의 키와 헷갈리지 않는지 볼 것).
            foreach (string key in uses.Keys)
                if (Regex.IsMatch(key, @"^F\d+$")) CollectionAssert.Contains(new[] { "F6", "F7", "F8", "F9" }, key);

            // 커서를 푸는 키(왼쪽 Alt)도 한 곳에서만 읽는다.
            Assert.AreEqual(1, Regex.Matches(source, @"leftAltKey").Count);
            Assert.AreEqual(0, Regex.Matches(source, @"rightAltKey|altKey\b").Count);

            // F6~F9 는 대시보드의 멤버 안에서만 나온다: 그 키 바로 앞에 선언된 멤버의 이름을 본다.
            MatchCollection members = Regex.Matches(source, @"public (?:bool|int|float|Vector2) (\w+)");
            foreach (Match key in Regex.Matches(source, @"Key\.F[6-9]\b"))
            {
                string owner = null;
                foreach (Match member in members)
                    if (member.Index < key.Index) owner = member.Groups[1].Value;
                Assert.IsNotNull(owner);
                StringAssert.StartsWith("Dashboard", owner, key.Value + " 를 대시보드가 아닌 멤버가 읽는다");
            }
        }

        [Test]
        public void OnlyTheInputLayerReadsTheKeyboard()
        {
            string root = ClientRoot();
            var offenders = new List<string>();
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string relative = file.Substring(root.Length + 1).Replace('\\', '/');
                if (relative.StartsWith("Input/", StringComparison.Ordinal)) continue;
                string text = File.ReadAllText(file);
                if (Regex.IsMatch(text, @"\bKeyboard\.current\b|\bKey\.F\d+\b|\bKeyCode\.")) offenders.Add(relative);
            }
            CollectionAssert.IsEmpty(offenders, "키보드는 Input/PlayerInput.cs 에서만 읽는다. 그래야 키가 겹치는지 한 곳에서 볼 수 있다.");
        }
    }
}
