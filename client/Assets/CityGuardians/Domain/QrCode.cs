using System;
using System.Collections.Generic;
using System.Text;

namespace CityGuardians.Domain
{
    /// <summary>
    /// QR 코드 만들기 (ISO/IEC 18004, 모델 2). CITY PASS 쿠폰을 매장에 보여 줄 때 쓴다 (docs/COUPON_SYSTEM.md 4장).
    ///
    /// 바깥 라이브러리 없이 필요한 만큼만 만들었다: 바이트 모드, 오류 정정 수준 M(약 15%), 1~10판(UTF-8 213바이트까지).
    /// 쿠폰의 QR 에 들어가는 글은 "https://…/merchant/redeem?code=XXXXX-XXXXX" 정도라서 4~5판이면 된다.
    /// QR 에 무엇을 담을지는 서버가 정한다 (일회용 코드뿐이고 개인정보는 없다). 이 코드는 받은 글을 그림으로 바꾸기만 한다.
    /// </summary>
    public sealed class QrCode
    {
        public const int MinVersion = 1;
        public const int MaxVersion = 10;
        /// <summary>담을 수 있는 가장 긴 글 (UTF-8 바이트 수).</summary>
        public const int MaxBytes = 213;
        /// <summary>코드 둘레에 비워 두어야 하는 칸 수 (조용한 영역).</summary>
        public const int QuietZone = 4;

