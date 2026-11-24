using Avalonia.Controls;
using Avalonia.Interactivity;
using RizzServer.Core.Api;

namespace RizzServer.App.Views;

public partial class ConnectWindow : Window
{
    public ConnectWindow()
    {
        InitializeComponent();
        UrlBox.Text = UiSettings.Instance.LastServiceUrl is { Length: > 0 } last ? last : "http://127.0.0.1:5080";
        Opened += (_, _) => (UrlBox.Text is { Length: > 0 } && UrlBox.Text != "http://127.0.0.1:5080" ? KeyBox : UrlBox).Focus();
    }

    private async void OnConnect(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;
        if (!TryParseUrl(UrlBox.Text, out var url))
        {
            Fail("Enter the service address, for example http://192.168.1.20:5080");
            return;
        }

        ConnectButton.IsEnabled = false;
        ConnectButton.Content = "Connecting…";
        try
        {
            await AppState.Instance.ConnectRemoteAsync(url, KeyBox.Text?.Trim() ?? "");
            UiSettings.Instance.LastServiceUrl = url.ToString().TrimEnd('/');
            UiSettings.Instance.Save();
            Close(true);
        }
        catch (ServiceApiException ex) { Fail(ex.IsUnauthorized ? "The service rejected the API key." : ex.FullMessage); }
        catch (InvalidOperationException ex) { Fail(ex.Message); }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = "Connect";
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    /// <summary>Accepts "host:port" without a scheme (http is assumed).</summary>
    public static bool TryParseUrl(string? text, out Uri url)
    {
        url = null!;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (!text.Contains("://")) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")) return false;
        url = parsed;
        return true;
    }
}
