using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Friday.Desktop.Panels;

public partial class OutputPanel : Window
{
    private readonly CancellationTokenSource _lifetime = new();

    private bool _busy;
    private bool _closed;

    public Func<string, CancellationToken, Task<string>>? ReplyAsync
    {
        get;
        set;
    }

    public OutputPanel()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Rect area = SystemParameters.WorkArea;

        Width = Math.Min(440, area.Width - 24);
        Height = Math.Min(600, area.Height - 40);

        Left = area.Right - Width - 12;
        Top = area.Top + (area.Height - Height) / 2;

        MessageInput.Focus();
    }

    public void SetOutput(string title, string content, bool isCode = false)
    {
        OutputTitle.Text = title;
        OutputContent.Text = content;

        OutputContent.FontFamily = new FontFamily(
            isCode ? "Consolas" : "Segoe UI");

        OutputContent.TextWrapping = isCode
            ? TextWrapping.NoWrap
            : TextWrapping.Wrap;

        OutputContent.HorizontalScrollBarVisibility = isCode
            ? System.Windows.Controls.ScrollBarVisibility.Auto
            : System.Windows.Controls.ScrollBarVisibility.Disabled;

        StatusLabel.Text = "";
        OutputContent.ScrollToHome();
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await SendMessageAsync();
    }

    private async void MessageInput_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key == Key.Enter &&
            Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await SendMessageAsync();
        }
    }

    private async Task SendMessageAsync()
    {
        if (_busy || _closed)
        {
            return;
        }

        string message = MessageInput.Text.Trim();

        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var reply = ReplyAsync;

        if (reply is null)
        {
            StatusLabel.Text = "Backend is not connected.";
            return;
        }

        _busy = true;
        SendButton.IsEnabled = false;
        MessageInput.IsReadOnly = true;
        StatusLabel.Text = "Thinking…";

        try
        {
            string response = await reply(message, _lifetime.Token);

            if (_closed)
            {
                return;
            }

            SetOutput("FRIDAY", response);
            MessageInput.Clear();
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                StatusLabel.Text = "Request timed out or was cancelled.";
            }
        }
        catch (HttpRequestException)
        {
            if (!_closed)
            {
                StatusLabel.Text = "Cannot reach the AI service.";
            }
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                StatusLabel.Text = exception.Message;
            }
        }
        finally
        {
            _busy = false;

            if (!_closed)
            {
                SendButton.IsEnabled = true;
                MessageInput.IsReadOnly = false;
                MessageInput.Focus();
            }
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(OutputContent.Text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(OutputContent.Text);
            StatusLabel.Text = "Copied";
        }
        catch (COMException)
        {
            StatusLabel.Text = "Clipboard busy. Try again.";
        }
    }

    private void Header_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _lifetime.Cancel();

        if (!_busy)
        {
            _lifetime.Dispose();
        }

        base.OnClosed(e);
    }
}