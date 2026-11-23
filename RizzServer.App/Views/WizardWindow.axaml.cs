using Avalonia.Controls;
using RizzServer.App.ViewModels;

namespace RizzServer.App.Views;

public partial class WizardWindow : Window
{
    public WizardWindow()
    {
        InitializeComponent();
        // Size is remembered; position stays centred on the main window.
        WindowPlacementService.Attach(this, "Wizard", restorePosition: false);
    }

    public WizardWindow(WizardViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += finished => Close(finished);
    }

    /// <summary>Shows the wizard modally; true when the user pressed Finish.</summary>
    public static async Task<bool> RunAsync(Window owner, WizardViewModel vm) =>
        await new WizardWindow(vm).ShowDialog<bool>(owner);
}
