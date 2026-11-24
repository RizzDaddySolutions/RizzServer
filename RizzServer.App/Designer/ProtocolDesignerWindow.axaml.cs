using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace RizzServer.App.Designer;

public partial class ProtocolDesignerWindow : Window
{
    private readonly ProtocolDesignerViewModel _vm;

    public ProtocolDesignerWindow() : this(new ProtocolDesignerViewModel()) { }

    public ProtocolDesignerWindow(ProtocolDesignerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        WindowPlacementService.Attach(this, "Designer", restorePosition: false);

        vm.LayoutChanged += RebuildColumns;
        RebuildColumns();
        Closed += (_, _) => vm.Close();

        FramesGrid.CellPointerPressed += (_, e) =>
        {
            if (e.Column?.Tag is int offset) vm.InspectOffset = offset;
        };
    }

    public ProtocolDesignerViewModel ViewModel => _vm;

    private const double IndexWidth = 48;
    private const double ByteWidth = 46;

    /// <summary>One narrow column per byte offset; a header strip above shows each offset and its role (HDR, LEN, CHK or field name).</summary>
    private void RebuildColumns()
    {
        FramesGrid.Columns.Clear();
        HeaderStrip.Children.Clear();

        FramesGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "#",
            Binding = new Binding(nameof(FrameRow.Index)),
            Width = new DataGridLength(IndexWidth),
            IsReadOnly = true,
        });
        HeaderStrip.Children.Add(HeaderCell("#", "", IndexWidth, Brushes.Transparent));

        for (var i = 0; i < _vm.MaxLength; i++)
        {
            var offset = i;
            var role = i < _vm.ColumnRoles.Length ? _vm.ColumnRoles[i] : "";
            var brush = i < _vm.ColumnBrushes.Length ? _vm.ColumnBrushes[i] : Brushes.Transparent;
            HeaderStrip.Children.Add(HeaderCell(offset.ToString(), role.Length > 6 ? role[..6] : role, ByteWidth, brush));

            FramesGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = offset.ToString(),
                Tag = offset,
                Width = new DataGridLength(ByteWidth),
                IsReadOnly = true,
                CellTemplate = new FuncDataTemplate<FrameRow>((_, _) =>
                {
                    var text = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    text.Bind(TextBlock.TextProperty, new Binding($"Cells[{offset}]"));
                    var cell = new Border { Child = text };
                    cell.Bind(Border.BackgroundProperty, new Binding($"ColumnBrushes[{offset}]") { Source = _vm });
                    return cell;
                }),
            });
        }

        FramesPanel.Width = IndexWidth + ByteWidth * _vm.MaxLength + 2;
    }

    private static Border HeaderCell(string offset, string role, double width, IBrush background)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        stack.Children.Add(new TextBlock { Text = offset, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = role, FontSize = 9, Opacity = 0.8, HorizontalAlignment = HorizontalAlignment.Center, Height = 12 });
        return new Border { Width = width, Background = background, Child = stack };
    }

    private void OnPortPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (PortPicker.SelectedItem is string port) _vm.PortName = port;
    }

    private void OnReadingDoubleTapped(object? sender, TappedEventArgs e) => _vm.AddFieldFromReadingCommand.Execute(null);
}
