// mac 대응: Sources/XFinder/Views/Sheets.swift ProgressSheet (복사/이동/압축 진행률 — 닫기 불가, 취소만)
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using XFinder.Models;

namespace XFinder.Views.Sheets;

/// <summary>
/// 작업 진행률 시트 — Esc/닫기 없음, 취소 버튼은 플래그만 set.
/// 작업 완료로 AppModel이 Sheet=null 하면 스스로 닫힌다 (model.PropertyChanged 구독).
/// </summary>
public sealed class ProgressSheet : SheetWindowBase
{
    private readonly AppModel _model;
    private readonly OperationProgress _op;
    private readonly TextBlock _title;
    private readonly TextBlock _file;
    private readonly TextBlock _count;
    private readonly ProgressBar _bar;
    private readonly Button _cancel;
    private bool _allowClose;

    public ProgressSheet(Window owner, AppModel model, OperationProgress op) : base(owner)
    {
        _model = model;
        _op = op;
        Title = op.Title;
        CloseOnEscape = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        var panel = new StackPanel { Margin = new Thickness(20), Width = 360 };

        _title = SheetUi.Text(op.Title, 13.5, FontWeights.SemiBold, "TextPrimaryBrush");
        panel.Children.Add(_title);

        _bar = new ProgressBar
        {
            Minimum = 0, Maximum = 1, Height = 6,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 12, 0, 0),
        };
        _bar.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        _bar.SetResourceReference(Control.BackgroundProperty, "ControlFillBrush");
        panel.Children.Add(_bar);

        _file = SheetUi.Text("", 11, FontWeights.Normal, "TextSecondaryBrush");
        _file.TextTrimming = TextTrimming.CharacterEllipsis;
        _file.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_file);

        var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _count = SheetUi.Text("", 11, FontWeights.Normal, "TextSecondaryBrush");
        _count.VerticalAlignment = VerticalAlignment.Center;
        bottom.Children.Add(_count);

        _cancel = SheetUi.SheetButton("취소");
        _cancel.Click += (_, _) =>
        {
            _op.IsCancelled = true;   // 작업 루프가 폴링해 중단 — 시트는 작업 쪽이 닫는다
            _cancel.IsEnabled = false;
            _cancel.Content = "취소 중…";
        };
        Grid.SetColumn(_cancel, 1);
        bottom.Children.Add(_cancel);
        panel.Children.Add(bottom);

        SetSheetContent(panel);
        UpdateUi();

        _model.PropertyChanged += OnModelChanged;
        _op.PropertyChanged += OnOpChanged;
        Closing += (_, e) => { if (!_allowClose) e.Cancel = true; };   // Alt+F4 등 사용자 닫기 차단
        Closed += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _op.PropertyChanged -= OnOpChanged;
        };
        // 다이얼로그가 열리기 전에 작업이 이미 끝났으면 즉시 닫기
        Loaded += (_, _) => { if (!IsMySheet()) CloseNow(); };
    }

    private bool IsMySheet()
        => _model.Sheet is AppSheet.Progress p && ReferenceEquals(p.Op, _op);

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppModel.Sheet)) return;
        if (IsMySheet()) return;
        if (Dispatcher.CheckAccess()) CloseNow();
        else Dispatcher.BeginInvoke(CloseNow);
    }

    private void CloseNow()
    {
        _allowClose = true;
        Close();
    }

    private void OnOpChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess()) UpdateUi();
        else Dispatcher.BeginInvoke(UpdateUi);   // 압축 등 일부 갱신은 작업 스레드에서 옴
    }

    private void UpdateUi()
    {
        _title.Text = _op.Title;
        _bar.Value = _op.Fraction;
        _file.Text = _op.CurrentFile;
        _count.Text = $"{_op.CompletedUnits:N0} / {_op.TotalUnits:N0}";
    }
}