        // 오류 정정 수준 M 의 판별 구성: 블록 하나의 오류 정정 코드워드 수, 블록 수.
        private static readonly int[] EccPerBlock = { 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
        private static readonly int[] BlockCount = { 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };

        /// <summary>판 (1~10). 한 변은 17 + 4 × 판.</summary>
        public int Version { get; }
        /// <summary>한 변의 칸 수 (조용한 영역 제외).</summary>
        public int Size { get; }
        /// <summary>고른 마스크 무늬 (0~7).</summary>
        public int Mask { get; }

        private readonly bool[] _dark;
        private readonly bool[] _function;

        private QrCode(int version, byte[] dataCodewords, int forcedMask)
        {
            Version = version;
            Size = SizeOf(version);
            _dark = new bool[Size * Size];
            _function = new bool[Size * Size];

            DrawFunctionPatterns();
            DrawCodewords(AddEccAndInterleave(version, dataCodewords));

            // 여덟 마스크를 모두 입혀 보고 벌점이 가장 낮은 것을 고른다.
            int best = forcedMask;
            int bestPenalty = int.MaxValue;
            for (int mask = 0; mask < 8 && forcedMask < 0; mask++)
            {
                ApplyMask(mask);
                DrawFormatBits(mask);
                int penalty = Penalty();
                if (penalty < bestPenalty)
                {
                    best = mask;
                    bestPenalty = penalty;
                }
                ApplyMask(mask);   // 같은 마스크를 한 번 더 입히면 원래대로 돌아온다
            }
            Mask = best;
            ApplyMask(best);
            DrawFormatBits(best);
        }

        public static int SizeOf(int version) => 17 + 4 * version;

        /// <summary>그 칸이 어두운가. 범위 밖(조용한 영역)은 밝다.</summary>
        public bool IsDark(int x, int y) => x >= 0 && y >= 0 && x < Size && y < Size && _dark[y * Size + x];

        /// <summary>그 칸이 정해진 무늬(위치 찾기, 타이밍, 형식 정보 등)인가. 테스트에서 쓴다.</summary>
        public bool IsFunction(int x, int y) => x >= 0 && y >= 0 && x < Size && y < Size && _function[y * Size + x];

        /// <summary>줄마다 '#'(어두움)과 '.'(밝음)으로 적은 그림. 조용한 영역은 넣지 않는다.</summary>
        public string[] ToRows()
        {
            var rows = new string[Size];
            var builder = new StringBuilder(Size);
            for (int y = 0; y < Size; y++)
            {
                builder.Length = 0;
                for (int x = 0; x < Size; x++) builder.Append(_dark[y * Size + x] ? '#' : '.');
                rows[y] = builder.ToString();
            }
            return rows;
        }

        /// <summary>글을 QR 코드로 만든다. 비어 있거나 너무 길면 ArgumentException.</summary>
        public static QrCode Encode(string text)
        {
            if (!TryEncode(text, out QrCode code))
                throw new ArgumentException($"QR 코드에 담을 수 없는 글입니다 (비어 있거나 {MaxBytes}바이트를 넘습니다).", nameof(text));
            return code;
        }

        /// <param name="mask">
        /// 마스크 무늬 (0~7). -1 이면 가장 읽기 좋은 것을 고른다. 어느 마스크든 읽는 데는 문제가 없다;
        /// 정해 줄 수 있게 한 것은 다른 구현이 만든 그림과 한 칸씩 견주어 보기 위해서다 (테스트).
        /// </param>
        public static bool TryEncode(string text, out QrCode code, int mask = -1)
        {
            code = null;
            if (string.IsNullOrEmpty(text) || mask < -1 || mask > 7) return false;
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            int version = VersionFor(bytes.Length);
            if (version == 0) return false;
            code = new QrCode(version, DataCodewords(version, bytes), mask);
            return true;
        }

        /// <summary>그 길이의 글이 들어가는 가장 작은 판. 들어가지 않으면 0.</summary>
        public static int VersionFor(int byteCount)
        {
            if (byteCount <= 0) return 0;
            for (int version = MinVersion; version <= MaxVersion; version++)
                if (byteCount <= CapacityBytes(version)) return version;
            return 0;
        }

        /// <summary>그 판에 담을 수 있는 바이트 수 (바이트 모드, 수준 M).</summary>
        public static int CapacityBytes(int version)
        {
            int bits = DataCodewordCount(version) * 8 - 4 - CountBits(version);
            return bits / 8;
        }

        // ── 자료 ────────────────────────────────────────────────

        private static int CountBits(int version) => version <= 9 ? 8 : 16;

        /// <summary>자료와 오류 정정 코드워드를 담는 칸의 수 (정해진 무늬를 뺀 나머지).</summary>
        private static int RawDataModules(int version)
        {
            int result = (16 * version + 128) * version + 64;
            if (version >= 2)
            {
                int align = version / 7 + 2;
                result -= (25 * align - 10) * align - 55;
                if (version >= 7) result -= 36;
            }
            return result;
        }

        private static int DataCodewordCount(int version) =>
            RawDataModules(version) / 8 - EccPerBlock[version - 1] * BlockCount[version - 1];

        private static byte[] DataCodewords(int version, byte[] bytes)
        {
            int capacityBits = DataCodewordCount(version) * 8;
            var bits = new List<bool>(capacityBits);
            AppendBits(bits, 0x4, 4);                         // 바이트 모드
            AppendBits(bits, bytes.Length, CountBits(version));
            for (int i = 0; i < bytes.Length; i++) AppendBits(bits, bytes[i], 8);

            AppendBits(bits, 0, Math.Min(4, capacityBits - bits.Count));   // 끝 표시
            AppendBits(bits, 0, (8 - bits.Count % 8) % 8);                 // 바이트 경계까지
            for (int pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11) AppendBits(bits, pad, 8);

            var result = new byte[capacityBits / 8];
            for (int i = 0; i < bits.Count; i++)
                if (bits[i]) result[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            return result;
        }

        private static void AppendBits(List<bool> bits, int value, int length)
        {
            for (int i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }

        // ── 오류 정정 (리드-솔로몬, GF(2^8), 0x11D) ─────────────

        private static byte[] AddEccAndInterleave(int version, byte[] data)
        {
            int blocks = BlockCount[version - 1];
            int eccLength = EccPerBlock[version - 1];
            int raw = RawDataModules(version) / 8;
            int shortBlocks = blocks - raw % blocks;
            int shortLength = raw / blocks;

            var all = new byte[blocks][];
            byte[] divisor = RsDivisor(eccLength);
            int offset = 0;
            for (int i = 0; i < blocks; i++)
            {
                int dataLength = shortLength - eccLength + (i < shortBlocks ? 0 : 1);
                var block = new byte[shortLength + 1];
                Array.Copy(data, offset, block, 0, dataLength);
                offset += dataLength;
                byte[] ecc = RsRemainder(block, dataLength, divisor);
                // 짧은 블록은 자료 끝에 빈 자리 하나를 둔다 (섞을 때 건너뛴다).
                Array.Copy(ecc, 0, block, block.Length - eccLength, eccLength);
                all[i] = block;
            }

            var result = new byte[raw];
            int at = 0;
            for (int i = 0; i <= shortLength; i++)
            {
                for (int j = 0; j < blocks; j++)
                {
                    if (i != shortLength - eccLength || j >= shortBlocks) result[at++] = all[j][i];
                }
            }
            return result;
        }

        private static byte[] RsDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = GfMultiply(result[j], root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = GfMultiply(root, 0x02);
            }
            return result;
        }

        private static byte[] RsRemainder(byte[] data, int length, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            for (int n = 0; n < length; n++)
            {
                int factor = data[n] ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < result.Length; i++) result[i] ^= GfMultiply(divisor[i], factor);
            }
            return result;
        }

        private static byte GfMultiply(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        // ── 정해진 무늬 ─────────────────────────────────────────

        private void SetFunction(int x, int y, bool dark)
        {
            _dark[y * Size + x] = dark;
            _function[y * Size + x] = true;
        }

        private void DrawFunctionPatterns()
        {
            // 타이밍 무늬
            for (int i = 0; i < Size; i++)
            {
                SetFunction(6, i, i % 2 == 0);
                SetFunction(i, 6, i % 2 == 0);
            }
            // 위치 찾기 무늬 셋 (둘레의 밝은 띠까지)
            DrawFinder(3, 3);
            DrawFinder(Size - 4, 3);
            DrawFinder(3, Size - 4);
            // 맞춤 무늬
            int[] positions = AlignmentPositions(Version);
            int last = positions.Length - 1;
            for (int i = 0; i <= last; i++)
            {
                for (int j = 0; j <= last; j++)
                {
                    // 위치 찾기 무늬와 겹치는 세 귀퉁이는 뺀다.
                    if ((i == 0 && j == 0) || (i == 0 && j == last) || (i == last && j == 0)) continue;
                    DrawAlignment(positions[i], positions[j]);
                }
            }
            DrawFormatBits(0);   // 자리만 잡아 둔다. 마스크를 고른 뒤에 다시 그린다.
            DrawVersion();
        }

        private void DrawFinder(int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx;
                    int y = cy + dy;
                    if (x < 0 || x >= Size || y < 0 || y >= Size) continue;
                    int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    SetFunction(x, y, distance != 2 && distance != 4);
                }
            }
        }

