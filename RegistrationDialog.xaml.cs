using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FileExplorerCS;

public partial class RegistrationDialog : Window
{
    private readonly bool _isExpiredMode;

    public RegistrationDialog(bool isExpiredMode)
    {
        InitializeComponent();
        _isExpiredMode = isExpiredMode;
        Loaded += RegistrationDialog_Loaded;
    }

    private void RegistrationDialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isExpiredMode)
        {
            // Expired mode style changes
            TrialIcon.Text = "\uF13C"; // Warning/error icon
            TrialIcon.Foreground = (System.Windows.Media.Brush)FindResource("RedAccentBrush");
            StatusTitleText.Text = "Trial Period Expired";

            if (LicenseService.IsClockRollbackDetected)
            {
                StatusDetailText.Text = "System clock rollback detected. Launch blocked.";
            }
            else
            {
                StatusDetailText.Text = "Your 14-day trial has ended. Activate to continue.";
            }

            CancelButton.Content = "Exit Application";
            CloseButton.Visibility = Visibility.Collapsed; // Force register or exit
        }
        else
        {
            // Active trial mode style changes
            TrialIcon.Text = "\uE99A"; // Information/Alert icon
            TrialIcon.Foreground = (System.Windows.Media.Brush)FindResource("AmberAccentBrush");
            StatusTitleText.Text = "Trial Mode Active";
            StatusDetailText.Text = $"{LicenseService.DaysRemaining} days remaining on your trial.";
            CancelButton.Content = "Continue Trial";
            CloseButton.Visibility = Visibility.Visible;
        }

#if !DEBUG
        HelperButton.Visibility = Visibility.Collapsed;
#endif

        EmailTextBox.Focus();
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
        if (_isExpiredMode)
        {
            System.Windows.Application.Current.Shutdown();
        }
        else
        {
            DialogResult = false;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExpiredMode)
        {
            System.Windows.Application.Current.Shutdown();
        }
        else
        {
            DialogResult = false;
        }
    }

    private void RegisterButton_Click(object sender, RoutedEventArgs e)
    {
        string email = EmailTextBox.Text.Trim();
        string key = KeyTextBox.Text.Trim();

        ErrorMessageText.Visibility = Visibility.Collapsed;

        if (string.IsNullOrEmpty(email))
        {
            ShowError("Please enter your license email.");
            EmailTextBox.Focus();
            return;
        }

        if (string.IsNullOrEmpty(key))
        {
            ShowError("Please enter your license key.");
            KeyTextBox.Focus();
            return;
        }

        bool success = LicenseService.Register(email, key);
        if (success)
        {
            System.Windows.MessageBox.Show("Activation successful! Thank you for purchasing FileExplorerCS.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        else
        {
            ShowError("Invalid email or license key. Please check your inputs.");
        }
    }

    private void HelperButton_Click(object sender, RoutedEventArgs e)
    {
        string email = EmailTextBox.Text.Trim();
        if (string.IsNullOrEmpty(email))
        {
            email = "test@example.com";
            EmailTextBox.Text = email;
        }

        string key = LicenseService.GenerateKey(email);
        KeyTextBox.Text = key;
        
        ErrorMessageText.Visibility = Visibility.Collapsed;
    }

    private void BuyButton_Click(object sender, RoutedEventArgs e)
    {
        string email = EmailTextBox.Text.Trim();
        if (string.IsNullOrEmpty(email))
        {
            email = "test@example.com";
        }

        var payWin = new PaymentWindow(email)
        {
            Owner = this
        };
        if (payWin.ShowDialog() == true)
        {
            DialogResult = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorMessageText.Text = message;
        ErrorMessageText.Visibility = Visibility.Visible;
    }

    private void TextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // Handled via control templates
    }

    private void TextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Handled via control templates
    }
}
