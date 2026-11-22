using System.Windows;

namespace OLI.Client;

/// <summary>Admin sets a brand-new password for a user (never sees the old one).</summary>
public partial class PasswordResetDialog : Window
{
    public string NewPassword { get; private set; } = "";

    public PasswordResetDialog(string userName)
    {
        InitializeComponent();
        Title = $"Сброс пароля: {userName}";
        TitleText.Text = $"Новый пароль для {userName}";
        ErrorText.Text = "";
        Loaded += (_, _) => PasswordBox1.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var first = PasswordBox1.Password;
        var second = PasswordBox2.Password;

        if (first.Length < 8 || !first.Any(char.IsLetter) || !first.Any(char.IsDigit))
        {
            ErrorText.Text = "Пароль должен быть не короче 8 символов и содержать буквы и цифры.";
            return;
        }
        if (first != second)
        {
            ErrorText.Text = "Пароли не совпадают.";
            return;
        }

        NewPassword = first;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
