// mac 대응: Sources/XFinder/Views/UninstallSheet.swift — Windows 재설계: 설치 프로그램 제거 + 잔재 정리 창 (스펙 04 §11)
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XFinder.Models;
using XFinder.Services;

namespace XFinder.Views;

/// <summary>설치 앱 목록 행 (아이콘은 로드 시점에 백그라운드에서 채움).</summary>
public sealed class InstalledAppRow
{
    public required InstalledApp App { get; init; }
    public ImageSource? Icon { get; init; }
    public string Name => App.DisplayName;
    public string Publisher => string.IsNullOrWhiteSpace(App.Publisher) ? "게시자 정보 없음" : App.Publisher;
    public string SizeText => App.EstimatedSizeBytes > 0 ? Format.Bytes(App.EstimatedSizeBytes) : "";
}

/// <summary>잔재 후보 행 — 기본 전체 체크 (사용자 검토 필수).</summary>
public sealed class ResidueRow : ObservableObject
{
    public required ResidueCandidate Candidate { get; init; }
    public required string DisplayPath { get; init; }
    public string Name => Path.GetFileName(Candidate.Path.TrimEnd('\\'));
    public string Kind => Candidate.Kind;
    public string SizeText => Format.Bytes(Candidate.Size);

    private bool _isChecked = true;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}

/// <summary>
/// 프로그램 제거 창 — 레지스트리 Uninstall 키 기반 설치 앱 목록 + AppCleaner식 잔재 체크 목록.
/// 위험 동작이므로 실행 전 자체 확인 오버레이를 거치고, 잔재 삭제는 휴지통 경유만 한다.
/// </summary>
public partial class UninstallWindow : Window
{
    private readonly AppModel _model;

    private List<InstalledAppRow> _allApps = new();
    private readonly ObservableCollection<InstalledAppRow> _filteredApps = new();
    private readonly ObservableCollection<ResidueRow> _residues = new();

    private InstalledAppRow? _selected;
    private CancellationTokenSource? _scanCts;
    private bool _busy;
    private bool _suppressSelection;

    private Action? _confirmAction;
    private bool _confirmDestructive = true;
    private int _confirmFocus;

    public UninstallWindow(Window owner, AppModel model)
    {
        InitializeComponent();
        _model = model;
        Owner = owner;

        AppList.ItemsSource = _filteredApps;
        ResidueList.ItemsSource = _residues;

        SourceInitialized += (_, _) => ThemeService.ApplyChrome(this);
        Loaded += async (_, _) => await LoadAppsAsync();
        Closed += (_, _) =>
        {
            _scanCts?.Cancel();
            _model.Sheet = null;
        };
        PreviewKeyDown += OnWindowKeyDown;

        UpdateFooter();
    }

    // ── 설치 앱 목록 로드/필터 ───────────────────────────────────────────

    private async Task LoadAppsAsync()
    {
        AppsLoading.Visibility = Visibility.Visible;
        AppsEmpty.Visibility = Visibility.Collapsed;
        CountText.Text = "";

        var rows = await Task.Run(() =>
        {
            var apps = AppUninstaller.ListInstalledApps();
            return apps.Select(a => new InstalledAppRow { App = a, Icon = LoadIcon(a) }).ToList();
        });

        _allApps = rows;
        AppsLoading.Visibility = Visibility.Collapsed;
        ApplyFilter();
        CountText.Text = $"{_allApps.Count}개 설치됨";
    }

    private static ImageSource? LoadIcon(InstalledApp app)
    {
        try
        {
            if (AppUninstaller.ResolveIconPath(app) is { } src)
                return ShellInterop.GetIcon(src.Path, src.IsDirectory, large: true);
        }
        catch { }
        return null;
    }

