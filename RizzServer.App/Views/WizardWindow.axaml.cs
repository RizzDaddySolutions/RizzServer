using Avalonia.Controls;
using RizzServer.App.ViewModels;

namespace RizzServer.App.Views;

public partial class WizardWindow : Window
{
    public WizardWindow() => InitializeComponent();

    public WizardWindow(WizardViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += finished => Close(finished);
    }

    /// <summary>Shows the wizard modally; true when the user pressed Finish.</summary>
    public static async Task<bool> RunAsync(Window owner, WizardViewModel vm) =>
        await new WizardWindow(vm).ShowDialog<bool>(owner);
}
