using System.Windows.Controls;

namespace ScopeDesk;

public partial class ExtremaTable : UserControl
{
    public ExtremaTable() => InitializeComponent();

    internal void ShowWindow(VoltageRange? range) => Update(WindowMin, WindowMax, range);
    internal void ShowHistory(VoltageRange? range) => Update(HistoryMin, HistoryMax, range);

    private static void Update(TextBlock minimum, TextBlock maximum, VoltageRange? range)
    {
        minimum.Text = range is { } value ? Units.Volts(value.Minimum) : "—";
        maximum.Text = range is { } upper ? Units.Volts(upper.Maximum) : "—";
    }
}
