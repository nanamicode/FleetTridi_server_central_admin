using System.Diagnostics;
using System.Windows.Forms;

ApplicationConfiguration.Initialize();

var baseDir = AppContext.BaseDirectory;
var serverPath = Path.Combine(baseDir, "FleetTridi.Server.exe");
var adminPath = Path.Combine(baseDir, "FleetTridi.Admin.exe");
var dataDir = Path.Combine(baseDir, "data");

const string localUrl = "http://127.0.0.1:8787";
const string localUser = "nanamicode";
const string localPassword = "veralucia12";

try
{
    if (!File.Exists(serverPath))
        throw new FileNotFoundException("FleetTridi.Server.exe não foi encontrado ao lado do FleetTridi.Central.exe.", serverPath);
    if (!File.Exists(adminPath))
        throw new FileNotFoundException("FleetTridi.Admin.exe não foi encontrado ao lado do FleetTridi.Central.exe.", adminPath);

    Directory.CreateDirectory(dataDir);

    if (Process.GetProcessesByName("FleetTridi.Server").Length == 0)
    {
        var server = new ProcessStartInfo(serverPath)
        {
            WorkingDirectory = baseDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        server.Environment["FLEETTRIDI_DEV_MODE"] = "1";
        server.Environment["FLEETTRIDI_ADMIN_USER"] = localUser;
        server.Environment["FLEETTRIDI_ADMIN_PASSWORD"] = localPassword;
        server.Environment["FLEETTRIDI_DATA_DIR"] = dataDir;
        server.Environment["FLEETTRIDI_URLS"] = localUrl;
        Process.Start(server);
        Thread.Sleep(1100);
    }

    var admin = new ProcessStartInfo(adminPath)
    {
        WorkingDirectory = baseDir,
        UseShellExecute = false
    };
    admin.Environment["FLEETTRIDI_SERVER_URL"] = localUrl;
    admin.Environment["FLEETTRIDI_ADMIN_USER"] = localUser;
    admin.Environment["FLEETTRIDI_ADMIN_PASSWORD"] = localPassword;
    admin.Environment["FLEETTRIDI_AUTO_LOGIN"] = "1";
    Process.Start(admin);
}
catch (Exception ex)
{
    MessageBox.Show(
        ex.Message,
        "FleetTridi Central",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);
}
