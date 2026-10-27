using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AdofaiModHelper.Core
{
    public sealed class InstalledMod
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Author { get; set; }
        public string Version { get; set; }
        public string Folder { get; set; }
        public string[] Requirements { get; set; } = new string[0];
        // UMM이 불러오는 모드 dll의 전체 경로 (dll 없는 콘텐츠 모드는 null)
        public string Dll { get; set; }
        public bool Enabled { get; set; } = true;
    }

    public enum ProblemKind { NestedFolder, DuplicateId, ZipNotExtracted, BrokenInfo, MissingRequirement }

    public sealed class ModProblem
    {
        public ProblemKind Kind { get; set; }
        public string Path { get; set; }
        public string ModId { get; set; }
        public bool AutoFixable => Kind == ProblemKind.NestedFolder || Kind == ProblemKind.DuplicateId || Kind == ProblemKind.ZipNotExtracted;
    }

    public sealed class ScanResult
    {
        public List<InstalledMod> Mods { get; } = new List<InstalledMod>();
        public List<ModProblem> Problems { get; } = new List<ModProblem>();
    }

    // UMM 런타임 규칙(ModManager.cs)에 맞춘 Mods 폴더 관리: 바로 아래 폴더만 읽고, 같은 Id는 나중 것을 무시한다
    public static class Mods
    {
        const string InfoFile = "Info.json"; // 윈도우 파일시스템은 대소문자 무시 → info.json도 잡힌다
        public const string DisabledSuffix = ".disabled";
        static readonly Regex RequirementVersion = new Regex(@"^(.*)-(\d+\.\d+\.\d+).*$");

        public static ScanResult Scan(Game game)
        {
            var result = new ScanResult();
            if (!Directory.Exists(game.ModsDir)) return result;
            var enabled = ReadEnabledStates(game);
            var seen = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in Directory.GetDirectories(game.ModsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(dir, InfoFile)))
                {
                    foreach (var nested in FindNestedMods(dir))
                        result.Problems.Add(new ModProblem { Kind = ProblemKind.NestedFolder, Path = nested });
                    continue;
                }
                var mod = ReadMod(dir);
                if (mod == null)
                {
                    result.Problems.Add(new ModProblem { Kind = ProblemKind.BrokenInfo, Path = dir });
                    continue;
                }
                if (seen.ContainsKey(mod.Id))
                {
                    result.Problems.Add(new ModProblem { Kind = ProblemKind.DuplicateId, Path = dir, ModId = mod.Id });
                    continue;
                }
                mod.Enabled = !IsDllDisabled(mod) && (!enabled.TryGetValue(mod.Id, out var on) || on);
                seen[mod.Id] = mod;
                result.Mods.Add(mod);
            }

            foreach (var zip in Directory.GetFiles(game.ModsDir, "*.zip"))
                result.Problems.Add(new ModProblem { Kind = ProblemKind.ZipNotExtracted, Path = zip });

            foreach (var mod in result.Mods)
                foreach (var req in mod.Requirements.Select(RequirementId).Where(r => !seen.ContainsKey(r)))
                    result.Problems.Add(new ModProblem { Kind = ProblemKind.MissingRequirement, Path = mod.Folder, ModId = req });
            return result;
        }

        static string RequirementId(string requirement)
        {
            var m = RequirementVersion.Match(requirement);
            return m.Success ? m.Groups[1].Value : requirement;
        }

        // Mods/X/Info.json이 없을 때 X 아래 2단계까지 들어간 모드를 찾는다 (예: Mods/Mods/Y, Mods/Y-1.0/Y)
        static IEnumerable<string> FindNestedMods(string container)
        {
            foreach (var sub in Directory.GetDirectories(container))
            {
                if (File.Exists(Path.Combine(sub, InfoFile))) { yield return sub; continue; }
                foreach (var deeper in Directory.GetDirectories(sub).Where(d => File.Exists(Path.Combine(d, InfoFile))))
                    yield return deeper;
            }
        }

        public static InstalledMod ReadMod(string dir)
        {
            try
            {
                var info = Json.Parse(File.ReadAllText(Path.Combine(dir, InfoFile)));
                var id = Json.Str(info, "Id");
                if (string.IsNullOrWhiteSpace(id)) return null;
                var name = Json.Str(info, "DisplayName");
                return new InstalledMod
                {
                    Id = id,
                    DisplayName = string.IsNullOrWhiteSpace(name) ? id : name,
                    Author = Json.Str(info, "Author") ?? "",
                    Version = Json.Str(info, "Version") ?? "",
                    Folder = dir,
                    Requirements = Json.StrArray(info, "Requirements"),
                    Dll = ResolveDll(dir, Json.Str(info, "AssemblyName"), id),
                };
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is IOException)
            {
                // JavaScriptSerializer는 잘못된 JSON에 ArgumentException을 던진다
                Log.Warn("Info.json 읽기 실패: " + dir, ex);
                return null;
            }
        }

        // UMM 규칙: AssemblyName이 비었으면 <Id>.dll이 있을 때 그것을 쓴다.
        // Info.json은 외부 입력이라 모드 폴더 밖을 가리키는 경로("..\..\x.dll")는 무시한다
        static string ResolveDll(string dir, string assemblyName, string id)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(assemblyName))
                {
                    var byId = Path.Combine(dir, id + ".dll");
                    if (!File.Exists(byId) && !File.Exists(byId + DisabledSuffix)) return null;
                    assemblyName = id + ".dll";
                }
                var root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                var full = Path.GetFullPath(Path.Combine(dir, assemblyName));
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return full;
                Log.Warn($"모드 폴더 밖을 가리키는 AssemblyName 무시: {dir} → {assemblyName}");
                return null;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                Log.Warn($"AssemblyName 경로 해석 실패: {dir} → {assemblyName}", ex);
                return null;
            }
        }

        static bool IsDllDisabled(InstalledMod mod) =>
            mod.Dll != null && !File.Exists(mod.Dll) && File.Exists(mod.Dll + DisabledSuffix);

        // ---- 켜기/끄기: UMM은 <Managed>\UnityModManager\Params.xml의 <Mod Id Enabled>를 읽는다 ----

        static string ParamsPath(Game game) => Path.Combine(game.UmmDir, "Params.xml");

        static Dictionary<string, bool> ReadEnabledStates(Game game)
        {
            var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var path = ParamsPath(game);
            if (!File.Exists(path)) return states;
            try
            {
                foreach (var mod in XDocument.Load(path).Root?.Element("ModParams")?.Elements("Mod") ?? Enumerable.Empty<XElement>())
                {
                    var id = (string)mod.Attribute("Id");
                    if (id != null) states[id] = !string.Equals((string)mod.Attribute("Enabled"), "false", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) when (ex is System.Xml.XmlException || ex is IOException)
            {
                Log.Warn("Params.xml 읽기 실패 (모두 켜짐으로 표시)", ex);
            }
            return states;
        }

        // 끄기 = 모드 dll을 <이름>.dll.disabled로 바꿔 UMM이 아예 못 불러오게 한다.
        // Params.xml도 같이 맞춰서 UMM이 'dll 없음' 오류를 내지 않게 하고, 게임 안 메뉴에서 꺼 둔 모드도 여기서 켤 수 있게 한다
        public static void SetEnabled(Game game, IEnumerable<InstalledMod> mods, bool enabled)
        {
            var list = mods.ToList();
            foreach (var mod in list.Where(m => m.Dll != null)) RenameDll(mod.Dll, enabled);
            SetParamsEnabled(game, list.Select(m => m.Id), enabled);
        }

        static void RenameDll(string dll, bool enabled)
        {
            var disabled = dll + DisabledSuffix;
            if (enabled && !File.Exists(dll) && File.Exists(disabled))
            {
                File.Move(disabled, dll);
            }
            else if (!enabled && File.Exists(dll))
            {
                if (File.Exists(disabled)) File.Delete(disabled); // 예전에 꺼 둔 구버전 사본이 남아 있던 경우
                File.Move(dll, disabled);
            }
        }

        // 게임이 꺼져 있을 때만 호출해야 한다: UMM이 게임 종료 시 메모리 상태로 파일을 덮어쓴다
        internal static void SetParamsEnabled(Game game, IEnumerable<string> modIds, bool enabled)
        {
            var path = ParamsPath(game);
            XDocument doc;
            if (File.Exists(path))
            {
                doc = XDocument.Load(path); // 깨진 파일이면 예외를 올려 사용자에게 알린다 (다른 모드 설정을 날리지 않게)
            }
            else
            {
                XNamespace xsd = "http://www.w3.org/2001/XMLSchema", xsi = "http://www.w3.org/2001/XMLSchema-instance";
                doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
                    new XElement("Param", new XAttribute(XNamespace.Xmlns + "xsd", xsd), new XAttribute(XNamespace.Xmlns + "xsi", xsi)));
            }
            var modParams = doc.Root.Element("ModParams");
            if (modParams == null) doc.Root.Add(modParams = new XElement("ModParams"));
            foreach (var id in modIds)
            {
                var mod = modParams.Elements("Mod").FirstOrDefault(e => string.Equals((string)e.Attribute("Id"), id, StringComparison.OrdinalIgnoreCase));
                if (mod == null) modParams.Add(new XElement("Mod", new XAttribute("Id", id), new XAttribute("Enabled", enabled ? "true" : "false")));
                else mod.SetAttributeValue("Enabled", enabled ? "true" : "false");
            }
            Directory.CreateDirectory(game.UmmDir);
            // 저장 중 실패해도 원본이 깨지지 않게 임시 파일에 쓴 뒤 교체
            var tmp = path + ".tmp";
            Files.SaveXml(doc, tmp);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            Log.Info($"모드 {(enabled ? "켜기" : "끄기")}: {string.Join(", ", modIds)}");
        }

        // ---- 설치/삭제 ----

        // zip을 풀어 Info.json이 있는 폴더를 찾고 Mods/<폴더>로 복사.
        // 기본은 기존 폴더 위에 덮어써서 모드 설정 파일을 보존하고, clean이면 기존 폴더를 휴지통으로 보낸 뒤 새로 깐다
        public static InstalledMod InstallZip(Game game, string zipPath, bool clean = false)
        {
            var temp = Files.NewTempDir();
            try
            {
                // .NET Framework 4.6.1+는 압축 경로가 대상 밖으로 나가면(zip slip) 예외를 던진다
                ZipFile.ExtractToDirectory(zipPath, temp);
                var info = FindShallowestInfo(temp)
                    ?? throw new InvalidDataException("압축 파일 안에 Info.json이 없음: " + Path.GetFileName(zipPath));
                var source = Path.GetDirectoryName(info);
                var mod = ReadMod(source) ?? throw new InvalidDataException("Info.json 형식 오류: " + Path.GetFileName(zipPath));

                var existing = Scan(game).Mods.FirstOrDefault(m => string.Equals(m.Id, mod.Id, StringComparison.OrdinalIgnoreCase));
                bool keepDisabled = existing != null && IsDllDisabled(existing);
                var target = existing?.Folder ?? FreeFolderFor(game, mod.Id);
                // zip 검증이 끝난 뒤에 지워야 받은 파일이 깨졌을 때 기존 모드까지 잃지 않는다
                if (clean && existing != null) Files.Discard(existing.Folder);
                Files.CopyDirectory(source, target);
                var installed = ReadMod(target);
                // 꺼 둔 모드는 업데이트해도 꺼진 채로 둔다 (새 dll도 .disabled로)
                if (keepDisabled && installed?.Dll != null) RenameDll(installed.Dll, false);
                Log.Info($"모드 설치: {mod.Id} {mod.Version} → {target}"
                         + (clean && existing != null ? " (새로 설치)" : "") + (keepDisabled ? " (꺼진 상태 유지)" : ""));
                return installed;
            }
            finally
            {
                Files.TryDeleteTemp(temp);
            }
        }

        static string FindShallowestInfo(string root)
        {
            var level = new List<string> { root };
            for (int depth = 0; depth < 4 && level.Count > 0; depth++)
            {
                var hit = level.Select(d => Path.Combine(d, InfoFile)).FirstOrDefault(File.Exists);
                if (hit != null) return hit;
                level = level.SelectMany(Directory.GetDirectories).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            }
            return null;
        }

        // Mods/<Id>가 다른 모드(다른 Id)나 무관한 폴더로 이미 쓰이고 있으면 덮어쓰지 않고 _2, _3…으로 비켜 간다
        static string FreeFolderFor(Game game, string id)
        {
            var target = Path.Combine(game.ModsDir, SafeFolderName(id));
            for (int i = 2; Directory.Exists(target); i++)
                target = Path.Combine(game.ModsDir, SafeFolderName(id) + "_" + i);
            return target;
        }

        static string SafeFolderName(string id)
        {
            var name = new string(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
            return string.IsNullOrEmpty(name) ? "Mod" : name;
        }

        public static void Remove(InstalledMod mod)
        {
            Files.Discard(mod.Folder);
            Log.Info("모드 삭제(휴지통): " + mod.Id);
        }

        // ---- 잘못된 설치 자동 정리 ----

        // 고친 개수와 못 고친 문제를 돌려준다. 같은 모드가 겹치면 버전 높은 쪽을 남기고 나머지는 휴지통으로
        public static (int fixedCount, List<string> failures) FixProblems(Game game)
        {
            int fixedCount = 0;
            var failures = new List<string>();

            var containers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var problem in Scan(game).Problems.Where(p => p.Kind == ProblemKind.NestedFolder))
            {
                containers.Add(TopLevelDir(game, problem.Path));
                try { MoveNestedUp(game, problem.Path); fixedCount++; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Error("중첩 모드 정리 실패: " + problem.Path, ex);
                    failures.Add(problem.Path);
                }
            }
            DiscardEmptiedContainers(containers);

            foreach (var group in DuplicateGroups(game))
            {
                foreach (var loser in group.OrderByDescending(m => m.Version, Comparer<string>.Create(Versions.Compare))
                             .ThenBy(m => Path.GetFileName(m.Folder).Equals(m.Id, StringComparison.OrdinalIgnoreCase) ? 0 : 1).Skip(1))
                {
                    Files.Discard(loser.Folder);
                    fixedCount++;
                }
            }

            foreach (var zip in Directory.Exists(game.ModsDir) ? Directory.GetFiles(game.ModsDir, "*.zip") : new string[0])
            {
                try { InstallZip(game, zip); Files.Discard(zip); fixedCount++; }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Error("압축 모드 설치 실패: " + zip, ex);
                    failures.Add(zip);
                }
            }
            Log.Info($"문제 정리: {fixedCount}건 해결, {failures.Count}건 실패");
            return (fixedCount, failures);
        }

        static void MoveNestedUp(Game game, string nestedPath)
        {
            var mod = ReadMod(nestedPath);
            if (mod == null) throw new IOException("Info.json을 읽을 수 없음: " + nestedPath);
            var current = Scan(game).Mods.FirstOrDefault(m => string.Equals(m.Id, mod.Id, StringComparison.OrdinalIgnoreCase));
            if (current != null)
            {
                // 이미 제자리에 같은 모드가 있으면 버전이 높은 쪽만 남긴다
                if (Versions.Compare(mod.Version, current.Version) <= 0) { Files.Discard(nestedPath); return; }
                Files.Discard(current.Folder);
            }
            var target = Path.Combine(game.ModsDir, Path.GetFileName(nestedPath));
            if (Directory.Exists(target)) target = FreeFolderFor(game, mod.Id);
            Directory.Move(nestedPath, target);
        }

        static string TopLevelDir(Game game, string path)
        {
            var relative = path.Substring(game.ModsDir.Length).TrimStart('\\');
            return Path.Combine(game.ModsDir, relative.Split('\\')[0]);
        }

        // 방금 모드를 꺼낸 껍데기 폴더(예: Mods/Mods)만 휴지통으로. 안에 모드가 남아 있으면 둔다
        static void DiscardEmptiedContainers(IEnumerable<string> containers)
        {
            foreach (var dir in containers.Where(Directory.Exists))
            {
                if (Directory.EnumerateFiles(dir, InfoFile, SearchOption.AllDirectories).Any()) continue;
                Files.Discard(dir);
            }
        }

        static IEnumerable<List<InstalledMod>> DuplicateGroups(Game game)
        {
            var all = Directory.GetDirectories(game.ModsDir).Where(d => File.Exists(Path.Combine(d, InfoFile)))
                .Select(ReadMod).Where(m => m != null);
            return all.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.ToList()).ToList();
        }
    }
}
