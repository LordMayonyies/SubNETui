using System.Text.Json;
using Sub.NETUI.Configuration;
using Sub.NETUI.Subsonic;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Sub.NETUI.UI;

internal sealed class LoginWindow : Window
{
    private readonly IApplication _app;
    private readonly HttpClient _httpClient;
    private readonly TextField _urlField;
    private readonly TextField _usernameField;
    private readonly TextField _passwordField;
    private readonly Label _errorLabel;
    private readonly Button _connectButton;
    private readonly Button _exitButton;
    private readonly object _connectionGate = new();
    private bool _connecting;
    private volatile bool _stopping;
    private CancellationTokenSource? _connectionCancellation;

    public LoginWindow(IApplication app, HttpClient httpClient)
    {
        _app = app;
        _httpClient = httpClient;
        var draft = AppSettingsLoader.ReadDraft();

        Title = "Sign in to Navidrome";
        Width = 72;
        Height = 17;
        X = Pos.Center();
        Y = Pos.Center();
        BorderStyle = LineStyle.Single;

        var urlLabel = new Label { X = 2, Y = 0, Text = "Server URL (http or https)" };
        _urlField = new TextField { X = 2, Y = 1, Width = Dim.Fill(4), Text = draft.Url };
        var usernameLabel = new Label { X = 2, Y = 3, Text = "Username" };
        _usernameField = new TextField { X = 2, Y = 4, Width = Dim.Fill(4), Text = draft.Username };
        var passwordLabel = new Label { X = 2, Y = 6, Text = "Password" };
        _passwordField = new TextField { X = 2, Y = 7, Width = Dim.Fill(4), Text = draft.Password, Secret = true };
        _errorLabel = new Label { X = 2, Y = 9, Width = Dim.Fill(4), Height = 2 };
        _connectButton = new Button { X = Pos.AnchorEnd(25), Y = 12, Text = "Connect", IsDefault = true };
        _exitButton = new Button { X = Pos.AnchorEnd(12), Y = 12, Text = "Exit" };

        _connectButton.Accepted += (_, _) => _ = ConnectAsync();
        _exitButton.Accepted += (_, _) => StopApplication();
        KeyDown += OnKeyDown;
        Add(urlLabel, _urlField, usernameLabel, _usernameField, passwordLabel, _passwordField,
            _errorLabel, _connectButton, _exitButton);
        _urlField.SetFocus();
    }

    public AppSettings? Settings { get; private set; }

    private async Task ConnectAsync()
    {
        if (_connecting)
        {
            return;
        }

        if (!AppSettingsLoader.TryCreate(
                _urlField.Text,
                _usernameField.Text,
                _passwordField.Text,
                out AppSettings? settings,
                out string? error))
        {
            _errorLabel.Text = error ?? string.Empty;
            return;
        }

        _connecting = true;
        _connectButton.Enabled = false;
        _exitButton.Enabled = false;
        _errorLabel.Text = "Connecting to Navidrome...";
        var connectionCancellation = new CancellationTokenSource();
        lock (_connectionGate)
        {
            _connectionCancellation = connectionCancellation;
        }

        try
        {
            var client = new SubsonicClient(_httpClient, settings!);
            await client.PingAsync(connectionCancellation.Token);
            AppSettingsLoader.Save(settings!);
            _app.Invoke(() =>
            {
                if (_stopping)
                {
                    return;
                }

                Settings = settings;
                _stopping = true;
                _app.RequestStop();
            });
        }
        catch (OperationCanceledException) when (_stopping)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or SubsonicException or JsonException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (!_stopping)
            {
                _app.Invoke(() => _errorLabel.Text = exception.Message);
            }
        }
        finally
        {
            lock (_connectionGate)
            {
                if (ReferenceEquals(_connectionCancellation, connectionCancellation))
                {
                    _connectionCancellation = null;
                }

                connectionCancellation.Dispose();
            }

            if (!_stopping)
            {
                _app.Invoke(() =>
                {
                    _connecting = false;
                    _connectButton.Enabled = true;
                    _exitButton.Enabled = true;
                });
            }
        }
    }

    private void OnKeyDown(object? sender, Key e)
    {
        if (e == Key.Q.WithCtrl)
        {
            e.Handled = true;
            StopApplication();
        }
    }

    private void StopApplication()
    {
        _stopping = true;
        lock (_connectionGate)
        {
            _connectionCancellation?.Cancel();
        }

        _app.RequestStop();
    }
}