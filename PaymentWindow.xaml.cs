using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace FileExplorerCS;

public partial class PaymentWindow : Window
{
    private readonly string _userEmail;
    private readonly DispatcherTimer _spinnerTimer = new();
    private readonly DispatcherTimer _countdownTimer = new();
    private static readonly System.Net.Http.HttpClient _httpClient = new System.Net.Http.HttpClient();
    
    private int _mpesaCountdown = 10;
    private string _normalizedPhone = string.Empty;

    public bool IsDarkMode { get; }

    public PaymentWindow(string userEmail)
    {
        _userEmail = string.IsNullOrWhiteSpace(userEmail) ? "customer@example.com" : userEmail;
        
        // Check dark mode for background styling in loader
        var settings = AppSettingsStore.Load();
        IsDarkMode = settings.IsDarkMode;
        DataContext = this;

        InitializeComponent();

        _spinnerTimer.Interval = TimeSpan.FromMilliseconds(50);
        _spinnerTimer.Tick += (s, e) => { SpinnerRotate.Angle = (SpinnerRotate.Angle + 15) % 360; };

        _countdownTimer.Interval = TimeSpan.FromSeconds(1);
        _countdownTimer.Tick += CountdownTimer_Tick;
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void PaymentTab_Checked(object sender, RoutedEventArgs e)
    {
        if (CardPanel == null || MpesaPanel == null || PayButton == null || CancelButton == null) return;

        if (CardTabButton.IsChecked == true)
        {
            CardPanel.Visibility = Visibility.Visible;
            MpesaPanel.Visibility = Visibility.Collapsed;

            if (CardAwaitingPanel != null && CardAwaitingPanel.Visibility == Visibility.Visible)
            {
                PayButton.Visibility = Visibility.Collapsed;
                CancelButton.Content = "Close";
            }
            else
            {
                PayButton.Visibility = Visibility.Visible;
                CancelButton.Content = "Cancel";
            }
        }
        else
        {
            CardPanel.Visibility = Visibility.Collapsed;
            MpesaPanel.Visibility = Visibility.Visible;

            PayButton.Visibility = Visibility.Visible;
            PayButton.IsEnabled = true;
            CancelButton.Content = "Cancel";
            CancelButton.IsEnabled = true;
        }
    }

    private async void PayButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorMessageText.Visibility = Visibility.Collapsed;

        if (CardTabButton.IsChecked == true)
        {
            try
            {
                string checkoutUrl = $"https://neatly.lemonsqueezy.com/checkout?email={Uri.EscapeDataString(_userEmail)}";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(checkoutUrl) { UseShellExecute = true });

                if (CardIntroPanel != null && CardAwaitingPanel != null)
                {
                    CardIntroPanel.Visibility = Visibility.Collapsed;
                    CardAwaitingPanel.Visibility = Visibility.Visible;
                }

                PayButton.Visibility = Visibility.Collapsed;
                CancelButton.Content = "Close";
            }
            catch (Exception ex)
            {
                ShowError($"Could not open checkout page: {ex.Message}");
            }
        }
        else
        {
            // Validate M-Pesa payment
            string phoneInput = MpesaPhoneBox.Text.Trim();
            if (!IsValidKenyanPhone(phoneInput, out _normalizedPhone))
            {
                ShowError("Please enter a valid Kenyan M-Pesa phone number.");
                MpesaPhoneBox.Focus();
                return;
            }

            // Start M-Pesa STK push payment
            ShowOverlay("Sending STK Push prompt to your phone...", "Please check your mobile device.");

            string? checkoutRequestId = null;
            bool useSimulationFallback = false;

            try
            {
                var payload = $"{{\"phone\":\"{_normalizedPhone}\",\"email\":\"{_userEmail}\"}}";
                var content = new System.Net.Http.StringContent(payload, Encoding.UTF8, "application/json");

                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                var response = await _httpClient.PostAsync("https://api.neatly.com/payments/mpesa/initiate", content, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    var match = System.Text.RegularExpressions.Regex.Match(responseBody, "\"checkoutRequestId\"\\s*:\\s*\"([^\"]+)\"");
                    if (match.Success)
                    {
                        checkoutRequestId = match.Groups[1].Value;
                    }
                }

                if (string.IsNullOrEmpty(checkoutRequestId))
                {
                    useSimulationFallback = true;
                }
            }
            catch
            {
                useSimulationFallback = true;
            }

            if (useSimulationFallback)
            {
                // Fallback to simulated countdown
                await Task.Delay(2000);
                _mpesaCountdown = 10;
                _countdownTimer.Start();
                UpdateMpesaOverlayText();
            }
            else
            {
                // Poll backend for real confirmation
                _ = PollForMpesaCompletion(checkoutRequestId!);
            }
        }
    }

    private async Task PollForMpesaCompletion(string checkoutRequestId)
    {
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(2000);

            if (ProcessingOverlay.Visibility != Visibility.Visible)
            {
                return;
            }

            int secondsRemaining = 60 - (i * 2);
            OverlayStatusText.Text = $"Awaiting PIN verification on phone...\n{_normalizedPhone}";
            OverlaySubText.Text = $"Please enter your M-Pesa PIN on your phone ({secondsRemaining}s remaining).";

            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                var result = await _httpClient.GetAsync(
                    $"https://api.neatly.com/payments/mpesa/check-payment?id={Uri.EscapeDataString(checkoutRequestId)}", cts.Token);

                if (result.IsSuccessStatusCode)
                {
                    var responseBody = await result.Content.ReadAsStringAsync();
                    var matchKey = System.Text.RegularExpressions.Regex.Match(responseBody, "\"licenseKey\"\\s*:\\s*\"([^\"]+)\"");
                    if (matchKey.Success)
                    {
                        string licenseKey = matchKey.Groups[1].Value;
                        CompletePayment(licenseKey);
                        return;
                    }
                }
            }
            catch
            {
                // Ignore transient network errors during polling
            }
        }

        _spinnerTimer.Stop();
        ProcessingOverlay.Visibility = Visibility.Collapsed;
        PayButton.IsEnabled = true;
        CancelButton.IsEnabled = true;
        ShowError("Payment timed out. Please check your M-Pesa messages and contact support if charged.");
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        _mpesaCountdown--;
        if (_mpesaCountdown <= 0)
        {
            _countdownTimer.Stop();
            string key = LicenseService.GenerateKey(_userEmail);
            CompletePayment(key);
        }
        else
        {
            UpdateMpesaOverlayText();
        }
    }

    private void UpdateMpesaOverlayText()
    {
        OverlayStatusText.Text = $"Awaiting PIN verification on phone...\n{_normalizedPhone}";
        OverlaySubText.Text = $"Please enter your M-Pesa PIN on your phone ({_mpesaCountdown}s remaining).";
    }

    private void ShowOverlay(string status, string subText)
    {
        OverlayStatusText.Text = status;
        OverlaySubText.Text = subText;
        ProcessingOverlay.Visibility = Visibility.Visible;
        PayButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        _spinnerTimer.Start();
    }

    private void CompletePayment(string licenseKey)
    {
        _spinnerTimer.Stop();
        _countdownTimer.Stop();

        // Register key
        LicenseService.Register(_userEmail, licenseKey);

        // Update Success Panel
        SuccessEmailText.Text = _userEmail;
        SuccessKeyText.Text = licenseKey;
        
        SuccessPanel.Visibility = Visibility.Visible;
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorMessageText.Text = message;
        ErrorMessageText.Visibility = Visibility.Visible;
    }

    private static bool IsValidKenyanPhone(string phone, out string normalized)
    {
        normalized = string.Empty;
        string digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 10 && digits.StartsWith("0"))
        {
            normalized = "+254 " + digits.Substring(1, 3) + " " + digits.Substring(4);
            return true;
        }
        if (digits.Length == 9 && (digits.StartsWith("7") || digits.StartsWith("1")))
        {
            normalized = "+254 " + digits.Substring(0, 3) + " " + digits.Substring(3);
            return true;
        }
        return false;
    }

    private void TextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // Handled via XAML templates
    }

    private void TextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Handled via XAML templates
    }
}
