using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Arboreal.InferenceRouter.Cli;

internal sealed record TopRow(string Id, string[] Cells, string Detail, string? Ticket = null, string? Queue = null);

internal sealed class TopTable(string[] headers, TopRow[] items) : ITableSource
{
    public int Rows => items.Length;
    public int Columns => headers.Length;
    public string[] ColumnNames => headers;
    public object this[int row, int col] => items[row].Cells[col];
    internal TopRow? At(int index) => index >= 0 && index < Rows ? items[index] : null;

    internal int Locate(string? id, int previous)
    {
        if (At(previous)?.Id == id && previous >= 0 && previous < Rows) return previous;
        int found = Array.FindIndex(items, r => r.Id == id);
        return found >= 0 ? found : Math.Clamp(previous, 0, Math.Max(0, Rows - 1));
    }
}

internal sealed record TopFrame(string Caption, TopTable Engines, TopTable Queues, TopTable Events,
    string Scope, string? TicketDetail, bool Filtered);

internal static class TopScreen
{
    internal static void Run(Func<TopFrame> read, Action<TopRow> open, Action back, bool follow)
    {
        using var app = Application.Create();
        app.Init();
        using var window = new Window { Title = "AIR monitor" };
        using var table = new TableView { Y = 1, Width = Dim.Fill(), Height = Dim.Percent(60), FullRowSelect = true, MultiSelect = false };
        table.Style.AlwaysShowHeaders = true;
        using var detail = new ListView { Y = Pos.Bottom(table), Width = Dim.Fill(), Height = Dim.Fill(1) };
        using var help = new Label { Y = Pos.AnchorEnd(1), Width = Dim.Fill(),
            Text = "←/→ tabs  ↑/↓ rows  Enter inspect  Tab details  Esc back  q quit" };
        window.Add(table, detail, help);
        string[] titles = ["Engines", "Queues", "Events"];
        var labels = new Label[titles.Length];
        var positions = new (string? Id, int Row, int Top, int Column)[titles.Length];
        int page = follow ? 2 : 0;
        TopFrame frame = read();
        TopFrame? drawn = null;
        bool updating = false;
        string? lastDetail = null;
        int lastWidth = 0;
        int Row() => table.Value?.SelectedCell.Y ?? 0;
        TopRow? Selected() => (table.Table as TopTable)?.At(Row());

        void ShowDetail()
        {
            if (updating) return;
            int i = page;
            string text = Selected()?.Detail ?? (i == 1 ? "No queued requests." : i == 2 ? "No observed events." : "Waiting for engine status.");
            if (i == 2) text = (frame.TicketDetail is { } ticket ? ticket + "\n" : "") + frame.Scope + "\n" + text;
            int width = Math.Max(1, detail.Viewport.Width - 1);
            if (width == lastWidth && string.Equals(text, lastDetail, StringComparison.Ordinal)) return;
            lastWidth = width;
            lastDetail = text;
            detail.SetSource<string>(new(text.Split('\n').SelectMany(line => TextFormatter.WordWrapText(line, width))));
        }
        void RememberPosition() =>
            positions[page] = (Selected()?.Id, Row(), table.RowOffset, table.ColumnOffset);

        void RenderTable()
        {
            var source = page == 0 ? frame.Engines : page == 1 ? frame.Queues : frame.Events;
            var position = positions[page];
            updating = true;
            table.Table = source;
            if (source.Rows > 0)
            {
                int next = page == 2 && position.Row == 0 ? 0 : source.Locate(position.Id, position.Row);
                table.SetSelection(0, next, false);
                table.RowOffset = Math.Clamp(position.Top + next - position.Row, 0, source.Rows - 1);
                table.ColumnOffset = position.Column;
            }
            table.Update();
            updating = false;
            ShowDetail();
        }
        void SelectPage(int next)
        {
            RememberPosition();
            page = next;
            for (int i = 0; i < labels.Length; i++)
                labels[i].Text = i == page ? "[" + titles[i] + "]" : titles[i];
            RenderTable();
            table.SetFocus();
        }
        for (int i = 0; i < titles.Length; i++)
        {
            int target = i;
            var label = new Label { X = i * 11, Width = 11, Height = 1 };
            label.MouseEvent += (_, mouse) =>
            {
                if (!mouse.IsSingleClicked) return;
                SelectPage(target);
                mouse.Handled = true;
            };
            labels[i] = label;
            window.Add(label);
        }
        bool Draw()
        {
            frame = read();
            if (ReferenceEquals(frame, drawn)) return true;
            drawn = frame;
            window.Title = frame.Caption;
            var source = page == 0 ? frame.Engines : page == 1 ? frame.Queues : frame.Events;
            if (!ReferenceEquals(table.Table, source))
            {
                RememberPosition();
                RenderTable();
            }
            else ShowDetail();
            return true;
        }
        detail.ViewportChanged += (_, _) => ShowDetail();
        table.ValueChanged += (_, _) => ShowDetail();
        int returnPage = 0;
        app.Keyboard.KeyDown += (_, key) =>
        {
            int i = page;
            if (key == Key.Q || key == Key.C.WithCtrl) app.RequestStop();
            else if (key == Key.CursorLeft || key == Key.CursorRight)
                SelectPage((i + (key == Key.CursorRight ? 1 : 2)) % titles.Length);
            else if (key == Key.CursorUp || key == Key.CursorDown)
            {
                bool down = key == Key.CursorDown;
                if (detail.HasFocus)
                {
                    if (down) detail.MoveDown(false);
                    else detail.MoveUp(false);
                }
                else if (table.Table is { Rows: > 0 } source)
                {
                    table.SetSelection(0, Math.Clamp(Row() + (down ? 1 : -1), 0, source.Rows - 1), false);
                    table.EnsureCursorIsVisible();
                }
            }
            else if (key == Key.Tab || key == Key.Tab.WithShift)
            {
                if (detail.HasFocus) table.SetFocus();
                else detail.SetFocus();
            }
            else if (key == Key.Enter && !detail.HasFocus && Selected() is { } row)
            {
                if (row.Queue is not null || row.Ticket is not null)
                {
                    if (!frame.Filtered || row.Queue is not null) returnPage = i;
                    open(row);
                    Draw();
                    SelectPage(2);
                }
                else detail.SetFocus();
            }
            else if (key == Key.Esc)
            {
                if (detail.HasFocus) table.SetFocus();
                else if (i == 2 && frame.Filtered)
                {
                    back();
                    Draw();
                    if (!frame.Filtered) SelectPage(returnPage);
                }
            }
            else return;
            key.Handled = true;
        };
        Draw();
        SelectPage(follow ? 2 : 0);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), Draw);
        app.Run(window);
    }
}
