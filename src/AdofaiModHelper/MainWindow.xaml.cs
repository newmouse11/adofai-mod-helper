using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AdofaiModHelper.Core;
using Microsoft.Win32;

namespace AdofaiModHelper
{
    public partial class MainWindow : Window
    {
        readonly Settings settings = Settings.Load();
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly ObservableCollection<ModRow> installedRows = new ObservableCollection<ModRow>();
        readonly ObservableCollection<StoreRow> storeRows = new ObservableCollection<StoreRow>();
        readonly ObservableCollection<PickRow> pickRows = new ObservableCollection<PickRow>();
        readonly ObservableCollection<Banner> banners = new ObservableCollection<Banner>();

        Game game;
        UmmStatus umm;
        ScanResult scan;
        List<CatalogMod> catalog = new List<CatalogMod>();
        Task catalogTask = Task.CompletedTask;
        bool catalogLoading;
        string latestUmm;
        int wizardStep;
        bool busy;
        TaskCompletionSource<DialogChoice> dialogResult;

        bool English => settings.Language == "en";

        public MainWindow()
        {
            InitializeComponent();
            App.SetLanguage(settings.Language);
            InstalledList.ItemsSource = installedRows;
            StoreList.ItemsSource = storeRows;
            PickList.ItemsSource = pickRows;
            BannerList.ItemsSource = banners;
            AppVersionText.Text = "v" + AppInfo.Version;
            SourceInitialized += (s, e) => DarkTitleBar.Apply(this);
            Loaded += OnLoaded;
            Closed += (s, e) => lifetime.Cancel();
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && DialogLayer.Visibility == Visibility.Visible) CloseDialog(DialogChoice.Cancel); };
        }

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            game = Game.FromUserPath(settings.GamePath) ?? Game.FindSteamInstall();
            Log.Info("게임 위치: " + (game?.Root ?? "찾지 못함"));
            UpdateLanguageButtons();
            catalogTask = LoadCatalogAsync();
            _ = LoadLatestUmmAsync();
            _ = CheckSelfUpdateAsync();
            if (settings.WizardDone && game != null) await ShowMainAsync();
            else await ShowWizardAsync(1);
        }

        // ================= 공통 작업 실행 =================

        // 파일을 바꾸는 작업은 전부 여기를 거친다: 한 번에 하나, 게임 실행 중 차단, 오류는 사용자에게 알림
        async Task<bool> RunAsync(Func<Task> work, bool needsGameClosed = true)
        {
            if (busy) return false;
            if (needsGameClosed && Game.IsRunning())
            {
                await ShowDialogAsync(App.T("S.D.GameRunning.Title"), App.T("S.D.GameRunning.Body"), App.T("S.Ok"), null);
                return false;
            }
            SetBusy(true, App.T("S.Working"));
            try
            {
                await work();
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error("게임 폴더 쓰기 권한 없음: " + game?.Root, ex);
                SetBusy(false, null);
                if (await ShowDialogAsync(App.T("S.D.Admin.Title"), App.T("S.D.Admin.Body"), App.T("S.D.Admin.Confirm"), App.T("S.Cancel")))
                    RestartAsAdmin();
                return false;
            }
            catch (RateLimitException ex)
            {
                Log.Warn("GitHub 한도 초과, 해제 시각 " + ex.ResetAt);
                Toast(App.T("S.T.RateLimit", ex.ResetAt.ToString("HH:mm")), true);
                return false;
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !lifetime.IsCancellationRequested))
            {
                Log.Error("네트워크 오류", ex);
                Toast(App.T("S.T.Offline"), true);
                return false;
            }
            catch (OperationCanceledException ex) when (!lifetime.IsCancellationRequested)
            {
                // 휴지통 이동 중 윈도우 셸 창에서 사용자가 취소한 경우
                Log.Info("사용자가 작업을 취소함: " + ex.Message);
                return false;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is InvalidOperationException
                                       || ex is NotSupportedException || ex is System.Xml.XmlException || ex is Win32Exception)
            {
                Log.Error("작업 실패", ex);
                SetBusy(false, null);
                await ShowDialogAsync(App.T("S.D.Error.Title"), ex.Message + "\n\n" + App.T("S.D.Error.Hint"), App.T("S.Ok"), null);
                return false;
            }
            finally
            {
                SetBusy(false, null);
                await RefreshLocalAsync();
            }
        }

        void SetBusy(bool on, string text)
        {
            busy = on;
            ContentRoot.IsEnabled = !on;
            BusyPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            BusyBar.IsIndeterminate = true;
            BusyText.Text = text ?? "";
        }

        IProgress<double> MakeProgress(string label)
        {
            BusyText.Text = label;
            BusyBar.IsIndeterminate = true;
            // Progress<T>는 만든 스레드(UI)에서 콜백을 실행한다
            return new Progress<double>(p =>
            {
                BusyBar.IsIndeterminate = false;
                BusyBar.Value = p * 100;
                BusyText.Text = label + "  " + App.T("S.Downloading", (int)(p * 100));
            });
        }

        void RestartAsAdmin()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName) { UseShellExecute = true, Verb = "runas" });
                Application.Current.Shutdown();
            }
            catch (Win32Exception ex)
            {
                // 사용자가 UAC 창에서 '아니요'를 누른 경우
                Log.Warn("관리자 재실행 취소됨", ex);
            }
        }

        // ================= 상태 읽기 =================

        async Task RefreshLocalAsync()
        {
            if (game == null) return;
            var g = game;
            try
            {
                var result = await Task.Run(() => (Umm.Detect(g), Mods.Scan(g)));
                umm = result.Item1;
                scan = result.Item2;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Error("게임 폴더 읽기 실패: " + g.Root, ex);
                Toast(ex.Message, true);
                return;
            }
            RenderStatus();
            RenderInstalled();
            RenderStore();
            RenderBanners();
        }

        async Task LoadCatalogAsync()
        {
            catalogLoading = true;
            RenderStore();
            try
            {
                catalog = await Catalog.LoadAsync(lifetime.Token);
                RebuildPickRows();
                RenderStore();
                DateTime? limitedUntil = null;
                using (var gate = new SemaphoreSlim(4))
                {
                    await Task.WhenAll(catalog.Select(async mod =>
                    {
                        await gate.WaitAsync(lifetime.Token);
                        try { await Catalog.ResolveAsync(mod, lifetime.Token); }
                        catch (RateLimitException ex) { limitedUntil = ex.ResetAt; }
                        catch (Exception ex) when (ex is HttpRequestException || ex is InvalidDataException || ex is ArgumentException
                                                   || (ex is TaskCanceledException && !lifetime.IsCancellationRequested))
                        {
                            // 한 모드의 실패가 목록 전체를 막지 않게 기록만 하고 넘어간다
                            Log.Warn("최신 릴리스 확인 실패: " + mod.Repo, ex);
                        }
                        finally { gate.Release(); }
                    }));
                }
                if (limitedUntil != null) Toast(App.T("S.T.RateLimit", limitedUntil.Value.ToString("HH:mm")), true);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return; // 창을 닫는 중
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException)
            {
                Log.Error("모드 목록 로드 실패", ex);
            }
            finally
            {
                catalogLoading = false;
            }
            RenderInstalled();
            RenderStore();
            RenderBanners();
        }

        async Task LoadLatestUmmAsync()
        {
            try
            {
                latestUmm = await Umm.GetLatestVersionAsync(lifetime.Token);
                RenderStatus();
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is ArgumentException)
            {
                Log.Warn("UMM 최신 버전 확인 실패", ex);
            }
        }

        async Task CheckSelfUpdateAsync()
        {
            try
            {
                var update = await SelfUpdate.CheckAsync(lifetime.Token);
                if (update == null) return;
                SelfUpdateButton.Content = App.T("S.NewVersion", update.Item1);
                SelfUpdateButton.Tag = update.Item2;
                SelfUpdateButton.Visibility = Visibility.Visible;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is RateLimitException || ex is ArgumentException)
            {
                // 저장소에 아직 릴리스가 없으면 404 → 조용히 넘어가되 기록은 남긴다
                Log.Info("도구 새 버전 확인 생략: " + ex.Message);
            }
        }

        CatalogMod CatalogFor(InstalledMod mod) =>
            catalog.FirstOrDefault(c => string.Equals(c.Id, mod.Id, StringComparison.OrdinalIgnoreCase));

        InstalledMod InstalledFor(CatalogMod mod) =>
            scan?.Mods.FirstOrDefault(m => string.Equals(m.Id, mod.Id, StringComparison.OrdinalIgnoreCase));

        HashSet<string> InstalledIds() =>
            new HashSet<string>(scan?.Mods.Select(m => m.Id) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        // ================= 메인 화면 그리기 =================

        async Task ShowMainAsync()
        {
            WizardView.Visibility = Visibility.Collapsed;
            MainView.Visibility = Visibility.Visible;
            await RefreshLocalAsync();
        }

        void RenderStatus()
        {
            if (game == null || umm == null) return;
            GamePathText.Text = game.Root;
            GamePathText.ToolTip = game.Root;
            UmmLatestText.Text = "";
            UmmActionButton.Visibility = Visibility.Visible;
            switch (umm.State)
            {
                case UmmState.Installed:
                    var installed = umm.Version?.ToString(3);
                    UmmStatusText.Text = App.T("S.Umm.Installed", installed);
                    bool outdated = latestUmm != null && Versions.Compare(latestUmm, installed) > 0;
                    if (outdated) UmmLatestText.Text = App.T("S.Umm.Latest", latestUmm);
                    UmmActionButton.Content = App.T("S.Update");
                    UmmActionButton.Visibility = outdated ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case UmmState.Broken:
                    UmmStatusText.Text = App.T("S.Umm.Broken");
                    UmmActionButton.Content = App.T("S.Repair");
                    break;
                default:
                    UmmStatusText.Text = App.T("S.Umm.NotInstalled");
                    UmmActionButton.Content = App.T("S.InstallUmm");
                    break;
            }
        }

        void RenderInstalled()
        {
            installedRows.Clear();
            foreach (var mod in scan?.Mods ?? new List<InstalledMod>())
                installedRows.Add(new ModRow(mod, CatalogFor(mod)));
            InstalledCount.Text = installedRows.Count > 0 ? "  " + installedRows.Count : "";
            UpdateAllButton.Visibility = installedRows.Any(r => r.HasUpdate) ? Visibility.Visible : Visibility.Collapsed;
            RenderEmptyText();
        }

        void RenderStore()
        {
            storeRows.Clear();
            foreach (var mod in catalog)
                storeRows.Add(new StoreRow(mod, InstalledFor(mod), catalogLoading, English));
            StoreCount.Text = storeRows.Count > 0 ? "  " + storeRows.Count : "";
            RenderEmptyText();
        }

        void RenderEmptyText()
        {
            bool store = TabStore.IsChecked == true;
            InstalledList.Visibility = store ? Visibility.Collapsed : Visibility.Visible;
            StoreList.Visibility = store ? Visibility.Visible : Visibility.Collapsed;
            bool empty = store ? storeRows.Count == 0 && !catalogLoading : installedRows.Count == 0;
            EmptyText.Text = store ? App.T("S.Empty.Store") : App.T("S.Empty.Installed");
            EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        }

        void RenderBanners()
        {
            banners.Clear();
            if (game == null || umm == null || scan == null) return;

            var buildId = game.ReadBuildId();
            if (buildId != null && settings.LastBuildId == null) { settings.LastBuildId = buildId; settings.Save(); }
            if (buildId != null && settings.LastBuildId != buildId)
                banners.Add(new Banner(App.T("S.B.GameUpdated"),
                    new BannerAction(App.T("S.CheckUpdates"), CheckModUpdatesAsync),
                    new BannerAction(App.T("S.DisableAll"), DisableAllAsync),
                    new BannerAction(App.T("S.Dismiss"), () => { settings.LastBuildId = buildId; settings.Save(); RenderBanners(); return Task.CompletedTask; })));

            if (umm.State == UmmState.NotInstalled)
                banners.Add(new Banner(App.T("S.B.UmmMissing"), new BannerAction(App.T("S.InstallUmm"), InstallUmmAsync)));
            else if (umm.State == UmmState.Broken)
                banners.Add(new Banner(App.T("S.B.UmmBroken", string.Join(", ", umm.Problems.Select(p => App.T(p)))),
                    new BannerAction(App.T("S.Repair"), InstallUmmAsync)));

            if (umm.OtherLoader != null) banners.Add(new Banner(App.T("S.B.OtherLoader", umm.OtherLoader)));

            int fixable = scan.Problems.Count(p => p.AutoFixable);
            if (fixable > 0) banners.Add(new Banner(App.T("S.B.Problems", fixable), new BannerAction(App.T("S.B.Fix"), FixProblemsAsync)));

            var brokenInfo = scan.Problems.Where(p => p.Kind == ProblemKind.BrokenInfo).Select(p => Path.GetFileName(p.Path)).ToList();
            if (brokenInfo.Count > 0) banners.Add(new Banner(App.T("S.B.BrokenInfo", string.Join(", ", brokenInfo))));

            foreach (var missing in scan.Problems.Where(p => p.Kind == ProblemKind.MissingRequirement).GroupBy(p => p.ModId, StringComparer.OrdinalIgnoreCase))
            {
                var owners = string.Join(", ", missing.Select(p => Path.GetFileName(p.Path)));
                var available = catalog.FirstOrDefault(c => string.Equals(c.Id, missing.Key, StringComparison.OrdinalIgnoreCase));
                banners.Add(available == null
                    ? new Banner(App.T("S.B.MissingReq", owners, missing.Key))
                    : new Banner(App.T("S.B.MissingReq", owners, missing.Key), new BannerAction(App.T("S.Install"), () => InstallModsAsync(new[] { available }))));
            }

            if (game.PathHasNonAscii) banners.Add(new Banner(App.T("S.B.NonAscii")));
        }

        // ================= 작업 =================

        async Task InstallUmmAsync()
        {
            if (umm?.OtherLoader != null && !await ShowDialogAsync(App.T("S.D.OtherLoader.Title"),
                    App.T("S.D.OtherLoader.Body", umm.OtherLoader), App.T("S.D.Continue"), App.T("S.Cancel")))
                return;
            var label = App.T("S.InstallingUmm");
            if (await RunAsync(() => Umm.InstallAsync(game, MakeProgress(label), lifetime.Token)))
                Toast(App.T("S.T.UmmInstalled", umm?.Version?.ToString(3)));
        }

        async Task InstallModsAsync(IEnumerable<CatalogMod> selected, bool withDependencies = true)
        {
            var list = withDependencies ? Catalog.WithDependencies(selected, catalog, InstalledIds()) : selected.ToList();
            if (list.Count == 0) return;

            // 이미 깔린 모드를 다시 받을 때는 쓰던 설정을 남길지 먼저 묻는다
            bool clean = false;
            var existing = list.Where(m => InstalledFor(m) != null).ToList();
            if (existing.Count > 0)
            {
                var title = existing.Count == 1 ? App.T("S.D.Exists.Title", existing[0].Name) : App.T("S.D.ExistsMany.Title", existing.Count);
                var choice = await ShowChoiceAsync(title, App.T("S.D.Exists.Body"),
                    App.T("S.D.Exists.Keep"), App.T("S.D.Exists.Clean"), App.T("S.Cancel"));
                if (choice == DialogChoice.Cancel) return;
                clean = choice == DialogChoice.Alternative;
            }

            int done = 0;
            await RunAsync(async () =>
            {
                foreach (var mod in list)
                {
                    await Catalog.InstallAsync(game, mod, MakeProgress(App.T("S.InstallingMod", mod.Name)), lifetime.Token, clean);
                    done++;
                }
            });
            if (done > 0) Toast(App.T("S.T.ModsInstalled", done));
        }

        async Task CheckModUpdatesAsync()
        {
            TabInstalled.IsChecked = true;
            if (catalogTask.IsCompleted) catalogTask = LoadCatalogAsync();
            await catalogTask;
            if (!installedRows.Any(r => r.HasUpdate)) Toast(App.T("S.T.NoUpdates"));
        }

        async Task DisableAllAsync()
        {
            var mods = scan?.Mods.ToList() ?? new List<InstalledMod>();
            if (mods.Count == 0) return;
            if (await RunAsync(() => Task.Run(() => Mods.SetEnabled(game, mods, false))))
                Toast(App.T("S.T.AllDisabled"));
        }

        async Task FixProblemsAsync()
        {
            (int fixedCount, List<string> failures) result = (0, new List<string>());
            await RunAsync(async () => result = await Task.Run(() => Mods.FixProblems(game)));
            if (result.fixedCount > 0) Toast(App.T("S.T.Fixed", result.fixedCount));
            if (result.failures.Count > 0) Toast(App.T("S.T.FixFailed", result.failures.Count), true);
        }

        // ================= 메인 화면 이벤트 =================

        async void OnUmmAction(object sender, RoutedEventArgs e) => await InstallUmmAsync();

        async void OnUpdateOne(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is ModRow row && row.Catalog != null)
                await InstallModsAsync(new[] { row.Catalog }, false);
        }

        async void OnUpdateAll(object sender, RoutedEventArgs e) =>
            await InstallModsAsync(installedRows.Where(r => r.HasUpdate).Select(r => r.Catalog).ToList(), false);

        async void OnStoreAction(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is StoreRow row)
                await InstallModsAsync(new[] { row.Mod }, InstalledFor(row.Mod) == null);
        }

        async void OnToggle(object sender, RoutedEventArgs e)
        {
            var box = (CheckBox)sender;
            if (!(box.DataContext is ModRow row)) return;
            bool want = box.IsChecked == true;
            bool ok = await RunAsync(() => Task.Run(() => Mods.SetEnabled(game, new[] { row.Mod }, want)));
            if (!ok)
            {
                // 실패하면 스위치를 원래 상태로 되돌린다 (바인딩은 유지)
                box.SetCurrentValue(ToggleButton.IsCheckedProperty, !want);
                return;
            }
            // dll을 꺼 버리면 이걸 필요로 하는 켜진 모드(JALib → 지퍼팩 등)도 같이 동작하지 않는다
            if (!want && scan != null)
            {
                var dependents = scan.Mods.Where(m => m.Enabled && DependsOn(m, row.Mod.Id)).Select(m => m.DisplayName).ToList();
                if (dependents.Count > 0) Toast(App.T("S.T.ForcedOn", row.Name, string.Join(", ", dependents)), true);
            }
        }

        // Info.json의 Requirements 또는 모드 목록의 requires (지퍼팩은 Info.json에 JALib을 LoadAfter로만 적어 둠)
        bool DependsOn(InstalledMod mod, string id) =>
            mod.Requirements.Any(r => r.StartsWith(id, StringComparison.OrdinalIgnoreCase))
            || (CatalogFor(mod)?.Requires.Contains(id, StringComparer.OrdinalIgnoreCase) ?? false);

        async void OnRemove(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).DataContext is ModRow row)) return;
            if (!await ShowDialogAsync(App.T("S.D.RemoveMod.Title", row.Name), App.T("S.D.RemoveMod.Body"), App.T("S.Delete"), App.T("S.Cancel")))
                return;
            if (await RunAsync(() => Task.Run(() => Mods.Remove(row.Mod))))
                Toast(App.T("S.T.Removed", row.Name));
        }

        async void OnRemoveUmm(object sender, RoutedEventArgs e)
        {
            if (game == null) return;
            if (!await ShowDialogAsync(App.T("S.D.RemoveUmm.Title"), App.T("S.D.RemoveUmm.Body"), App.T("S.D.Remove"), App.T("S.Cancel")))
                return;
            if (await RunAsync(() => Task.Run(() => Umm.Uninstall(game))))
                Toast(App.T("S.T.UmmRemoved"));
        }

        async void OnBannerAction(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is BannerAction action) await action.Run();
        }

        async void OnRefresh(object sender, RoutedEventArgs e)
        {
            if (catalogTask.IsCompleted) catalogTask = LoadCatalogAsync();
            await RefreshLocalAsync();
        }

        void OnTabChanged(object sender, RoutedEventArgs e)
        {
            if (EmptyText != null) RenderEmptyText(); // InitializeComponent 도중에도 호출된다
        }

        void OnLaunch(object sender, RoutedEventArgs e)
        {
            try
            {
                Game.LaunchViaSteam();
            }
            catch (Win32Exception steamError)
            {
                // steam:// 프로토콜이 등록 안 된 PC → exe 직접 실행 (UMM은 작업 폴더 기준으로 Mods를 찾는다)
                Log.Warn("Steam으로 실행 실패, exe 직접 실행", steamError);
                try
                {
                    Process.Start(new ProcessStartInfo(Path.Combine(game.Root, Game.ExeName)) { WorkingDirectory = game.Root, UseShellExecute = true });
                }
                catch (Win32Exception ex)
                {
                    Log.Error("게임 실행 실패", ex);
                    Toast(App.T("S.T.LaunchFailed"), true);
                }
            }
        }

        async void OnCopyDiagnostics(object sender, RoutedEventArgs e)
        {
            if (await CopyDiagnosticsAsync()) Toast(App.T("S.T.DiagCopied"));
        }

        async Task<bool> CopyDiagnosticsAsync()
        {
            var g = game;
            var text = await Task.Run(() => Diagnostics.Build(g));
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException ex)
            {
                // 다른 프로그램이 클립보드를 잡고 있으면 COMException
                Log.Warn("클립보드 복사 실패", ex);
                Toast(App.T("S.T.ClipboardBusy"), true);
                return false;
            }
        }

        // 도움말은 지금 화면 언어의 README '자주 묻는 질문'으로 바로 연다
        void OnHelp(object sender, RoutedEventArgs e) =>
            OpenUrl(English ? Catalog.RepoUrl + "/blob/main/README.en.md#faq" : Catalog.RepoUrl + "#" + Uri.EscapeDataString("자주-묻는-질문"));

        // 문의 글에 바로 붙여넣을 수 있게 진단 정보를 먼저 복사해 둔다
        async void OnContact(object sender, RoutedEventArgs e)
        {
            if (await CopyDiagnosticsAsync()) Toast(App.T("S.T.ContactCopied"));
            OpenUrl(Catalog.RepoUrl + "/issues/new");
        }

        void OnGitHub(object sender, RoutedEventArgs e) => OpenUrl(Catalog.RepoUrl);

        void OnSelfUpdate(object sender, RoutedEventArgs e)
        {
            if (SelfUpdateButton.Tag is string url) OpenUrl(url);
        }

        void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Win32Exception ex)
            {
                // 기본 브라우저가 없거나 막혀 있으면 주소라도 보여 준다
                Log.Warn("브라우저 열기 실패: " + url, ex);
                Toast(App.T("S.T.OpenFailed", url), true);
            }
        }

        async void OnLocateGame(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = App.T("S.LocateDialog"),
                Filter = App.T("S.ExeFilter") + "|" + Game.ExeName + "|*.exe|*.exe",
                CheckFileExists = true,
            };
            if (game != null) dialog.InitialDirectory = game.Root;
            if (dialog.ShowDialog(this) != true) return;

            var picked = Game.FromUserPath(dialog.FileName);
            if (picked == null)
            {
                await ShowDialogAsync(App.T("S.W1.Title"), App.T("S.W1.Invalid"), App.T("S.Ok"), null);
                return;
            }
            game = picked;
            settings.GamePath = picked.Root;
            settings.LastBuildId = null; // 다른 설치본이면 업데이트 감지 기준을 새로 잡는다
            settings.Save();
            Log.Info("게임 위치 변경: " + picked.Root);
            if (WizardView.Visibility == Visibility.Visible) await ShowWizardAsync(1);
            else await RefreshLocalAsync();
        }

        void OnLangKo(object sender, RoutedEventArgs e) => SwitchLanguage("ko");
        void OnLangEn(object sender, RoutedEventArgs e) => SwitchLanguage("en");

        async void SwitchLanguage(string language)
        {
            if (settings.Language == language) return;
            settings.Language = language;
            settings.Save();
            App.SetLanguage(language);
            UpdateLanguageButtons();
            RebuildPickRows();
            if (WizardView.Visibility == Visibility.Visible) await ShowWizardAsync(wizardStep);
            else { RenderStatus(); RenderInstalled(); RenderStore(); RenderBanners(); }
        }

        void UpdateLanguageButtons()
        {
            LangKo.Foreground = (Brush)FindResource(English ? "TextTertiary" : "TextPrimary");
            LangEn.Foreground = (Brush)FindResource(English ? "TextPrimary" : "TextTertiary");
        }

        // ================= 첫 실행 마법사 =================

        async Task ShowWizardAsync(int step)
        {
            wizardStep = step;
            MainView.Visibility = Visibility.Collapsed;
            WizardView.Visibility = Visibility.Visible;
            WizardStep1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
            WizardStep2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
            WizardStep3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
            RenderStepHeader(Step1Label, Step1Bar, 1);
            RenderStepHeader(Step2Label, Step2Bar, 2);
            RenderStepHeader(Step3Label, Step3Bar, 3);
            WizardBack.Visibility = step > 1 ? Visibility.Visible : Visibility.Hidden;
            WizardSkip.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
            WizardSecondary.Visibility = Visibility.Collapsed;

            if (step == 1)
            {
                W1Path.Text = game?.Root ?? "--";
                W1Message.Text = App.T(game != null ? "S.W1.Found" : "S.W1.NotFound");
                W1Warning.Visibility = game?.PathHasNonAscii == true ? Visibility.Visible : Visibility.Collapsed;
                WizardPrimary.Content = App.T("S.Next");
                WizardPrimary.IsEnabled = game != null;
            }
            else if (step == 2)
            {
                await RefreshLocalAsync();
                RenderWizardUmm();
            }
            else
            {
                await RefreshLocalAsync();
                RebuildPickRows();
                WizardPrimary.Content = App.T("S.InstallSelected");
                UpdatePickNotes();
            }
        }

        void RenderStepHeader(TextBlock label, Border bar, int step)
        {
            label.Foreground = (Brush)FindResource(step == wizardStep ? "TextPrimary" : step < wizardStep ? "TextSecondary" : "TextTertiary");
            label.FontWeight = step == wizardStep ? FontWeights.Medium : FontWeights.Normal;
            bar.Visibility = step == wizardStep ? Visibility.Visible : Visibility.Hidden;
        }

        void RenderWizardUmm()
        {
            if (umm == null) return;
            bool installed = umm.State == UmmState.Installed;
            var version = umm.Version?.ToString(3);
            bool outdated = installed && latestUmm != null && Versions.Compare(latestUmm, version) > 0;
            W2Status.Text = installed ? App.T("S.Umm.Installed", version) + (outdated ? "  " + App.T("S.Umm.Latest", latestUmm) : "")
                : umm.State == UmmState.Broken ? App.T("S.Umm.Broken") + ": " + string.Join(", ", umm.Problems.Select(p => App.T(p)))
                : App.T("S.Umm.NotInstalled");
            W2Warning.Visibility = umm.OtherLoader != null ? Visibility.Visible : Visibility.Collapsed;
            W2WarningText.Text = umm.OtherLoader != null ? App.T("S.B.OtherLoader", umm.OtherLoader) : "";
            WizardPrimary.Content = App.T(installed ? "S.Next" : umm.State == UmmState.Broken ? "S.Repair" : "S.InstallUmm");
            WizardPrimary.IsEnabled = true;
            WizardSecondary.Content = App.T(outdated ? "S.Update" : "S.Reinstall");
            WizardSecondary.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        }

        void RebuildPickRows()
        {
            var keep = new HashSet<string>(pickRows.Where(r => r.IsChecked).Select(r => r.Mod.Id), StringComparer.OrdinalIgnoreCase);
            pickRows.Clear();
            var installed = InstalledIds();
            // 라이브러리는 필요한 모드를 고르면 자동으로 따라오므로 목록에 따로 보이지 않는다
            foreach (var mod in catalog.Where(c => !c.Library && !installed.Contains(c.Id)))
                pickRows.Add(new PickRow(mod, English) { IsChecked = keep.Contains(mod.Id) });
            W3Loading.Visibility = catalog.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdatePickNotes();
        }

        void UpdatePickNotes()
        {
            var installed = InstalledIds();
            foreach (var row in pickRows)
            {
                var deps = Catalog.WithDependencies(new[] { row.Mod }, catalog, installed).Where(m => m != row.Mod).Select(m => m.Name).ToList();
                row.AutoNote = row.IsChecked && deps.Count > 0 ? App.T("S.W3.AutoDep", string.Join(", ", deps)) : null;
            }
            if (wizardStep == 3) WizardPrimary.IsEnabled = pickRows.Any(r => r.IsChecked);
        }

        void OnPickChanged(object sender, RoutedEventArgs e) => UpdatePickNotes();

        async void OnWizardBack(object sender, RoutedEventArgs e) => await ShowWizardAsync(wizardStep - 1);

        async void OnWizardSkip(object sender, RoutedEventArgs e) => await FinishWizardAsync();

        async void OnWizardSecondary(object sender, RoutedEventArgs e)
        {
            await InstallUmmAsync();
            RenderWizardUmm();
        }

        async void OnWizardPrimary(object sender, RoutedEventArgs e)
        {
            switch (wizardStep)
            {
                case 1:
                    settings.GamePath = game.Root;
                    settings.Save();
                    await ShowWizardAsync(2);
                    break;
                case 2:
                    if (umm?.State == UmmState.Installed) { await ShowWizardAsync(3); break; }
                    await InstallUmmAsync();
                    RenderWizardUmm();
                    break;
                case 3:
                    await InstallModsAsync(pickRows.Where(r => r.IsChecked).Select(r => r.Mod).ToList());
                    await FinishWizardAsync();
                    break;
            }
        }

        async Task FinishWizardAsync()
        {
            settings.WizardDone = true;
            settings.Save();
            await ShowMainAsync();
        }

        // ================= 확인창 / 토스트 =================

        enum DialogChoice { Cancel, Alternative, Primary }

        async Task<bool> ShowDialogAsync(string title, string body, string confirm, string cancel) =>
            await ShowChoiceAsync(title, body, confirm, null, cancel) == DialogChoice.Primary;

        // 주 버튼(오른쪽 끝, 노랑) / 대안 버튼(회색, 없으면 숨김) / 취소. Esc는 취소
        Task<DialogChoice> ShowChoiceAsync(string title, string body, string primary, string alternative, string cancel)
        {
            dialogResult?.TrySetResult(DialogChoice.Cancel);
            dialogResult = new TaskCompletionSource<DialogChoice>();
            DialogTitle.Text = title;
            DialogBody.Text = body;
            DialogConfirm.Content = primary;
            DialogAlt.Content = alternative;
            DialogAlt.Visibility = alternative == null ? Visibility.Collapsed : Visibility.Visible;
            DialogCancel.Content = cancel;
            DialogCancel.Visibility = cancel == null ? Visibility.Collapsed : Visibility.Visible;
            DialogLayer.Visibility = Visibility.Visible;
            ContentRoot.IsEnabled = false; // 키보드 포커스가 뒤 화면으로 새지 않게
            DialogConfirm.Focus();
            return dialogResult.Task;
        }

        void CloseDialog(DialogChoice choice)
        {
            DialogLayer.Visibility = Visibility.Collapsed;
            ContentRoot.IsEnabled = !busy;
            dialogResult?.TrySetResult(choice);
        }

        void OnDialogConfirm(object sender, RoutedEventArgs e) => CloseDialog(DialogChoice.Primary);
        void OnDialogAlt(object sender, RoutedEventArgs e) => CloseDialog(DialogChoice.Alternative);
        void OnDialogCancel(object sender, RoutedEventArgs e) => CloseDialog(DialogChoice.Cancel);

        // 오른쪽 아래 알림 4초, 최대 3개 (§5.7). 문제 알림은 왼쪽 선을 경고색으로
        void Toast(string message, bool warning = false)
        {
            var toast = new Border
            {
                Background = (Brush)FindResource("BgElevated"),
                BorderBrush = (Brush)FindResource(warning ? "Warning" : "TextPrimary"),
                BorderThickness = new Thickness(1, 0, 0, 0),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 8, 0, 0),
                MaxWidth = 360,
                Child = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("TextPrimary") },
            };
            Toasts.Children.Add(toast);
            while (Toasts.Children.Count > 3) Toasts.Children.RemoveAt(0);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (s, e) => { timer.Stop(); Toasts.Children.Remove(toast); };
            timer.Start();
        }
    }

    // 윈도우 기본 제목 표시줄을 어두운 색으로 (Win10 1809+ 다크 모드, Win11은 제목 표시줄 색까지)
    static class DarkTitleBar
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        const int UseImmersiveDarkMode = 20;
        const int CaptionColor = 35;
        const int TextColor = 36;

        public static void Apply(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            int on = 1;
            // 지원 안 하는 구버전 윈도우에선 HRESULT 실패만 돌려주고 아무 일도 없다 (외형만의 문제라 무시)
            DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref on, sizeof(int));
            int caption = ToColorRef(((SolidColorBrush)window.FindResource("BgSurface")).Color);
            int text = ToColorRef(((SolidColorBrush)window.FindResource("TextPrimary")).Color);
            DwmSetWindowAttribute(hwnd, CaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, TextColor, ref text, sizeof(int));
        }

        static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
    }
}
