using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AdofaiModHelper.Core
{
    public sealed class CatalogMod
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Author { get; set; }
        public string Repo { get; set; }
        public Regex Asset { get; set; }
        public bool AllowPrerelease { get; set; }
        public bool Library { get; set; }
        public string[] Requires { get; set; } = new string[0];
        public string DescriptionKo { get; set; }
        public string DescriptionEn { get; set; }

        // ResolveAsync가 채운다
        public string LatestVersion { get; set; }
        public string DownloadUrl { get; set; }
        // GitHub이 자산마다 제공하는 "sha256:..." (없는 오래된 릴리스도 있다)
        public string Digest { get; set; }
    }

    public static class Catalog
    {
        public const string Owner = "Newmouse11";
        public const string RepoName = "adofai-mod-helper";
        // 목록은 GitHub 저장소의 파일 하나: 이 JSON만 고치면 도구 재배포 없이 반영된다
        public const string RemoteUrl = "https://raw.githubusercontent.com/" + Owner + "/" + RepoName + "/main/catalog/mods.json";

        // GitHub 규칙: 소유자는 영숫자로 시작(영숫자·하이픈), 저장소 이름은 "."/".."일 수 없다 → API 경로 조작 차단
        static readonly Regex RepoPattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9-]*/(?!\.\.?$)[A-Za-z0-9_.-]+$");

        // 원격 목록 → 실패하면 exe에 내장된 목록 (저장소를 아직 안 만들었거나 오프라인일 때)
        public static async Task<List<CatalogMod>> LoadAsync(CancellationToken ct)
        {
            try
            {
                return Parse(await Http.GetStringAsync(RemoteUrl, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && (ex is System.Net.Http.HttpRequestException
                                       || ex is TaskCanceledException || ex is ArgumentException || ex is InvalidDataException))
            {
                // 저장소를 아직 안 만들었으면 매번 404라 스택 없이 한 줄만 남긴다
                Log.Warn("원격 모드 목록을 못 받아 내장 목록 사용: " + ex.Message);
                using (var stream = typeof(Catalog).Assembly.GetManifestResourceStream("mods.json"))
                using (var reader = new StreamReader(stream))
                    return Parse(reader.ReadToEnd());
            }
        }

        // 외부 입력이므로 항목 단위로 검증하고, 잘못된 항목은 건너뛴다
        public static List<CatalogMod> Parse(string json)
        {
            var mods = Json.Get(Json.Parse(json), "mods") as object[]
                ?? throw new InvalidDataException("mods.json에 mods 배열이 없음");
            var list = new List<CatalogMod>();
            foreach (var m in mods)
            {
                var id = Json.Str(m, "id");
                var repo = Json.Str(m, "repo");
                var asset = Json.Str(m, "asset");
                if (string.IsNullOrWhiteSpace(id) || repo == null || !RepoPattern.IsMatch(repo) || string.IsNullOrWhiteSpace(asset))
                {
                    Log.Warn("모드 목록 항목 무시 (필수값 누락/형식 오류): " + (id ?? "?"));
                    continue;
                }
                Regex assetRegex;
                try { assetRegex = new Regex(asset, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException ex)
                {
                    Log.Warn("모드 목록 항목 무시 (asset 정규식 오류): " + id, ex);
                    continue;
                }
                var desc = Json.Get(m, "description");
                list.Add(new CatalogMod
                {
                    Id = id,
                    Name = Json.Str(m, "name") ?? id,
                    Author = Json.Str(m, "author") ?? "",
                    Repo = repo,
                    Asset = assetRegex,
                    AllowPrerelease = Json.Bool(m, "prerelease"),
                    Library = Json.Bool(m, "library"),
                    Requires = Json.StrArray(m, "requires"),
                    DescriptionKo = Json.Str(desc, "ko") ?? "",
                    DescriptionEn = Json.Str(desc, "en") ?? "",
                });
            }
            return list;
        }

        // 최신 릴리스 중 asset 패턴에 맞는 zip을 찾는다. /releases/latest는 prerelease만 있는 저장소(JALib)에서 404라 목록을 본다
        public static async Task ResolveAsync(CatalogMod mod, CancellationToken ct)
        {
            var json = await Http.GetGitHubJsonAsync($"https://api.github.com/repos/{mod.Repo}/releases?per_page=15", ct).ConfigureAwait(false);
            foreach (var release in Json.Parse(json) as object[] ?? new object[0])
            {
                if (Json.Bool(release, "draft") || (Json.Bool(release, "prerelease") && !mod.AllowPrerelease)) continue;
                var asset = (Json.Get(release, "assets") as object[] ?? new object[0])
                    .FirstOrDefault(a => mod.Asset.IsMatch(Json.Str(a, "name") ?? ""));
                var url = Json.Str(asset, "browser_download_url");
                if (url == null) continue;
                // GitHub 릴리스 주소가 아닌 곳에서는 받지 않는다
                if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) continue;
                var tag = Json.Str(release, "tag_name");
                mod.LatestVersion = Versions.Extract(tag) ?? Versions.Extract(Json.Str(release, "name")) ?? tag;
                mod.DownloadUrl = url;
                mod.Digest = Json.Str(asset, "digest");
                return;
            }
            throw new InvalidDataException($"{mod.Repo}에서 조건에 맞는 릴리스 파일을 찾지 못함");
        }

        // 고른 모드 + 아직 없는 필수 라이브러리를 의존 순서대로 펼친다 (JALib → JipperResourcePack)
        public static List<CatalogMod> WithDependencies(IEnumerable<CatalogMod> selected, IList<CatalogMod> catalog, ISet<string> installedIds)
        {
            var order = new List<CatalogMod>();
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(CatalogMod mod)
            {
                if (order.Contains(mod) || !visiting.Add(mod.Id)) return; // 순환 의존은 한 번만
                foreach (var dep in mod.Requires)
                {
                    var depMod = catalog.FirstOrDefault(c => string.Equals(c.Id, dep, StringComparison.OrdinalIgnoreCase));
                    if (depMod != null && !installedIds.Contains(depMod.Id)) Visit(depMod);
                }
                order.Add(mod);
            }
            foreach (var mod in selected) Visit(mod);
            return order;
        }

        // 다운로드가 깨졌거나 바꿔치기됐으면 설치하지 않는다
        static void VerifyDigest(string file, CatalogMod mod)
        {
            const string prefix = "sha256:";
            if (mod.Digest == null || !mod.Digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
            string actual;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(file))
                actual = string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
            if (!string.Equals(actual, mod.Digest.Substring(prefix.Length), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{mod.Name} 파일의 SHA-256이 GitHub 값과 다름 (다운로드 손상 가능)");
        }

        // 한 모드를 받아 설치. 설치 후 실제 Info.json Id가 목록 Id와 다르면 기록해 둔다 (목록 수정 필요 신호)
        public static async Task<InstalledMod> InstallAsync(Game game, CatalogMod mod, IProgress<double> progress, CancellationToken ct, bool clean = false)
        {
            if (mod.DownloadUrl == null) await ResolveAsync(mod, ct).ConfigureAwait(false);
            var temp = Files.NewTempDir();
            try
            {
                var zip = Path.Combine(temp, "mod.zip");
                await Http.DownloadAsync(mod.DownloadUrl, zip, progress, ct).ConfigureAwait(false);
                VerifyDigest(zip, mod);
                var installed = Mods.InstallZip(game, zip, clean);
                if (!string.Equals(installed.Id, mod.Id, StringComparison.OrdinalIgnoreCase))
                    Log.Warn($"목록 Id({mod.Id})와 실제 Info.json Id({installed.Id})가 다름: {mod.Repo}");
                return installed;
            }
            finally
            {
                Files.TryDeleteTemp(temp);
            }
        }
    }

    public static class SelfUpdate
    {
        // 새 버전이 있으면 (버전, 릴리스 페이지 주소), 없으면 null
        public static async Task<Tuple<string, string>> CheckAsync(CancellationToken ct)
        {
            var json = await Http.GetGitHubJsonAsync(
                $"https://api.github.com/repos/{Catalog.Owner}/{Catalog.RepoName}/releases/latest", ct).ConfigureAwait(false);
            var release = Json.Parse(json);
            var latest = Versions.Extract(Json.Str(release, "tag_name"));
            var page = Json.Str(release, "html_url");
            if (latest == null || page == null || !page.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return null;
            return Versions.Compare(latest, AppInfo.Version) > 0 ? Tuple.Create(latest, page) : null;
        }
    }
}
