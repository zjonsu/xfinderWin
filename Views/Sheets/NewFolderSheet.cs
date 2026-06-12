// mac 대응: Sources/XFinder/Views/Sheets.swift NewFolderSheet (새 폴더, Ctrl+Shift+N)
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XFinder.Models;

namespace XFinder.Views.Sheets;

/// <summary>새 폴더 시트 — Enter=생성, Esc=취소, 자동 포커스 + 전체 선택.</summary>
public sealed class NewFolderSheet : SheetWindowBase
{
    private readonly AppModel _model;
    private readonly TextBox _box;
    private bool _submitted;

    public NewFolderSheet(Window owner, AppModel model) : base(owner)
    {
        _model = model;
        Title = "새 폴더";
        SizeToContent = SizeToContent.WidthAndHeight;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(SheetUi.Text("새 폴더", 13.5, FontWeights.SemiBold, "TextPrimaryBrush"));

        var field = SheetUi.InputField(out _box, "폴더 이름", "제목 없는 폴더", 320);
        field.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(field);

        var cancel = SheetUi.SheetButton("취소");
        cancel.Click += (_, _) => Close();
        var create = SheetUi.SheetButton("생성", prominent: true);
        create.Click += (_, _) => Submit();
        panel.Children.Add(SheetUi.ButtonRow(cancel, create));

        SetSheetContent(panel);

        KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => { _box.Focus(); _box.SelectAll(); });
    }

    private void Submit()
    {
        if (_submitted) return;
        _submitted = true;
        var name = _box.Text;
        Close();
        _model.CreateFolder(name);   // 빈 이름/검증/오류 메시지는 AppModel 책임
    }
}
