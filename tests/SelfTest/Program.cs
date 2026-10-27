using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdofaiModHelper.Core;

// 실행: dotnet run --project tests/SelfTest            (오프라인 검사)
//       dotnet run --project tests/SelfTest -- --online (실제 UMM·목록 모드 다운로드 검사 포함)
static class Program
{
    static int failures;
    static string root;

    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        root = Path.Combine(Path.GetTempPath(), "amh-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        AppPaths.DataDir = Path.Combine(root, "appdata");
        Files.Discard = path => { if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); };
        try
        {
            Run("버전 비교", TestVersions);
            Run("게임 폴더 인식", TestGameLocate);
            Run("UMM 설치·진단·제거", TestUmm);
            Run("Assembly 방식 흔적 원복", TestAssemblyRestore);
            Run("모드 켜기/끄기 (dll 이름 + Params.xml)", TestToggle);
            Run("zip 설치와 설정 보존", TestInstallZip);
            Run("잘못된 설치 자동 정리", TestFixProblems);
            Run("모드 목록 파싱·의존성", TestCatalog);
            if (args.Contains("--online"))
            {
                Run("[온라인] 실제 UMM 설치", () => TestOnlineUmm().GetAwaiter().GetResult());
                Run("[온라인] 목록의 모든 모드 설치", () => TestOnlineCatalog().GetAwaiter().GetResult());
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { Console.WriteLine("임시 폴더 정리 실패: " + root); }
        }
        Console.WriteLine(failures == 0 ? "\n모든 검사 통과" : $"\n실패 {failures}건");
        return failures == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("[통과] " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("[실패] " + name + "\n  " + ex); }
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("검사 실패: " + message);
    }

    // ---- 가짜 게임 폴더 ----

