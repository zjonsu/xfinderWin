// mac 대응: Sources/XFinder/Views/Sheets.swift RenameSheet (이름 변경, F2)
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XFinder.Models;

namespace XFinder.Views.Sheets;

/// <summary>이름 변경 시트 — 현재 이름 채움 + 확장자 제외 선택, Enter=변경, Esc=취소.</summary>
public sealed class RenameSheet : SheetWindowBase
{
    private readonly AppModel _model;
    private readonly FileItem _item;
    private readonly TextBox _box;
    private bool _submitted;

    public RenameSheet(Window owner, AppModel model, FileItem item) : base(owner)
    {
        _model = model;
        _item = item;
        Title = "이름 변경";
        SizeToContent = SizeToContent.WidthAndHeight;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(SheetUi.Text("이름 변경", 13.5, FontWeights.SemiBold, "TextPrimaryBrush"));

        var field = SheetUi.InputField(out _box, "이름", item.Name, 320);
        field.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(field);

        var cancel = SheetUi.SheetButton("취소");
        cancel.Click += (_, _) => Close();
        var rename = SheetUi.SheetButton("변경", prominent: true);
        rename.Click += (_, _) => Submit();
        panel.Children.Add(SheetUi.ButtonRow(cancel, rename));

        SetSheetContent(panel);

        KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
        Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _box.Focus();
            // 탐색기 관례: 확장자 제외 선택 (폴더/숨김 점 파일은 전체 선택)
            var name = _box.Text;
            var dot = _item.IsDirectory ? -1 : name.LastIndexOf('.');
            _box.Select(0, dot > 0 ? dot : name.Length);
        });
    }

    private void Submit()
    {
        if (_submitted) return;
        _submitted = true;
        var name = _box.Text;
        Close();
        _model.RenameItem(_item, name);   // 검증/오류 메시지는 AppModel 책임
    }
}
