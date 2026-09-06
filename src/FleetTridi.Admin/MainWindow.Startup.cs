namespace FleetTridi.Admin;

public partial class MainWindow
{
    public void ApplyStartupConfiguration()
    {
        var server = Environment.GetEnvironmentVariable("FLEETTRIDI_SERVER_URL");
        var user = Environment.GetEnvironmentVariable("FLEETTRIDI_ADMIN_USER");
        var password = Environment.GetEnvironmentVariable("FLEETTRIDI_ADMIN_PASSWORD");

        if (!string.IsNullOrWhiteSpace(server)) ServerBox.Text = server;
        if (!string.IsNullOrWhiteSpace(user)) UserBox.Text = user;
        if (!string.IsNullOrWhiteSpace(password)) PassBox.Password = password;
    }

    public async Task TryAutoLoginFromEnvironmentAsync()
    {
        if (Environment.GetEnvironmentVariable("FLEETTRIDI_AUTO_LOGIN") != "1") return;
        if (string.IsNullOrWhiteSpace(ServerBox.Text) ||
            string.IsNullOrWhiteSpace(UserBox.Text) ||
            string.IsNullOrWhiteSpace(PassBox.Password)) return;

        Exception? last = null;

        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                await LoginAsync();
                StatusText.Text = "Central local iniciada. Confirme a URL LAN detectada antes do bootstrap.";
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(500);
            }
        }

        StatusText.Text = "A central local foi iniciada, mas o login automático falhou: " + last?.Message;
    }
}
