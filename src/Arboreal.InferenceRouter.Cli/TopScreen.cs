using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Arboreal.InferenceRouter.Cli;

internal sealed record TopRow(string Id, string Kind, string Summary, string Detail);
internal sealed record TopFrame(string Caption, TopRow[] Rows);

internal static class TopScreen
{
    internal static void Run(Func<TopFrame> read, Action<string>? open = null, Action? back = null)
    {
        using var app = Application.Create();
        app.Init();
        using var window = new Window { Title = "AIR monitor" };
        using var table = new TableView { Width = Dim.Fill(), Height = Dim.Percent(65), FullRowSelect = true };
        using var detail = new Label { Y = Pos.Bottom(table), Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(table, detail);
        TopRow[] rows = [];
        TopFrame? current = null;
        TopRow? Selected()
        {
            int index = table.Value?.SelectedCell.Y ?? -1;
            return index >= 0 && index < rows.Length ? rows[index] : null;
        }
        void ShowDetail() => detail.Text = Selected()?.Detail ?? "";
        bool Draw()
        {
            var frame = read();
            if (ReferenceEquals(frame, current)) return true;
            current = frame;
            string? selectedId = Selected()?.Id;
            window.Title = frame.Caption;
            rows = frame.Rows;
            table.Table = new EnumerableTableSource<TopRow>(rows, new(StringComparer.Ordinal)
            {
                ["View"] = static r => r.Kind,
                ["Observation"] = static r => r.Summary,
            });
            int index = Array.FindIndex(rows, r => string.Equals(r.Id, selectedId, StringComparison.Ordinal));
            if (rows.Length > 0) table.SetSelection(0, Math.Max(0, index), false);
            table.Update();
            ShowDetail();
            return true;
        }
        table.ValueChanged += (_, _) => ShowDetail();
        app.Keyboard.KeyDown += (_, key) =>
        {
            if (key == Key.Q || key == Key.C.WithCtrl) app.RequestStop();
            else if (key == Key.Enter && Selected() is { } row) open?.Invoke(row.Id);
            else if (key == Key.Esc && back is not null) back();
            else return;
            key.Handled = true;
        };
        Draw();
        app.AddTimeout(TimeSpan.FromMilliseconds(200), Draw);
        app.Run(window);
    }
}
