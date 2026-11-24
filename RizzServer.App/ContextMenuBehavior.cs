using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace RizzServer.App;

/// <summary>
/// Right-click selects the row/node under the pointer before the context menu opens, so the menu's
/// commands act on what was clicked (list controls normally only select on a left click).
/// </summary>
public static class ContextMenuBehavior
{
    public static readonly AttachedProperty<bool> SelectOnRightClickProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("SelectOnRightClick", typeof(ContextMenuBehavior));

    public static bool GetSelectOnRightClick(Control c) => c.GetValue(SelectOnRightClickProperty);
    public static void SetSelectOnRightClick(Control c, bool value) => c.SetValue(SelectOnRightClickProperty, value);

    static ContextMenuBehavior()
    {
        SelectOnRightClickProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true) control.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
            else control.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        });
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed) return;
        var source = e.Source as Visual;

        switch (sender)
        {
            case DataGrid grid when source?.FindAncestorOfType<DataGridRow>(includeSelf: true) is { DataContext: { } item }:
                grid.SelectedItem = item;
                break;
            case TreeView tree when source?.FindAncestorOfType<TreeViewItem>(includeSelf: true) is { DataContext: { } node }:
                tree.SelectedItem = node;
                break;
        }
    }
}