    private void ApplyFilter()
    {
        var needle = SearchBox.Text.Trim();
        var keep = _selected;

        _suppressSelection = true;
        _filteredApps.Clear();
        foreach (var row in _allApps)
        {
            if (needle.Length > 0
                && !row.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !row.App.Publisher.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;
            _filteredApps.Add(row);
        }
        var keepVisible = keep is not null && _filteredApps.Contains(keep);
        if (keepVisible) AppList.SelectedItem = keep;
        _suppressSelection = false;

        // 선택 항목이 필터에서 사라지면 우측 패널도 초기화
        if (keep is not null && !keepVisible)
        {
            _selected = null;
            UpdateHeader();
            StartResidueScan();
            UpdateFooter();
        }
        AppsEmpty.Visibility = AppsLoading.Visibility == Visibility.Collapsed && _filteredApps.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyFilter();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        _selected = null;
        AppList.SelectedItem = null;
        ClearResidues();
        UpdateHeader();
        UpdateFooter();
        await LoadAppsAsync();
    }

    // ── 선택/잔재 스캔 ───────────────────────────────────────────────────

    private void OnAppSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        var row = AppList.SelectedItem as InstalledAppRow;
        if (ReferenceEquals(row, _selected)) return;
        _selected = row;
        UpdateHeader();
        StartResidueScan();
    }

