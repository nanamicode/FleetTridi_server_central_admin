using System.Windows;

namespace FleetTridi.Admin;

public partial class AddTotemWindow : Window
{
    public string TotemName => NameBox.Text.Trim();
    public string City => CityBox.Text.Trim();
    public string Site => SiteBox.Text.Trim();
    public string InitialIp => IpBox.Text.Trim();

    public AddTotemWindow() => InitializeComponent();

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TotemName))
        {
            MessageBox.Show(this, "Informe um nome para o totem.");
            return;
        }
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}