        private void DrawAlignment(int cx, int cy)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    SetFunction(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        /// <summary>맞춤 무늬의 가운데 좌표들. 1판에는 없다.</summary>
        public static int[] AlignmentPositions(int version)
        {
            if (version == 1) return new int[0];
            int count = version / 7 + 2;
            int step = (version * 4 + count * 2 + 1) / (count * 2 - 2) * 2;
            var result = new int[count];
            result[0] = 6;
            int position = SizeOf(version) - 7;
            for (int i = count - 1; i >= 1; i--, position -= step) result[i] = position;
            return result;
        }

        /// <summary>형식 정보 15비트: 오류 정정 수준(M = 00)과 마스크 번호, BCH(15,5) 오류 정정, 0x5412 로 가림.</summary>
        public static int FormatBits(int mask)
        {
            int data = mask;   // 수준 M 의 표시는 00 이다
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            return ((data << 10) | rem) ^ 0x5412;
        }

        private void DrawFormatBits(int mask)
        {
            int bits = FormatBits(mask);
            // 왼쪽 위 위치 찾기 무늬 둘레
            for (int i = 0; i <= 5; i++) SetFunction(8, i, Bit(bits, i));
            SetFunction(8, 7, Bit(bits, 6));
            SetFunction(8, 8, Bit(bits, 7));
            SetFunction(7, 8, Bit(bits, 8));
            for (int i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(bits, i));
            // 오른쪽 위와 왼쪽 아래에 한 번 더
            for (int i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(bits, i));
            for (int i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(bits, i));
            SetFunction(8, Size - 8, true);   // 늘 어두운 칸
        }

        /// <summary>판 정보 18비트 (7판부터). BCH(18,6).</summary>
        public static int VersionBits(int version)
        {
            int rem = version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            return (version << 12) | rem;
        }

        private void DrawVersion()
        {
            if (Version < 7) return;
            int bits = VersionBits(Version);
            for (int i = 0; i < 18; i++)
            {
                bool bit = Bit(bits, i);
                int a = Size - 11 + i % 3;
                int b = i / 3;
                SetFunction(a, b, bit);
                SetFunction(b, a, bit);
            }
        }

        private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;

        // ── 자료 칸 채우기와 마스크 ─────────────────────────────

        private void DrawCodewords(byte[] codewords)
        {
            int i = 0;
            int total = codewords.Length * 8;
            // 오른쪽 아래에서 시작해 두 칸 너비의 띠를 위아래로 오가며 채운다. 세로 타이밍 무늬(x = 6)는 건너뛴다.
            for (int right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < Size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? Size - 1 - vert : vert;
                        if (_function[y * Size + x] || i >= total) continue;
                        _dark[y * Size + x] = ((codewords[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                }
            }
        }

        /// <summary>마스크 무늬가 그 칸을 뒤집는가.</summary>
        public static bool MaskFlips(int mask, int x, int y)
        {
            switch (mask)
            {
                case 0: return (x + y) % 2 == 0;
                case 1: return y % 2 == 0;
                case 2: return x % 3 == 0;
                case 3: return (x + y) % 3 == 0;
                case 4: return (x / 3 + y / 2) % 2 == 0;
                case 5: return x * y % 2 + x * y % 3 == 0;
                case 6: return (x * y % 2 + x * y % 3) % 2 == 0;
                case 7: return ((x + y) % 2 + x * y % 3) % 2 == 0;
                default: throw new ArgumentOutOfRangeException(nameof(mask));
            }
        }

        private void ApplyMask(int mask)
        {
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int at = y * Size + x;
                    if (!_function[at] && MaskFlips(mask, x, y)) _dark[at] = !_dark[at];
                }
            }
        }

        /// <summary>읽기 어려운 무늬에 매기는 벌점 (규격의 네 가지 규칙). 낮을수록 좋다.</summary>
        private int Penalty()
        {
            int result = 0;
            int dark = 0;
            var line = new bool[Size];

            for (int pass = 0; pass < 2; pass++)
            {
                for (int a = 0; a < Size; a++)
                {
                    for (int b = 0; b < Size; b++) line[b] = pass == 0 ? _dark[a * Size + b] : _dark[b * Size + a];
                    result += LinePenalty(line);
                }
            }
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    bool color = _dark[y * Size + x];
                    if (color) dark++;
                    // 규칙 2: 같은 색의 2×2 덩어리
                    if (x + 1 < Size && y + 1 < Size && color == _dark[y * Size + x + 1]
                        && color == _dark[(y + 1) * Size + x] && color == _dark[(y + 1) * Size + x + 1]) result += 3;
                }
            }
            // 규칙 4: 어두운 칸의 비율이 절반에서 멀수록
            int total = Size * Size;
            int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            return result + Math.Max(0, k) * 10;
        }

        private static readonly bool[] FinderLike = { true, false, true, true, true, false, true };

        private static int LinePenalty(bool[] line)
        {
            int result = 0;
            int run = 1;
            for (int i = 1; i <= line.Length; i++)
            {
                if (i < line.Length && line[i] == line[i - 1])
                {
                    run++;
                    continue;
                }
                // 규칙 1: 같은 색이 다섯 칸 넘게 이어짐
                if (run >= 5) result += 3 + (run - 5);
                run = 1;
            }
            // 규칙 3: 위치 찾기 무늬처럼 보이는 1:1:3:1:1 과 그 앞이나 뒤의 밝은 네 칸
            for (int i = 0; i + 7 <= line.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < 7 && match; j++) match = line[i + j] == FinderLike[j];
                if (!match) continue;
                if (LightRun(line, i - 4, 4) || LightRun(line, i + 7, 4)) result += 40;
            }
            return result;
        }

        /// <summary>그 자리부터 count 칸이 모두 밝은가. 줄 밖은 밝은 것으로 본다 (조용한 영역).</summary>
        private static bool LightRun(bool[] line, int start, int count)
        {
            for (int i = start; i < start + count; i++)
                if (i >= 0 && i < line.Length && line[i]) return false;
            return true;
        }
    }
}
