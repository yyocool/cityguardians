using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using CityGuardians.Domain;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    /// <summary>
    /// QR 코드 만들기 (docs/COUPON_SYSTEM.md 4장). 두 가지로 확인한다.
    ///  1) 널리 쓰이는 다른 구현(Node 의 qrcode 1.5.4)이 같은 글과 같은 마스크로 만든 그림과 한 칸도 다르지 않다.
    ///     그 그림은 미리 만들어 여기에 적어 두었다 (작은 것은 그대로, 큰 것은 SHA-256 으로).
    ///  2) 만든 그림을 이 파일의 읽기 코드로 되읽으면 처음 글이 나온다 (마스크를 스스로 고른 경우 포함).
    /// </summary>
    public sealed class QrCodeTests
    {
        private static string Sha256(string[] rows)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", rows)));
                var builder = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) builder.Append(hash[i].ToString("x2"));
                return builder.ToString();
            }
        }

        private static QrCode Make(string text, int mask = -1)
        {
            Assert.IsTrue(QrCode.TryEncode(text, out QrCode code, mask), "만들 수 있어야 한다: " + text);
            return code;
        }

        // ── 다른 구현과 견주기 ──────────────────────────────────

        [Test]
        public void 한_글자는_1판이고_참조_그림과_같다()
        {
            string[] expected =
            {
                "#######...#...#######",
                "#.....#.###...#.....#",
                "#.###.#.##....#.###.#",
                "#.###.#.#.###.#.###.#",
                "#.###.#...#.#.#.###.#",
                "#.....#..###..#.....#",
                "#######.#.#.#.#######",
                "........##...........",
                "#.....#.##.#.##..###.",
                ".##....##.####.###..#",
                ".####.#.....#.##.....",
                ".#####.#..######.#.#.",
                "####..###############",
                "........#.#.#.....#..",
                "#######..###.#..####.",
                "#.....#...#...#...###",
                "#.###.#...##.#..###..",
                "#.###.#..#.#####.#...",
                "#.###.#....###.###.##",
                "#.....#...######.#...",
                "#######.###.#..#..##.",
            };
            QrCode code = Make("A", 5);
            Assert.AreEqual(1, code.Version);
            Assert.AreEqual(21, code.Size);
            Assert.AreEqual(5, code.Mask);
            CollectionAssert.AreEqual(expected, code.ToRows());
        }

        [Test]
        public void 주소가_없을_때의_쿠폰_글은_2판이고_참조_그림과_같다()
        {
            string[] expected =
            {
                "#######.##.###.##.#######",
                "#.....#.#.###.#.#.#.....#",
                "#.###.#..##.##.#..#.###.#",
                "#.###.#.##.#...#..#.###.#",
                "#.###.#..#.#.##.#.#.###.#",
                "#.....#....#..#...#.....#",
                "#######.#.#.#.#.#.#######",
                "........#...####.........",
                "#.##.###...#.##...#..#.##",
                "###......##.#....#...#.##",
                ".#..#.#..#..#.#..#.......",
                ".#.#....#....###..##.####",
                "##.#.####.#....#.##..#.##",
                ".#.###...###.#..##.##..#.",
                ".##.###.###.#.#......###.",
                "#..###..##..##.###.....#.",
                ".....##.#..#....######.#.",
                "........###.##..#...#.#..",
                "#######.#####..##.#.#.###",
                "#.....#.#...#..##...##.##",
                "#.###.#..#####..#######..",
                "#.###.#.#.#.###..##..#.##",
                "#.###.#.##.###.###.#####.",
                "#.....#..........#.##.#..",
                "#######.#.#.#.##..#.#.###",
            };
            QrCode code = Make("CGPASS:AB2CD-EF3GH", 3);
            Assert.AreEqual(2, code.Version);
            CollectionAssert.AreEqual(expected, code.ToRows());
        }

        // 글, 마스크, 판, 참조 구현이 만든 그림의 SHA-256 ('#'과 '.'의 줄을 \n 으로 이은 것)
        private static readonly object[] ReferenceCases =
        {
            new object[] { "https://cg.test/merchant/redeem?code=AB2CD-EF3GH", 0, 4,
                "c4f7c408d8d29085d9a1d72b1839d0676e7a6c549c66512dae6d5cf10e94206b" },
            new object[] { "https://www.dalfin.net/cityguardians/merchant/redeem?code=K7M2P-Q9XWZ", 1, 5,
                "3be5997c1eefc6a8daf49d68af56a9b4c431ec550963d8bd1d2c05cc7df5f6c4" },
            new object[] { "시티가디언즈 CITY PASS 쿠폰 확인", 2, 3,
                "4cc2bdacda38205909f26aff20d6739f62106870a7633e8bf304915dbedef0db" },
            new object[] { Repeat("x", 90, 90), 4, 6,
                "2ba2feee37651d867922452a8bce92872c762efeadd8e907aabec0ccf19c004b" },
            new object[] { Repeat("ab", 55, 110), 2, 7,
                "380b3f4ba6201d1cd91e385ea9312de4e3ab8511ef99cd0e0991a53d3709d15e" },
            new object[] { Repeat("0123456789abcdefghijklmnopqrstuvwxyz", 4, 130), 6, 8,
                "72268edbd014ed5384e0793a03a11a4b2d727a87bca6240d286e90803e25e3f8" },
            new object[] { Repeat("Zz9", 57, 170), 5, 9,
                "174d131446900bf2b20d8f692fe48760a4c31c8b9f480121f369ce64da41c77b" },
            new object[] { Repeat("The quick brown fox jumps over the lazy dog. ", 5, 213), 7, 10,
                "4fb5ebaca728124a0db88c3287896f747eb1990d0a54629488db4402dd1bda1c" },
        };

        private static string Repeat(string unit, int times, int length)
        {
            var builder = new StringBuilder(unit.Length * times);
            for (int i = 0; i < times; i++) builder.Append(unit);
            return builder.ToString().Substring(0, length);
        }

        [TestCaseSource(nameof(ReferenceCases))]
        public void 참조_구현과_한_칸도_다르지_않다(string text, int mask, int version, string sha256)
        {
            QrCode code = Make(text, mask);
            Assert.AreEqual(version, code.Version, "판");
            Assert.AreEqual(mask, code.Mask);
            Assert.AreEqual(sha256, Sha256(code.ToRows()));
        }

        // ── 되읽기 ──────────────────────────────────────────────

        private static readonly int[] EccPerBlock = { 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
        private static readonly int[] BlockCount = { 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };

        /// <summary>그림을 글로 되읽는다 (오류가 없는 그림만. 오류 정정은 하지 않는다).</summary>
        private static string Read(QrCode code)
        {
            int size = code.Size;
            int version = (size - 17) / 4;

            // 형식 정보
            int format = 0;
            for (int i = 0; i <= 5; i++) if (code.IsDark(8, i)) format |= 1 << i;
            if (code.IsDark(8, 7)) format |= 1 << 6;
            if (code.IsDark(8, 8)) format |= 1 << 7;
            if (code.IsDark(7, 8)) format |= 1 << 8;
            for (int i = 9; i < 15; i++) if (code.IsDark(14 - i, 8)) format |= 1 << i;
            int plain = format ^ 0x5412;
            int mask = (plain >> 10) & 7;
            Assert.AreEqual(0, plain >> 13, "오류 정정 수준은 M 이다");
            Assert.AreEqual(QrCode.FormatBits(mask), format, "형식 정보의 오류 정정 비트");
            // 다른 두 곳에 적힌 사본도 같아야 한다.
            int copy = 0;
            for (int i = 0; i < 8; i++) if (code.IsDark(size - 1 - i, 8)) copy |= 1 << i;
            for (int i = 8; i < 15; i++) if (code.IsDark(8, size - 15 + i)) copy |= 1 << i;
            Assert.AreEqual(format, copy, "형식 정보의 사본");

            // 자료 칸을 순서대로 읽는다.
            var bits = new List<bool>();
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? size - 1 - vert : vert;
                        if (code.IsFunction(x, y)) continue;
                        bits.Add(code.IsDark(x, y) ^ QrCode.MaskFlips(mask, x, y));
                    }
                }
            }
            int raw = bits.Count / 8;
            var codewords = new byte[raw];
            for (int i = 0; i < raw * 8; i++)
                if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));

            // 섞인 블록을 푼다.
            int blocks = BlockCount[version - 1];
            int shortData = raw / blocks - EccPerBlock[version - 1];
            int shortBlocks = blocks - raw % blocks;
            var perBlock = new List<byte>[blocks];
            for (int j = 0; j < blocks; j++) perBlock[j] = new List<byte>();
            int at = 0;
            for (int i = 0; i <= shortData; i++)
                for (int j = 0; j < blocks; j++)
                    if (i < shortData || j >= shortBlocks) perBlock[j].Add(codewords[at++]);
            var data = new List<byte>();
            for (int j = 0; j < blocks; j++) data.AddRange(perBlock[j]);

            // 바이트 모드의 글
            int pos = 0;
            Func<int, int> take = length =>
            {
                int value = 0;
                for (int i = 0; i < length; i++, pos++)
                    value = (value << 1) | ((data[pos >> 3] >> (7 - (pos & 7))) & 1);
                return value;
            };
            Assert.AreEqual(4, take(4), "바이트 모드");
            int count = take(version <= 9 ? 8 : 16);
            var bytes = new byte[count];
            for (int i = 0; i < count; i++) bytes[i] = (byte)take(8);
            return Encoding.UTF8.GetString(bytes);
        }

        [Test]
        public void 만든_그림을_되읽으면_처음_글이_나온다()
        {
            string[] texts =
            {
                "A",
                "CGPASS:K7M2P-Q9XWZ",
                "https://cg.test/merchant/redeem?code=AB2CD-EF3GH",
                "https://www.dalfin.net/cityguardians/merchant/redeem?code=K7M2P-Q9XWZ",
                "시티가디언즈 CITY PASS 쿠폰 확인",
                Repeat("ab", 55, 110),
                Repeat("Zz9", 57, 170),
                Repeat("The quick brown fox jumps over the lazy dog. ", 5, 213),
            };
            foreach (string text in texts)
            {
                QrCode auto = Make(text);
                Assert.AreEqual(text, Read(auto), "스스로 고른 마스크 " + auto.Mask);
                for (int mask = 0; mask < 8; mask++) Assert.AreEqual(text, Read(Make(text, mask)), "마스크 " + mask);
            }
        }

        [Test]
        public void 길이마다_들어가는_가장_작은_판을_고른다()
        {
            // 바이트 모드, 오류 정정 수준 M 의 용량 (규격의 표)
            int[] capacity = { 14, 26, 42, 62, 84, 106, 122, 152, 180, 213 };
            for (int version = 1; version <= 10; version++)
            {
                int bytes = capacity[version - 1];
                Assert.AreEqual(bytes, QrCode.CapacityBytes(version), version + "판의 용량");
                Assert.AreEqual(version, QrCode.VersionFor(bytes));
                Assert.AreEqual(version < 10 ? version + 1 : 0, QrCode.VersionFor(bytes + 1));

                string text = new string('k', bytes);
                QrCode code = Make(text);
                Assert.AreEqual(version, code.Version);
                Assert.AreEqual(17 + 4 * version, code.Size);
                Assert.AreEqual(text, Read(code), version + "판을 가득 채운 글");
            }
            Assert.AreEqual(QrCode.MaxBytes, QrCode.CapacityBytes(QrCode.MaxVersion));
        }

        [Test]
        public void 한글은_UTF8_바이트_수로_센다()
        {
            // 한글 한 글자는 3바이트다. 71자 = 213바이트는 들어가고, 72자는 들어가지 않는다.
            Assert.IsTrue(QrCode.TryEncode(new string('가', 71), out QrCode code));
            Assert.AreEqual(10, code.Version);
            Assert.AreEqual(new string('가', 71), Read(code));
            Assert.IsFalse(QrCode.TryEncode(new string('가', 72), out _));
        }

        [Test]
        public void 담을_수_없는_글은_만들지_않는다()
        {
            Assert.IsFalse(QrCode.TryEncode(null, out QrCode none));
            Assert.IsNull(none);
            Assert.IsFalse(QrCode.TryEncode("", out _));
            Assert.IsFalse(QrCode.TryEncode(new string('a', QrCode.MaxBytes + 1), out _));
            Assert.IsFalse(QrCode.TryEncode("A", out _, 8), "없는 마스크");
            Assert.IsFalse(QrCode.TryEncode("A", out _, -2));
            Assert.Throws<ArgumentException>(() => QrCode.Encode(""));
            Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('a', 300)));
            Assert.AreEqual(1, QrCode.Encode("A").Version);
        }

        // ── 모양 ────────────────────────────────────────────────

        [Test]
        public void 정해진_무늬가_제자리에_있다()
        {
            foreach (string text in new[] { "A", "https://cg.test/merchant/redeem?code=AB2CD-EF3GH", new string('q', 200) })
            {
                QrCode code = Make(text);
                int size = code.Size;

                // 위치 찾기 무늬 셋: 7×7 테두리, 밝은 띠, 3×3 가운데
                foreach (int[] corner in new[] { new[] { 0, 0 }, new[] { size - 7, 0 }, new[] { 0, size - 7 } })
                {
                    for (int dy = 0; dy < 7; dy++)
                    {
                        for (int dx = 0; dx < 7; dx++)
                        {
                            int ring = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));
                            Assert.AreEqual(ring != 2, code.IsDark(corner[0] + dx, corner[1] + dy), $"위치 찾기 무늬 ({dx},{dy})");
                            Assert.IsTrue(code.IsFunction(corner[0] + dx, corner[1] + dy));
                        }
                    }
                }
                // 위치 찾기 무늬를 두르는 밝은 띠
                for (int i = 0; i < 8; i++)
                {
                    Assert.IsFalse(code.IsDark(7, i));
                    Assert.IsFalse(code.IsDark(i, 7));
                    Assert.IsFalse(code.IsDark(size - 8, i));
                    Assert.IsFalse(code.IsDark(i, size - 8));
                }
                // 타이밍 무늬: 번갈아 어둡고 밝다
                for (int i = 8; i < size - 8; i++)
                {
                    Assert.AreEqual(i % 2 == 0, code.IsDark(i, 6), "가로 타이밍 " + i);
                    Assert.AreEqual(i % 2 == 0, code.IsDark(6, i), "세로 타이밍 " + i);
                }
                // 늘 어두운 칸
                Assert.IsTrue(code.IsDark(8, size - 8));
                // 그림 밖(조용한 영역)은 밝다
                Assert.IsFalse(code.IsDark(-1, 0));
                Assert.IsFalse(code.IsDark(0, -1));
                Assert.IsFalse(code.IsDark(size, 0));
                Assert.IsFalse(code.IsDark(0, size));
                Assert.IsFalse(code.IsFunction(-1, -1));

                string[] rows = code.ToRows();
                Assert.AreEqual(size, rows.Length);
                for (int y = 0; y < size; y++)
                {
                    Assert.AreEqual(size, rows[y].Length);
                    for (int x = 0; x < size; x++) Assert.AreEqual(code.IsDark(x, y), rows[y][x] == '#');
                }
            }
        }

        [Test]
        public void 맞춤_무늬의_자리는_규격의_표와_같다()
        {
            CollectionAssert.AreEqual(new int[0], QrCode.AlignmentPositions(1));
            CollectionAssert.AreEqual(new[] { 6, 18 }, QrCode.AlignmentPositions(2));
            CollectionAssert.AreEqual(new[] { 6, 22 }, QrCode.AlignmentPositions(3));
            CollectionAssert.AreEqual(new[] { 6, 26 }, QrCode.AlignmentPositions(4));
            CollectionAssert.AreEqual(new[] { 6, 30 }, QrCode.AlignmentPositions(5));
            CollectionAssert.AreEqual(new[] { 6, 34 }, QrCode.AlignmentPositions(6));
            CollectionAssert.AreEqual(new[] { 6, 22, 38 }, QrCode.AlignmentPositions(7));
            CollectionAssert.AreEqual(new[] { 6, 24, 42 }, QrCode.AlignmentPositions(8));
            CollectionAssert.AreEqual(new[] { 6, 26, 46 }, QrCode.AlignmentPositions(9));
            CollectionAssert.AreEqual(new[] { 6, 28, 50 }, QrCode.AlignmentPositions(10));

            // 2판의 맞춤 무늬 하나: 가운데 (18, 18), 5×5
            QrCode code = Make("CGPASS:AB2CD-EF3GH");
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    Assert.AreEqual(Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1, code.IsDark(18 + dx, 18 + dy));
        }

        [Test]
        public void 형식과_판_정보의_값은_규격의_표와_같다()
        {
            // 수준 M 의 형식 정보 (마스크 0~7)
            int[] format = { 0x5412, 0x5125, 0x5E7C, 0x5B4B, 0x45F9, 0x40CE, 0x4F97, 0x4AA0 };
            for (int mask = 0; mask < 8; mask++) Assert.AreEqual(format[mask], QrCode.FormatBits(mask), "마스크 " + mask);
            // 판 정보 (7~10판)
            Assert.AreEqual(0x07C94, QrCode.VersionBits(7));
            Assert.AreEqual(0x085BC, QrCode.VersionBits(8));
            Assert.AreEqual(0x09A99, QrCode.VersionBits(9));
            Assert.AreEqual(0x0A4D3, QrCode.VersionBits(10));
        }

        [Test]
        public void 같은_글은_언제나_같은_그림이_된다()
        {
            string text = "https://cg.test/merchant/redeem?code=AB2CD-EF3GH";
            QrCode a = Make(text);
            QrCode b = Make(text);
            Assert.AreEqual(a.Mask, b.Mask);
            CollectionAssert.AreEqual(a.ToRows(), b.ToRows());
            // 코드가 한 글자만 달라도 그림이 다르다.
            CollectionAssert.AreNotEqual(a.ToRows(), Make(text.Replace("AB2CD", "AB2CE"), a.Mask).ToRows());
        }
    }
}
