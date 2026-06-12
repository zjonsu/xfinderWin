// mac 대응: Sources/XFinder/Views/RootView.swift 51~62행 — .sheet(item: $app.sheet) 분기 라우팅
using System.Windows;
using System.Windows.Threading;
using XFinder.Models;
using XFinder.Views.Sheets;

namespace XFinder.Views;

/// <summary>
/// AppModel.Sheet 라우팅 — MainWindow가 Sheet 변경 시 호출.
/// 각 시트를 모달 창으로 띄우고, 닫힐 때 model.Sheet = null 로 되돌린다.
/// </summary>
public static class SheetPresenter
{
    public static void Present(Window owner, AppModel model)
    {
        var sheet = model.Sheet;
        if (sheet is null) return;

        // 호출 스택에서 바로 ShowDialog 하면 안 됨 — 진행 시트는 "Sheet = …" 직후 작업을 시작하므로
        // (setter의 PropertyChanged에서 블로킹하면 작업이 영영 시작되지 않음) 다음 디스패처 루프에서 연다.
        owner.Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(model.Sheet, sheet)) return;   // 그 사이 닫혔거나 교체됨

            Window? dialog = sheet switch
            {
                AppSheet.Viewer v => new ViewerSheet(owner, v.Item),
                AppSheet.GoToFolder => new GoToFolderSheet(owner, model),
                AppSheet.NewFolder => new NewFolderSheet(owner, model),
                AppSheet.Rename r => new RenameSheet(owner, model, r.Item),
                AppSheet.Progress p => new ProgressSheet(owner, model, p.Op),
                AppSheet.About => new AboutSheet(owner),
                AppSheet.Manual => new ManualSheet(owner),
                AppSheet.AiOrganize => new AIOrganizeWindow(owner, model),   // 별도 구현 (스펙 07)
                AppSheet.Uninstall => new UninstallWindow(owner, model),     // 별도 구현 (스펙 04 §11)
                _ => null,
            };
            if (dialog is null)
            {
                model.Sheet = null;
                return;
            }

            try { dialog.ShowDialog(); }
            finally
            {
                // 작업 완료(Progress)로 이미 null/교체된 경우는 건드리지 않음
                if (ReferenceEquals(model.Sheet, sheet)) model.Sheet = null;
            }
        }, DispatcherPriority.Normal);
    }
}
