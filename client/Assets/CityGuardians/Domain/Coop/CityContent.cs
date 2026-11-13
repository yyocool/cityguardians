using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace CityGuardians.Domain
{
    /// <summary>서버가 알려 준 도시 콘텐츠 한 묶음 (GET /cities/content 의 한 줄).</summary>
    [Serializable]
    public sealed class CityContentEntry
    {
        public string contentId;
        public string regionCode;
        /// <summary>landmark(상징물), street(상징거리), special_zone(특화 구역).</summary>
        public string kind;
        public string version;
        /// <summary>Addressables 키.</summary>
        public string addressKey;
        public string catalogUrl;
        /// <summary>카탈로그 파일의 SHA-256 (16진수 64자).</summary>
        public string contentHash;
        public long sizeBytes;
        public string minClientVersion;
    }

    /// <summary>
    /// 운영 중인 클라이언트가 새 지역 콘텐츠를 안전하게 받는 규칙 (docs/CITY_MODELING_PIPELINE.md 6장).
    ///
    /// 서버가 "이 계정이 받아도 되는 묶음"을 정해 준다 (배포 상태, 받는 비율, 최소 클라이언트 버전). 클라이언트는 그래도 다시 확인한다:
    /// 주소가 정해 둔 콘텐츠 저장소의 것인지, 내 버전으로 쓸 수 있는지, 받은 파일의 해시가 맞는지. 하나라도 맞지 않으면
    /// 그 묶음을 쓰지 않고 기본 도시를 보여 준다. 콘텐츠가 없어도 게임은 그대로 된다.
    /// </summary>
    public static class CityContent
    {
        public const string Landmark = "landmark";
        public const string Street = "street";
        public const string SpecialZone = "special_zone";

        /// <summary>"9.0.0" 같은 버전을 견준다. a 가 크면 양수. 형식이 틀리면 가장 낮은 것으로 본다.</summary>
        public static int CompareVersions(string a, string b)
        {
            int[] left = Parse(a), right = Parse(b);
            for (int i = 0; i < 4; i++)
                if (left[i] != right[i]) return left[i].CompareTo(right[i]);
            return 0;
        }

        private static int[] Parse(string version)
        {
            var parts = new int[4];
            if (string.IsNullOrEmpty(version)) return parts;
            string[] pieces = version.Split('.');
            if (pieces.Length > 4) return new int[4];
            for (int i = 0; i < pieces.Length; i++)
            {
                if (pieces[i].Length == 0 || pieces[i].Length > 6) return new int[4];
                foreach (char c in pieces[i])
                    if (c < '0' || c > '9') return new int[4];
                parts[i] = int.Parse(pieces[i]);
            }
            return parts;
        }

        private static bool IsHex64(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        /// <summary>
        /// 이 묶음을 써도 되는가. contentBase 는 클라이언트에 들어 있는 콘텐츠 저장소 주소다 (https 만).
        /// 서버가 내려 준 주소가 그 저장소 밖을 가리키면 쓰지 않는다.
        /// </summary>
        public static bool IsUsable(CityContentEntry entry, string clientVersion, string contentBase)
        {
            if (entry == null || string.IsNullOrEmpty(entry.regionCode) || string.IsNullOrEmpty(entry.addressKey)) return false;
            if (entry.kind != Landmark && entry.kind != Street && entry.kind != SpecialZone) return false;
            if (!IsHex64(entry.contentHash) || entry.sizeBytes <= 0) return false;
            if (string.IsNullOrEmpty(contentBase) || !contentBase.StartsWith("https://", StringComparison.Ordinal)) return false;
            string prefix = contentBase.TrimEnd('/') + "/";
            if (string.IsNullOrEmpty(entry.catalogUrl) || !entry.catalogUrl.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string path = entry.catalogUrl.Substring(prefix.Length);
            if (path.Length == 0 || path.Contains("..") || path.Contains("//") || path.Contains("\\") || path.Contains("?") || path.Contains("#")
                || path.Contains("@") || path.Contains("%")) return false;
            if (!string.IsNullOrEmpty(entry.minClientVersion) && CompareVersions(clientVersion, entry.minClientVersion) < 0) return false;
            return true;
        }

        /// <summary>그 도시의 그 종류 콘텐츠 가운데 쓸 수 있는 가장 새 판. 없으면 null (기본 도시를 쓴다).</summary>
        public static CityContentEntry Pick(IEnumerable<CityContentEntry> entries, string regionCode, string kind, string clientVersion,
            string contentBase)
        {
            CityContentEntry best = null;
            if (entries == null) return null;
            foreach (var entry in entries)
            {
                if (entry == null || entry.regionCode != regionCode || entry.kind != kind) continue;
                if (!IsUsable(entry, clientVersion, contentBase)) continue;
                if (best == null || CompareVersions(entry.version, best.version) > 0) best = entry;
            }
            return best;
        }

        /// <summary>받은 카탈로그 파일의 해시가 서버가 알려 준 값과 같은가. 다르면 쓰지 않는다.</summary>
        public static bool HashMatches(CityContentEntry entry, byte[] downloaded)
        {
            if (entry == null || downloaded == null || !IsHex64(entry.contentHash)) return false;
            if (entry.sizeBytes != downloaded.LongLength) return false;
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(downloaded);
                var hex = new StringBuilder(64);
                foreach (byte b in hash) hex.Append(b.ToString("x2"));
                return string.Equals(hex.ToString(), entry.contentHash, StringComparison.Ordinal);
            }
        }
    }
}