    private void UpdateHeader()
    {
        if (_selected is null)
        {
            AppNameText.Text = "프로그램을 선택하세요";
            AppMetaText.Text = "";
            ScanStatus.Text = "";
            return;
        }
        var app = _selected.App;
        AppNameText.Text = app.DisplayName;
        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(app.Publisher)) meta.Add(app.Publisher);
        if (!string.IsNullOrWhiteSpace(app.DisplayVersion)) meta.Add($"버전 {app.DisplayVersion}");
        if (app.EstimatedSizeBytes > 0) meta.Add(Format.Bytes(app.EstimatedSizeBytes));
        AppMetaText.Text = string.Join("  ·  ", meta);
    }

    private async void StartResidueScan()
    {
        _scanCts?.Cancel();
        ClearResidues();
        UpdateFooter();

        if (_selected is null)
        {
            ResiduePlaceholder.Visibility = Visibility.Visible;
            ScanStatus.Text = "";
            return;
        }

        ResiduePlaceholder.Visibility = Visibility.Collapsed;
        ScanStatus.Text = "관련 파일을 찾는 중…";

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var app = _selected.App;

        List<ResidueCandidate> found;
        try
        {
            found = await Task.Run(() => AppUninstaller.ScanResidue(app, cts.Token), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch { found = new(); }

        if (cts.IsCancellationRequested || !ReferenceEquals(_selected?.App, app)) return;

        foreach (var c in found)
        {
            var row = new ResidueRow { Candidate = c, DisplayPath = DisplayPathOf(c.Path) };
            row.PropertyChanged += (_, _) => UpdateFooter();
            _residues.Add(row);
        }

        ScanStatus.Text = found.Count == 0
            ? "잔재 후보가 없습니다."
            : $"잔재 후보 {found.Count}개  ·  {Format.Bytes(found.Sum(f => f.Size))}";
        UpdateFooter();
    }

    private void ClearResidues() => _residues.Clear();

    /// <summary>상위 폴더 경로 — 홈은 "~"로 축약 (mac DisplayPath 대응).</summary>
    private static string DisplayPathOf(string path)
    {
        var parent = Path.GetDirectoryName(path.TrimEnd('\\')) ?? path;
        var home = AppModel.HomePath.TrimEnd('\\');
        // 경로 경계 확인 — "C:\Users\me"가 "C:\Users\meow\..."에 매칭되지 않게
        if (string.Equals(parent, home, StringComparison.OrdinalIgnoreCase))
            return "~";
        if (parent.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase))
            return "~" + parent[home.Length..];
        return parent;
    }

    // ── 잔재 행 상호작용 ─────────────────────────────────────────────────

    private void OnResidueRowClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ResidueRow row)
            row.IsChecked = !row.IsChecked;
    }

    private void OnRevealResidue(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is ResidueRow row)
        {
            try { ShellInterop.RevealInExplorer(row.Candidate.Path); } catch { }
        }
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var row in _residues) row.IsChecked = true;
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        foreach (var row in _residues) row.IsChecked = false;
    }

    private void UpdateFooter()
    {
        var checkedRows = _residues.Where(r => r.IsChecked).ToList();
        SummaryText.Text = _residues.Count == 0
            ? ""
            : $"잔재 {_residues.Count}개 중 {checkedRows.Count}개 선택  ·  {Format.Bytes(checkedRows.Sum(r => r.Candidate.Size))}";
        BtnSelectAll.IsEnabled = _residues.Any(r => !r.IsChecked);
        BtnClearAll.IsEnabled = checkedRows.Count > 0;
        BtnTrash.IsEnabled = !_busy && checkedRows.Count > 0;
        BtnUninstall.IsEnabled = !_busy && _selected is not null
            && !string.IsNullOrWhiteSpace(_selected.App.UninstallString);
    }

    // ── 제거 실행 / 잔재 휴지통 (확인 오버레이 경유) ─────────────────────

    private void OnRunUninstall(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var app = _selected.App;
        ShowConfirm(
            title: "프로그램 제거",
            message: $"“{app.DisplayName}”을(를) 제거하시겠습니까?\n\n해당 프로그램의 제거 프로그램이 실행되며, 관리자 권한을 요청할 수 있습니다. 제거가 끝난 뒤 잔재 목록을 다시 확인하세요.",
            confirmTitle: "제거 실행",
            destructive: true,
            action: () => RunUninstaller(app));
    }

    private void RunUninstaller(InstalledApp app)
    {
        if (AppUninstaller.RunUninstaller(app, out var error))
            ShowAlert("XFinder",
                $"“{app.DisplayName}”의 제거 프로그램을 실행했습니다.\n제거가 끝나면 새로 고침으로 목록을 갱신하세요.");
        else
            ShowAlert("오류", $"제거 프로그램을 실행하지 못했습니다:\n{error}");
    }

    private void OnTrashResidue(object sender, RoutedEventArgs e)
    {
        var checkedRows = _residues.Where(r => r.IsChecked).ToList();
        if (checkedRows.Count == 0) return;
        var total = Format.Bytes(checkedRows.Sum(r => r.Candidate.Size));
        ShowConfirm(
            title: "잔재 정리",
            message: $"선택한 {checkedRows.Count}개 항목({total})을 휴지통으로 이동하시겠습니까?\n\n이름 기반 추정 결과이므로 목록을 다시 한 번 확인하세요. 항목은 휴지통에서 복원할 수 있습니다.",
            confirmTitle: "휴지통으로 이동",
            destructive: true,
            action: DoTrashChecked);
    }

    private async void DoTrashChecked()
    {
        var targets = _residues.Where(r => r.IsChecked).Select(r => r.Candidate.Path).ToList();
        if (targets.Count == 0) return;

        _busy = true;
        UpdateFooter();
        ScanStatus.Text = "휴지통으로 이동 중…";

        var failed = await Task.Run(() =>
        {
            ShellInterop.MoveToRecycleBin(targets);
            // 원경로에 여전히 존재하는 것만 실제 실패로 판정 (mac performUninstall과 동일)
            return targets.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        });

        _busy = false;
        var moved = targets.Count - failed.Count;
        if (failed.Count == 0)
        {
            ShowAlert("XFinder", $"잔재 {moved}개를 휴지통으로 이동했습니다.");
        }
        else
        {
            var preview = string.Join("\n", failed.Take(8).Select(f => "• " + Path.GetFileName(f.TrimEnd('\\'))));
            var more = failed.Count > 8 ? $"\n…외 {failed.Count - 8}개" : "";
            ShowAlert("오류",
                $"다음 항목을 삭제하지 못했습니다:\n{preview}{more}\n\n실행 중인 프로그램이 점유 중이거나 관리자 권한이 필요한 항목일 수 있습니다. 프로그램을 먼저 종료한 뒤 다시 시도하세요.");
        }
        StartResidueScan();   // 재스캔으로 목록 갱신
    }

    // ── 알림 오버레이 ────────────────────────────────────────────────────

    private void ShowAlert(string title, string message)
    {
        AlertTitle.Text = title;
        AlertMessage.Text = message;
        AlertOverlay.Visibility = Visibility.Visible;
    }

    private void DismissAlert() => AlertOverlay.Visibility = Visibility.Collapsed;

    private void OnAlertOk(object sender, RoutedEventArgs e) => DismissAlert();
    private void OnAlertScrim(object sender, MouseButtonEventArgs e) => DismissAlert();
    private void OnPanelClickEat(object sender, MouseButtonEventArgs e) => e.Handled = true;

    // ── 확인 오버레이 (MainWindow ConfirmDialog와 동일 패턴) ─────────────

    private void ShowConfirm(string title, string message, string confirmTitle, bool destructive, Action action)
    {
        _confirmAction = action;
        _confirmDestructive = destructive;
        _confirmFocus = 0;
        ConfirmTitle.Text = title;
        ConfirmMessage.Text = message;
        ConfirmActionBtn.Content = confirmTitle;
        ConfirmOverlay.Visibility = Visibility.Visible;
        RefreshConfirmFocus();
    }

    private void ExecuteConfirm(int index)
    {
        var action = _confirmAction;
        _confirmAction = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        if (index == 0) action?.Invoke();
    }

    private void CancelConfirm()
    {
        _confirmAction = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnConfirmAction(object sender, RoutedEventArgs e) => ExecuteConfirm(0);
    private void OnConfirmCancel(object sender, RoutedEventArgs e) => ExecuteConfirm(1);
    private void OnConfirmScrim(object sender, MouseButtonEventArgs e) => CancelConfirm();

    private void RefreshConfirmFocus()
    {
        var accent = (Brush)FindResource("AccentBrush");
        ApplyConfirmButtonLook(ConfirmActionBtn, _confirmDestructive, _confirmFocus == 0, accent);
        ApplyConfirmButtonLook(ConfirmCancelBtn, destructive: false, focused: _confirmFocus == 1, accent);
    }

    private void ApplyConfirmButtonLook(Button btn, bool destructive, bool focused, Brush accent)
    {
        if (btn.Template.FindName("Bg", btn) is Border bg)
        {
            bg.BorderBrush = focused ? accent : Brushes.Transparent;
            bg.BorderThickness = new Thickness(focused ? 3 : 0);
            if (destructive)
            {
                bg.Background = new SolidColorBrush(
                    Color.FromArgb((byte)((focused ? 1.0 : 0.75) * 255), 0xE8, 0x3B, 0x30));
                btn.Foreground = Brushes.White;
            }
            else
            {
                bg.ClearValue(Border.BackgroundProperty);
                btn.ClearValue(ForegroundProperty);
            }
        }
        else
        {
            // 템플릿이 아직 적용 전이면 적용 후 재시도
            btn.ApplyTemplate();
            Dispatcher.BeginInvoke(RefreshConfirmFocus, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    // ── 키 라우팅 (창 로컬 — 전역 키는 MainWindow 소유라 여기엔 영향 없음) ─

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (AlertOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key is Key.Enter or Key.Escape or Key.Space) { DismissAlert(); e.Handled = true; }
            return;
        }
        if (ConfirmOverlay.Visibility == Visibility.Visible)
        {
            switch (e.Key)
            {
                case Key.Left or Key.Up: _confirmFocus = (_confirmFocus + 1) % 2; RefreshConfirmFocus(); e.Handled = true; break;
                case Key.Right or Key.Down or Key.Tab: _confirmFocus = (_confirmFocus + 1) % 2; RefreshConfirmFocus(); e.Handled = true; break;
                case Key.Enter: ExecuteConfirm(_confirmFocus); e.Handled = true; break;
                case Key.Escape: CancelConfirm(); e.Handled = true; break;
            }
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (SearchBox.IsKeyboardFocusWithin && SearchBox.Text.Length > 0) SearchBox.Text = "";
            else Close();
            e.Handled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