    static Game MakeGame(string name)
    {
        var dir = Path.Combine(root, name, "steamapps", "common", "A Dance of Fire and Ice");
        Directory.CreateDirectory(Path.Combine(dir, Game.DataFolderName, "Managed"));
        // PE 헤더만 흉내: 0x3C에 오프셋, 거기에 "PE\0\0" + AMD64
        var pe = new byte[0x100];
        pe[0] = (byte)'M'; pe[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(pe, 0x3C);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(pe, 0x80);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(pe, 0x84);
        File.WriteAllBytes(Path.Combine(dir, Game.ExeName), pe);
        File.WriteAllText(Path.Combine(dir, Game.DataFolderName, "Managed", "System.Xml.dll"), "game's own");
        File.WriteAllText(Path.Combine(root, name, "steamapps", "appmanifest_977950.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"977950\"\n\t\"installdir\"\t\t\"A Dance of Fire and Ice\"\n\t\"buildid\"\t\t\"24397494\"\n}");
        return Game.FromUserPath(dir);
    }

    // 실제 UMM zip과 같은 평평한 구조. UnityModManager.dll은 버전 읽기가 되도록 진짜 .NET 어셈블리를 쓴다
    static string MakeFakeInstaller()
    {
        var dir = Path.Combine(root, "installer-" + Guid.NewGuid().ToString("N").Substring(0, 6), "UnityModManagerInstaller");
        Directory.CreateDirectory(dir);
        File.Copy(typeof(Program).Assembly.Location, Path.Combine(dir, "UnityModManager.dll"));
        foreach (var f in new[] { "0Harmony.dll", "dnlib.dll", "UnityModManager.xml", "System.Xml.dll", "winhttp_x86.dll" })
            File.WriteAllText(Path.Combine(dir, f), f);
        File.WriteAllText(Path.Combine(dir, "winhttp_x64.dll"), "x64 proxy");
        File.WriteAllText(Path.Combine(dir, "UnityModManagerConfig.xml"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Config><GameInfo Name=\"Other\"><Folder>X</Folder></GameInfo>" +
            "<GameInfo Name=\"A Dance of Fire and Ice\"><Folder>ADOFAI</Folder><ModsDirectory>Mods</ModsDirectory><ModInfo>Info.json</ModInfo>" +
            "<EntryPoint>[UnityEngine.CoreModule.dll]UnityEngine.MonoBehaviour.cctor:Before</EntryPoint></GameInfo></Config>");
        return Path.GetDirectoryName(dir);
    }

    static void WriteMod(string dir, string id, string version, string extraJson = "")
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Info.json"),
            "﻿{ \"Id\": \"" + id + "\", \"DisplayName\": \"" + id + " Mod\", \"Version\": \"" + version + "\"" + extraJson + " }");
    }

    // ---- 검사들 ----

    static void TestVersions()
    {
        Assert(Versions.Compare("2.8.1", "2.9.3") < 0, "2.8.1 < 2.9.3");
        Assert(Versions.Compare("1.5.2", "1.5.2.0") == 0, "1.5.2 == 1.5.2.0");
        Assert(Versions.Compare("1.0.0.45 beta45", "1.0.0.45") == 0, "베타 접미사 무시");
        Assert(Versions.Compare("0.33.2", "0.32.5") > 0, "0.33.2 > 0.32.5");
        Assert(Versions.Extract("v2.9.3 - Support v3.4.0 (r150)") == "2.9.3", "릴리스 이름에서 첫 버전 추출");
        Assert(Versions.Extract("r202_final") == null, "빌드 번호만 있는 태그는 버전 아님");
    }

    static void TestGameLocate()
    {
        var game = MakeGame("locate");
        Assert(game != null, "게임 루트 인식");
        Assert(Game.FromUserPath(Path.Combine(game.Root, Game.ExeName))?.Root == game.Root, "exe를 골라도 루트로");
        Assert(Game.FromUserPath(game.ManagedDir)?.Root == game.Root, "하위 폴더를 골라도 루트로");
        Assert(Game.FromUserPath(root) == null, "엉뚱한 폴더는 거부");
        Assert(game.ReadBuildId() == "24397494", "Steam 빌드 번호 읽기");
    }

    static void TestUmm()
    {
        var game = MakeGame("umm");
        Assert(Umm.Detect(game).State == UmmState.NotInstalled, "처음엔 미설치");
        Umm.InstallFromExtracted(game, MakeFakeInstaller());
        var status = Umm.Detect(game);
        Assert(status.State == UmmState.Installed, "설치 후 Installed, 문제: " + string.Join(",", status.Problems));
        Assert(status.Version != null, "버전 읽기");
        var ini = File.ReadAllBytes(Path.Combine(game.Root, "doorstop_config.ini"));
        var expected = Encoding.UTF8.GetBytes("[General]\r\nenabled = true\r\ntarget_assembly = A Dance of Fire and Ice_Data\\Managed\\UnityModManager\\UnityModManager.dll");
        Assert(ini.SequenceEqual(expected), "doorstop_config.ini가 공식 설치기와 바이트 단위로 동일 (BOM 없음)");
        Assert(File.ReadAllText(Path.Combine(game.Root, "winhttp.dll")) == "x64 proxy", "64비트 exe에는 x64 프록시");
        Assert(!File.Exists(Path.Combine(game.UmmDir, "System.Xml.dll")), "게임에 System.Xml.dll이 있으면 넣지 않음");
        var config = File.ReadAllText(Path.Combine(game.UmmDir, "Config.xml"));
        Assert(config.Contains("Name=\"A Dance of Fire and Ice\"") && config.Contains("UnityEngine.MonoBehaviour.cctor:Before"), "Config.xml에 ADOFAI GameInfo");
        Assert(!config.Contains("<GameInfo"), "Config.xml 루트는 Config");
        Assert(File.ReadAllBytes(Path.Combine(game.UmmDir, "Config.xml"))[0] == (byte)'<', "Config.xml은 BOM 없음 (공식 설치기와 동일)");
        Assert(Directory.Exists(game.ModsDir), "Mods 폴더 생성");

        File.Delete(Path.Combine(game.Root, "winhttp.dll"));
        status = Umm.Detect(game);
        Assert(status.State == UmmState.Broken && status.Problems.Contains("P.DoorstopMissing"), "프록시가 지워지면 Broken");

        Mods.SetParamsEnabled(game, new[] { "Keep" }, false);
        Umm.InstallFromExtracted(game, MakeFakeInstaller()); // 복구 = 재설치
        Assert(Umm.Detect(game).State == UmmState.Installed, "재설치로 복구");

        Umm.Uninstall(game);
        Assert(Umm.Detect(game).State == UmmState.NotInstalled, "제거 후 미설치");
        Assert(!File.Exists(Path.Combine(game.Root, "winhttp.dll")) && !File.Exists(Path.Combine(game.Root, "doorstop_config.ini")), "프록시·ini 제거");
        Assert(File.Exists(Path.Combine(game.UmmDir, "Params.xml")), "Params.xml(모드 켜기/끄기 상태)은 남김");

        // 다른 로더의 Doorstop은 건드리지 않는다
        File.WriteAllText(Path.Combine(game.Root, "winhttp.dll"), "bepinex");
        File.WriteAllText(Path.Combine(game.Root, "doorstop_config.ini"), "[General]\nenabled = true\ntarget_assembly = BepInEx\\core\\BepInEx.Preloader.dll");
        Umm.Uninstall(game);
        Assert(File.Exists(Path.Combine(game.Root, "winhttp.dll")), "BepInEx의 winhttp.dll은 보존");
    }

    static void TestAssemblyRestore()
    {
        var game = MakeGame("assembly");
        var core = Path.Combine(game.ManagedDir, "UnityEngine.CoreModule.dll");
        File.WriteAllText(core, "patched ... UnityModManagerStarter ...");
        File.WriteAllText(core + ".original_", "original");
        Assert(Umm.Detect(game).State == UmmState.NotInstalled, "UMM dll이 없으면 미설치로 봄");
        Umm.InstallFromExtracted(game, MakeFakeInstaller());
        Assert(File.ReadAllText(core) == "original" && !File.Exists(core + ".original_"), "패치된 dll을 원본으로 되돌림");
        Assert(Umm.Detect(game).State == UmmState.Installed, "원복 후 Doorstop으로 정상 설치");
    }

    static InstalledMod[] Pick(Game game, string id) => Mods.Scan(game).Mods.Where(m => m.Id == id).ToArray();
    static bool IsOn(Game game, string id) => Mods.Scan(game).Mods.First(m => m.Id == id).Enabled;

    static void TestToggle()
    {
        var game = MakeGame("toggle");
        var aDir = Path.Combine(game.ModsDir, "A");
        var aDll = Path.Combine(aDir, "A.Core.dll");
        WriteMod(aDir, "A", "1.0", ", \"AssemblyName\": \"A.Core.dll\"");
        File.WriteAllText(aDll, "v1");
        var bDll = Path.Combine(game.ModsDir, "B", "B.dll");
        WriteMod(Path.Combine(game.ModsDir, "B"), "B", "1.0");               // AssemblyName 없음 → UMM 규칙대로 <Id>.dll
        File.WriteAllText(bDll, "b");
        WriteMod(Path.Combine(game.ModsDir, "C"), "C", "1.0");               // dll 없는 콘텐츠 모드
        WriteMod(Path.Combine(game.ModsDir, "Evil"), "Evil", "1.0", ", \"AssemblyName\": \"..\\\\B\\\\B.dll\"");
        Assert(Mods.Scan(game).Mods.All(m => m.Enabled), "처음엔 모두 켜짐");

        Mods.SetEnabled(game, Pick(game, "A"), false);
        Assert(!File.Exists(aDll) && File.Exists(aDll + ".disabled"), "끄면 dll이 .dll.disabled로");
        Assert(File.ReadAllText(Path.Combine(game.UmmDir, "Params.xml")).Contains("Id=\"A\" Enabled=\"false\""), "Params.xml도 꺼짐으로");
        Assert(!IsOn(game, "A") && IsOn(game, "B"), "A만 꺼짐");

        // 꺼 둔 모드를 업데이트해도 꺼진 채로, 새 dll이 .disabled로 들어간다
        var v2 = MakeZip("a2.zip", s =>
        {
            WriteMod(Path.Combine(s, "A"), "A", "2.0", ", \"AssemblyName\": \"A.Core.dll\"");
            File.WriteAllText(Path.Combine(s, "A", "A.Core.dll"), "v2");
        });
        Mods.InstallZip(game, v2);
        Assert(!File.Exists(aDll) && File.ReadAllText(aDll + ".disabled") == "v2", "업데이트 후에도 꺼짐 유지 (새 버전)");

        Mods.SetEnabled(game, Pick(game, "A"), true);
        Assert(File.ReadAllText(aDll) == "v2" && !File.Exists(aDll + ".disabled") && IsOn(game, "A"), "켜면 원래 이름으로");

        Mods.SetEnabled(game, Pick(game, "B"), false);
        Assert(File.Exists(bDll + ".disabled") && !IsOn(game, "B"), "AssemblyName 없으면 <Id>.dll을 끔");
        Mods.SetEnabled(game, Pick(game, "B"), true);

        Mods.SetEnabled(game, Pick(game, "C"), false);
        Assert(!IsOn(game, "C"), "dll 없는 모드는 Params.xml로만 끔");

        Mods.SetEnabled(game, Pick(game, "Evil"), false);
        Assert(File.Exists(bDll), "모드 폴더 밖을 가리키는 AssemblyName은 건드리지 않음");

        // 게임 안 UMM 메뉴에서 꺼 둔 모드(Params만 false)도 도구에서 켤 수 있다
        Mods.SetParamsEnabled(game, new[] { "B" }, false);
        Assert(!IsOn(game, "B"), "UMM 메뉴에서 끈 상태 반영");
        Mods.SetEnabled(game, Pick(game, "B"), true);
        Assert(IsOn(game, "B"), "도구에서 다시 켜짐");

        // 실제 UMM이 쓴 형식(Hotkey 포함)도 다른 값은 보존한 채 Enabled만 바뀌어야 한다
        File.WriteAllText(Path.Combine(game.UmmDir, "Params.xml"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Param xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            "<Hotkey><keyCode>None</keyCode><modifiers>0</modifiers></Hotkey><CheckUpdates>2</CheckUpdates><UIScale>1.5</UIScale>" +
            "<ModParams><Mod Id=\"B\" Enabled=\"true\"><Hotkey><keyCode>F5</keyCode><modifiers>0</modifiers></Hotkey></Mod></ModParams></Param>");
        Mods.SetEnabled(game, Pick(game, "B"), false);
        var xml = File.ReadAllText(Path.Combine(game.UmmDir, "Params.xml"));
        Assert(xml.Contains("<UIScale>1.5</UIScale>") && xml.Contains("<keyCode>F5</keyCode>") && xml.Contains("Enabled=\"false\""), "다른 설정 보존");
    }

    static string MakeZip(string name, Action<string> fill)
    {
        var src = Path.Combine(root, "zipsrc-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(src);
        fill(src);
        var zip = Path.Combine(root, name);
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(src, zip);
        return zip;
    }

    static void TestInstallZip()
    {
        var game = MakeGame("zip");
        Directory.CreateDirectory(game.ModsDir);
        // AdofaiTweaks처럼 zip 안에 폴더가 하나 더 있는 구조
        var v1 = MakeZip("tweaks1.zip", s => WriteMod(Path.Combine(s, "AdofaiTweaks"), "AdofaiTweaks", "2.8.1"));
        var mod = Mods.InstallZip(game, v1);
        Assert(mod.Folder == Path.Combine(game.ModsDir, "AdofaiTweaks"), "하위 폴더 구조도 Mods/<Id>로");
        File.WriteAllText(Path.Combine(mod.Folder, "Settings.xml"), "user settings");

        var v2 = MakeZip("tweaks2.zip", s => WriteMod(Path.Combine(s, "AdofaiTweaks"), "AdofaiTweaks", "2.9.3"));
        mod = Mods.InstallZip(game, v2);
        Assert(mod.Version == "2.9.3", "업데이트 반영");
        Assert(File.ReadAllText(Path.Combine(mod.Folder, "Settings.xml")) == "user settings", "모드 설정 파일 보존");

        // 새로 설치: 설정·옛 파일은 휴지통으로, 같은 폴더 이름에 깨끗하게. 꺼 둔 상태는 유지
        File.WriteAllText(Path.Combine(mod.Folder, "old-leftover.dll"), "stale");
        File.WriteAllText(Path.Combine(mod.Folder, "AdofaiTweaks.dll.disabled"), "off");
        var v3 = MakeZip("tweaks3.zip", s =>
        {
            WriteMod(Path.Combine(s, "AdofaiTweaks"), "AdofaiTweaks", "3.0.0");
            File.WriteAllText(Path.Combine(s, "AdofaiTweaks", "AdofaiTweaks.dll"), "v3");
        });
        mod = Mods.InstallZip(game, v3, clean: true);
        Assert(mod.Folder == Path.Combine(game.ModsDir, "AdofaiTweaks") && mod.Version == "3.0.0", "새로 설치도 같은 폴더에");
        Assert(!File.Exists(Path.Combine(mod.Folder, "Settings.xml")) && !File.Exists(Path.Combine(mod.Folder, "old-leftover.dll")), "설정·옛 파일 제거");
        Assert(!File.Exists(Path.Combine(mod.Folder, "AdofaiTweaks.dll")) && File.ReadAllText(Path.Combine(mod.Folder, "AdofaiTweaks.dll.disabled")) == "v3",
            "꺼 둔 모드는 새로 설치해도 꺼진 채 (새 dll로)");

        // Iridium처럼 루트에 Info.json이 있는 구조 + 소문자 info.json
        var flat = MakeZip("flat.zip", s => { WriteMod(s, "Iridium", "2.0.1"); File.Move(Path.Combine(s, "Info.json"), Path.Combine(s, "info.json")); });
        Assert(Mods.InstallZip(game, flat).Folder == Path.Combine(game.ModsDir, "Iridium"), "루트 Info.json(소문자) 구조");

        // 같은 이름 폴더를 Id가 다른 모드가 쓰고 있으면 덮어쓰지 않는다
        WriteMod(Path.Combine(game.ModsDir, "Quartz"), "SomethingElse", "1.0");
        var quartz = MakeZip("quartz.zip", s => WriteMod(Path.Combine(s, "Quartz"), "Quartz", "2.1.1"));
        Assert(Mods.InstallZip(game, quartz).Folder == Path.Combine(game.ModsDir, "Quartz_2"), "이름 겹치면 Quartz_2로");
        Assert(Mods.ReadMod(Path.Combine(game.ModsDir, "Quartz")).Id == "SomethingElse", "기존 다른 모드는 그대로");

        var bad = MakeZip("bad.zip", s => File.WriteAllText(Path.Combine(s, "readme.txt"), "no mod"));
        bool threw = false;
        try { Mods.InstallZip(game, bad); } catch (InvalidDataException) { threw = true; }
        Assert(threw, "Info.json 없는 zip은 거부");
    }

    static void TestFixProblems()
    {
        var game = MakeGame("fix");
        var mods = game.ModsDir;
        WriteMod(Path.Combine(mods, "A"), "A", "1.0");
        WriteMod(Path.Combine(mods, "Mods", "A"), "A", "2.0");          // 중첩 + 더 새 버전
        WriteMod(Path.Combine(mods, "Mods", "B"), "B", "1.0");          // 중첩만
        File.WriteAllText(Path.Combine(mods, "Mods", "B.zip"), "leftover");
        WriteMod(Path.Combine(mods, "C1"), "C", "1.0");                  // 같은 Id 중복
        WriteMod(Path.Combine(mods, "C2"), "C", "3.0");
        Directory.CreateDirectory(Path.Combine(mods, "Broken"));
        File.WriteAllText(Path.Combine(mods, "Broken", "Info.json"), "{ not json");
        Directory.CreateDirectory(Path.Combine(mods, "MyNotes"));           // 모드와 무관한 폴더
        File.WriteAllText(Path.Combine(mods, "MyNotes", "memo.txt"), "keep me");
        File.Copy(MakeZip("d.zip", s => WriteMod(Path.Combine(s, "D"), "D", "1.0")), Path.Combine(mods, "D.zip"));

        var problems = Mods.Scan(game).Problems;
        Assert(problems.Count(p => p.Kind == ProblemKind.NestedFolder) == 2, "중첩 2개 감지");
        Assert(problems.Count(p => p.Kind == ProblemKind.DuplicateId) == 1, "중복 1개 감지");
        Assert(problems.Count(p => p.Kind == ProblemKind.ZipNotExtracted) == 1, "안 푼 zip 감지");
        Assert(problems.Count(p => p.Kind == ProblemKind.BrokenInfo) == 1, "깨진 Info.json 감지");

        var (fixedCount, fails) = Mods.FixProblems(game);
        Assert(fails.Count == 0, "실패 없음: " + string.Join(",", fails));
        var scan = Mods.Scan(game);
        Assert(scan.Mods.First(m => m.Id == "A").Version == "2.0", "A는 새 버전만 남음");
        Assert(scan.Mods.Any(m => m.Id == "B") && scan.Mods.First(m => m.Id == "B").Folder == Path.Combine(mods, "B"), "B는 Mods/B로");
        Assert(scan.Mods.Single(m => m.Id == "C").Version == "3.0", "중복 C는 높은 버전만");
        Assert(scan.Mods.Any(m => m.Id == "D") && !File.Exists(Path.Combine(mods, "D.zip")), "zip은 풀어서 설치");
        Assert(!Directory.Exists(Path.Combine(mods, "Mods")), "비워진 Mods/Mods 정리");
        Assert(File.Exists(Path.Combine(mods, "MyNotes", "memo.txt")), "무관한 폴더는 건드리지 않음");
        Assert(scan.Problems.Count == 1 && scan.Problems[0].Kind == ProblemKind.BrokenInfo, "자동으로 못 고치는 것만 남음");
    }

    static void TestCatalog()
    {
        string json;
        using (var s = typeof(Catalog).Assembly.GetManifestResourceStream("mods.json"))
        using (var r = new StreamReader(s)) json = r.ReadToEnd();
        var catalog = Catalog.Parse(json);
        Assert(catalog.Count == 13, "13개 항목 모두 유효 (실제 " + catalog.Count + ")");
        Assert(catalog.All(c => c.DescriptionKo.Length > 0 && c.DescriptionEn.Length > 0), "한/영 설명 모두 있음");

        var pick = catalog.Where(c => c.Id == "JipperResourcePack" || c.Id == "BetterCalibration");
        var order = Catalog.WithDependencies(pick, catalog, new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Select(c => c.Id).ToList();
        Assert(order.First() == "JALib" && order.Count == 3, "JALib 먼저, 한 번만: " + string.Join(",", order));
        var withJalib = Catalog.WithDependencies(pick, catalog, new HashSet<string>(new[] { "JALib" }, StringComparer.OrdinalIgnoreCase));
        Assert(withJalib.All(c => c.Id != "JALib"), "이미 있으면 다시 안 받음");

        Assert(Catalog.Parse("{\"mods\":[{\"id\":\"x\",\"repo\":\"../evil\",\"asset\":\"a\"}]}").Count == 0, "잘못된 repo 형식은 무시");
    }

    // ---- 온라인 검사 ----

    static async Task TestOnlineUmm()
    {
        var game = MakeGame("online-umm");
        await Umm.InstallAsync(game, null, CancellationToken.None);
        var status = Umm.Detect(game);
        Assert(status.State == UmmState.Installed, "실제 UMM 설치 후 Installed: " + string.Join(",", status.Problems));
        var latest = await Umm.GetLatestVersionAsync(CancellationToken.None);
        Console.WriteLine($"  설치된 UMM {status.Version} / 피드 최신 {latest}");
        Assert(Versions.Compare(status.Version.ToString(), latest) >= 0, "Dropbox 판이 피드 최신 버전과 같음");
        var config = File.ReadAllText(Path.Combine(game.UmmDir, "Config.xml"));
        Assert(config.Contains("ADOStartup.Startup:Before"), "공식 설정에서 ADOFAI StartingPoint를 가져옴");
    }

    static async Task TestOnlineCatalog()
    {
        var game = MakeGame("online-mods");
        Directory.CreateDirectory(game.ModsDir);
        string json;
        using (var s = typeof(Catalog).Assembly.GetManifestResourceStream("mods.json"))
        using (var r = new StreamReader(s)) json = r.ReadToEnd();
        var bad = new List<string>();
        foreach (var mod in Catalog.Parse(json))
        {
            try
            {
                var installed = await Catalog.InstallAsync(game, mod, null, CancellationToken.None);
                var match = string.Equals(installed.Id, mod.Id, StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"  {(match ? "OK " : "ID!")} {mod.Id,-24} 최신 {mod.LatestVersion,-10} 설치된 Info.json: {installed.Id} {installed.Version} ({Path.GetFileName(installed.Folder)})");
                if (!match) bad.Add(mod.Id + "≠" + installed.Id);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERR {mod.Id}: {ex.GetType().Name} {ex.Message}");
                bad.Add(mod.Id);
            }
        }
        Assert(bad.Count == 0, "문제 있는 항목: " + string.Join(", ", bad));
    }
}
