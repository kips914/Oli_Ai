using System.Windows;

namespace OLI.Client;

/// <summary>Per-user quota picker (64 MB … 10 GB, default 1 GB).</summary>
public partial class QuotaDialog : Window
{
    public long QuotaBytes { get; private set; }

    public QuotaDialog(string userName, long currentQuota)
    {
        InitializeComponent();
        TitleText.Text = $"Квота: {userName}";
        QuotaSlider.Value = Math.Clamp(currentQuota / 1024 / 1024, 64, 10240);
        QuotaValueText.Text = Human(QuotaBytes = (long)QuotaSlider.Value * 1024 * 1024);
        QuotaSlider.ValueChanged += (_, e) =>
            QuotaValueText.Text = Human(QuotaBytes = (long)e.NewValue * 1024 * 1024);
    }

    private static string Human(long bytes) => OLI.Workspace.TextExtractors.FormatSize(bytes);

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
