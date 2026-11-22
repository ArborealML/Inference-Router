using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Arboreal.InferenceRouter.Cli;

internal sealed record TopRow(string Id, string Kind, string Summary, string Detail, string? Ticket = null, string? Queue = null);
// Each frame owns its rows. The producer never mutates them after publication.
internal sealed record TopFrame(string Caption, TopRows Rows);

// TableView asks for visible cells by index. A feed event only replaces the small history
// suffix. Every engine and queue remains reachable without rebuilding the gauge rows.
internal sealed class TopRows(TopRow? followed, TopRow[] gauges, TopRow[] activity) : ITableSource, IReadOnlyList<TopRow>
{
    internal TopRow? Followed { get; } = followed;
    internal TopRow[] Gauges { get; } = gauges;
    internal TopRow[] Activity { get; } = activity;
    public int Count => (Followed is null ? 0 : 1) + Gauges.Length + Activity.Length;
    public int Rows => Count;
    public int Columns => 2;
    public string[] ColumnNames { get; } = ["View", "Observation"];
    public object this[int row, int col] => col == 0 ? this[row].Kind : this[row].Summary;
    public TopRow this[int index]
    {
        get
        {
            if (Followed is not null && index-- == 0) return Followed;
            return index < Gauges.Length ? Gauges[index] : Activity[index - Gauges.Length];
        }
    }
    public IEnumerator<TopRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    internal bool Matches(TopRows other) => Followed == other.Followed && ReferenceEquals(Gauges, other.Gauges)
        && Activity.AsSpan().SequenceEqual(other.Activity);
    internal int Find(string? id, int at)
    {
        if (id is null) return 0;
        if (at >= 0 && at < Count && string.Equals(this[at].Id, id, StringComparison.Ordinal)) return at;
        for (int i = 0; i < Count; i++)
            if (string.Equals(this[i].Id, id, StringComparison.Ordinal)) return i;
        return 0;
    }
}

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
        TopRows rows = new(null, [], []);
        TopFrame? current = null;
        TopRow? Selected()
        {
            int index = table.Value?.SelectedCell.Y ?? -1;
            return index >= 0 && index < rows.Count ? rows[index] : null;
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
            if (rows.Matches(frame.Rows)) return true;
            int column = table.Value?.SelectedCell.X ?? 0;
            int horizontal = table.ColumnOffset;
            int offset = table.RowOffset;
            int oldIndex = table.Value?.SelectedCell.Y ?? 0;
            updating = true;
            rows = frame.Rows;
            table.Table = rows;
            if (rows.Count > 0)
            {
                int selected = rows.Find(selectedId, oldIndex);
                table.SetSelection(column, selected, false);
                table.RowOffset = Math.Clamp(offset + selected - oldIndex, 0, rows.Count - 1);
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
