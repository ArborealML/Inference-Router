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
        using var tabs = new Tabs { Width = Dim.Fill(), Height = Dim.Percent(62) };
        using var detail = new ListView { Y = Pos.Bottom(tabs), Width = Dim.Fill(), Height = Dim.Fill(1) };
        using var help = new Label { Y = Pos.AnchorEnd(1), Width = Dim.Fill(),
            Text = "←/→ tabs  ↑/↓ rows  Enter inspect  Tab details  Esc back  q quit" };
        window.Add(tabs, detail, help);
        string[] titles = ["Engines", "Queues", "Events"];
        var pages = new View[titles.Length];
        var tables = new TableView[titles.Length];
        TopFrame frame = read();
        TopFrame? drawn = null;
        bool updating = false;
        string? lastDetail = null;
        int lastWidth = 0;
        int Page() => Math.Max(0, Array.IndexOf(pages, tabs.Value));
        int Row(int i) => tables[i].Value?.SelectedCell.Y ?? 0;
        TopRow? Selected(int i) => (tables[i].Table as TopTable)?.At(Row(i));

        void ShowDetail()
        {
            if (updating) return;
            int i = Page();
            string text = Selected(i)?.Detail ?? (i == 1 ? "No queued requests." : i == 2 ? "No observed events." : "Waiting for engine status.");
            if (i == 2) text = (frame.TicketDetail is { } ticket ? ticket + "\n" : "") + frame.Scope + "\n" + text;
            int width = Math.Max(1, detail.Viewport.Width - 1);
            if (width == lastWidth && string.Equals(text, lastDetail, StringComparison.Ordinal)) return;
            lastWidth = width;
            lastDetail = text;
            detail.SetSource<string>(new(text.Split('\n').SelectMany(line => TextFormatter.WordWrapText(line, width))));
        }
        for (int i = 0; i < titles.Length; i++)
        {
            var table = new TableView { Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true, MultiSelect = false };
            table.Style.AlwaysShowHeaders = true;
            tables[i] = table;
            pages[i] = new View { Title = titles[i], Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
            pages[i].Add(table);
            tabs.Add(pages[i]);
            table.ValueChanged += (_, _) => ShowDetail();
        }
        void SelectPage(int i)
        {
            tabs.Value = pages[i];
            tables[i].SetFocus();
            ShowDetail();
        }
        bool Draw()
        {
            frame = read();
            if (ReferenceEquals(frame, drawn)) return true;
            drawn = frame;
            window.Title = frame.Caption;
            updating = true;
            for (int i = 0; i < tables.Length; i++)
            {
                var source = i == 0 ? frame.Engines : i == 1 ? frame.Queues : frame.Events;
                var table = tables[i];
                if (ReferenceEquals(table.Table, source)) continue;
                int old = Row(i);
                int offset = table.RowOffset;
                int col = table.ColumnOffset;
                int next = i == 2 && old == 0 ? 0 : source.Locate(Selected(i)?.Id, old);
                table.Table = source;
                if (source.Rows > 0)
                {
                    table.SetSelection(0, next, false);
                    table.RowOffset = Math.Clamp(offset + next - old, 0, source.Rows - 1);
                    table.ColumnOffset = col;
                }
                table.Update();
            }
            updating = false;
            ShowDetail();
            return true;
        }
        detail.ViewportChanged += (_, _) => ShowDetail();
        tabs.ValueChanged += (_, _) => ShowDetail();
        int returnPage = 0;
        app.Keyboard.KeyDown += (_, key) =>
        {
            int i = Page();
            if (key == Key.Q || key == Key.C.WithCtrl) app.RequestStop();
            else if (key == Key.CursorLeft || key == Key.CursorRight)
                SelectPage((i + (key == Key.CursorRight ? 1 : 2)) % pages.Length);
            else if (key == Key.Tab || key == Key.Tab.WithShift)
            {
                if (detail.HasFocus) tables[i].SetFocus();
                else detail.SetFocus();
            }
            else if (key == Key.Enter && !detail.HasFocus && Selected(i) is { } row)
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
                if (detail.HasFocus) tables[i].SetFocus();
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
