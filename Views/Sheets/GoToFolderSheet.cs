// mac 대응: Sources/XFinder/Views/Sheets.swift GoToFolderSheet (폴더로 이동, Ctrl+Shift+G)
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XFinder.Models;

namespace XFinder.Views.Sheets;

/// <summary>폴더로 이동 시트 — 현재 경로 채움 + 전체 선택, Enter=이동, Esc=취소.</summary>
public sealed class GoToFolderSheet : SheetWindowBase
{
    private readonly AppModel _model;
    private readonly TextBox _box;
    private bool _submitted;

    public GoToFolderSheet(Window owner, AppModel model) : base(owner)
    {
        _model = model;
        Title = "폴더로 이동";
        SizeToContent = SizeToContent.WidthAndHeight;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(SheetUi.Text("폴더로 이동", 13.5, FontWeights.SemiBold, "TextPrimaryBrush"));

        var initial = model.SelectedFolder == PaneTab.ComputerPath ? "" : model.SelectedFolder;
        var field = SheetUi.InputField(out _box, "경로 입력 (예: ~/Downloads)", initial, 420);
        field.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(field);

        var cancel = SheetUi.SheetButton("취소");
        cancel.Click += (_, _) => Close();
        var go = SheetUi.SheetButton("이동", prominent: true);
        go.Click += (_, _) => Submit();
        panel.Children.Add(SheetUi.ButtonRow(cancel, go));

        SetSheetContent(panel);

        KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => { _box.Focus(); _box.SelectAll(); });
    }

    private void Submit()
    {
        if (_submitted) return;
        _submitted = true;
        var path = _box.Text.Trim();
        Close();
        if (path.Length > 0) _model.GoToFolderPath(path);   // ~ 확장/존재 검증/오류는 AppModel 책임
    }
}
