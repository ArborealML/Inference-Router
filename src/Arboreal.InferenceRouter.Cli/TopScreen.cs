using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Arboreal.InferenceRouter.Cli;

internal sealed record TopRow(string Id, string Kind, string Summary, string Detail, string? Ticket = null, string? Queue = null);
// Each frame owns its rows. The producer never mutates them after publication.
internal sealed record TopFrame(string Caption, TopRow[] Rows);

internal static class TopScreen
{
    internal static void Run(Func<TopFrame> read, Action<TopRow>? open = null, Action? back = null)
    {
        using var app = Application.Create();
        app.Init();
        using var window = new Window { Title = "AIR monitor" };
        using var table = new TableView { Width = Dim.Fill(), Height = Dim.Percent(65), FullRowSelect = true };
        using var detail = new ListView { Y = Pos.Bottom(table), Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(table, detail);
        TopRow[] rows = [];
        TopFrame? current = null;
        TopRow? Selected()
        {
            int index = table.Value?.SelectedCell.Y ?? -1;
            return index >= 0 && index < rows.Length ? rows[index] : null;
        }
        bool updating = false;
        string? lastDetail = null;
        int lastWidth = 0;
        void ShowDetail()
        {
            if (updating) return;
            var text = Selected()?.Detail ?? "";
            int width = Math.Max(1, detail.Viewport.Width - 1);
            if (width == lastWidth && string.Equals(text, lastDetail, StringComparison.Ordinal)) return;
            lastWidth = width;
            lastDetail = text;
            detail.SetSource<string>(new(text.Split('\n').SelectMany(line => TextFormatter.WordWrapText(line, width))));
        }
        bool Draw()
        {
            var frame = read();
            if (ReferenceEquals(frame, current)) return true;
            current = frame;
            string? selectedId = Selected()?.Id;
            window.Title = frame.Caption;
            if (rows.AsSpan().SequenceEqual(frame.Rows)) return true;
            int column = table.Value?.SelectedCell.X ?? 0;
            int horizontal = table.ColumnOffset;
            int offset = table.RowOffset;
            int oldIndex = table.Value?.SelectedCell.Y ?? 0;
            updating = true;
            rows = frame.Rows;
            table.Table = new EnumerableTableSource<TopRow>(rows, new(StringComparer.Ordinal)
            {
                ["View"] = static r => r.Kind,
                ["Observation"] = static r => r.Summary,
            });
            int index = Array.FindIndex(rows, r => string.Equals(r.Id, selectedId, StringComparison.Ordinal));
            if (rows.Length > 0)
            {
                int selected = Math.Max(0, index);
                table.SetSelection(column, selected, false);
                table.RowOffset = Math.Clamp(offset + selected - oldIndex, 0, rows.Length - 1);
                table.ColumnOffset = horizontal;
            }
            table.Update();
            updating = false;
            ShowDetail();
            return true;
        }
        detail.ViewportChanged += (_, _) => ShowDetail();
        table.ValueChanged += (_, _) => ShowDetail();
        app.Keyboard.KeyDown += (_, key) =>
        {
            if (key == Key.Q || key == Key.C.WithCtrl) app.RequestStop();
            else if (key == Key.Enter && Selected() is { } row) open?.Invoke(row);
            else if (key == Key.Esc && back is not null) back();
            else return;
            key.Handled = true;
        };
        Draw();
        app.AddTimeout(TimeSpan.FromMilliseconds(200), Draw);
        app.Run(window);
    }
}
