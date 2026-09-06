using Microsoft.Win32;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace FleetTridi.Admin;

public partial class MainWindow : Window {
    readonly HttpClient http = new();
    string token = "";
    string? selectedId;
    public MainWindow(){ InitializeComponent(); }

    async void Login_Click(object sender, RoutedEventArgs e) {
        try {
            http.BaseAddress = new Uri(ServerBox.Text.TrimEnd('/') + "/");
            var r = await http.PostAsJsonAsync("api/login", new { username = UserBox.Text, password = PassBox.Password });
            r.EnsureSuccessStatusCode();
            token = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            http.DefaultRequestHeaders.Remove("X-Fleet-Token"); http.DefaultRequestHeaders.Add("X-Fleet-Token", token);
            await Refresh(); StatusText.Text = "Conectado";
        } catch(Exception ex){ StatusText.Text = ex.Message; }
    }
    async Task Refresh() {
        var data = await http.GetFromJsonAsync<List<DeviceRow>>("api/devices") ?? [];
        DevicesGrid.ItemsSource = data;
    }
    void DevicesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if(DevicesGrid.SelectedItem is DeviceRow d){ selectedId=d.id; SelectedLabel.Text=$"{d.name} — {d.city}/{d.site}\n{d.id}"; }
    }
    async Task Job(string type, Dictionary<string,string>? args=null) {
        if(selectedId is null) return;
        var r=await http.PostAsJsonAsync($"api/devices/{selectedId}/jobs",new{type,args=args??new Dictionary<string,string>()});
        OutputBox.Text=await r.Content.ReadAsStringAsync(); await Refresh();
    }
    async void Home_Click(object s,RoutedEventArgs e)=>await Job("keyevent",new(){{"key","3"}});
    async void Back_Click(object s,RoutedEventArgs e)=>await Job("keyevent",new(){{"key","4"}});
    async void Reboot_Click(object s,RoutedEventArgs e)=>await Job("shell",new(){{"command","reboot"}});
    async void Shell_Click(object s,RoutedEventArgs e)=>await Job("shell",new(){{"command",ShellBox.Text}});
    async void InstallApk_Click(object s,RoutedEventArgs e){
        if(selectedId is null)return; var dlg=new OpenFileDialog{Filter="APK Android|*.apk"}; if(dlg.ShowDialog()!=true)return;
        using var form=new MultipartFormDataContent(); await using var fs=File.OpenRead(dlg.FileName); form.Add(new StreamContent(fs),"file",Path.GetFileName(dlg.FileName));
        var r=await http.PostAsync($"api/devices/{selectedId}/install-apk",form); OutputBox.Text=await r.Content.ReadAsStringAsync();
    }
    async void PushFile_Click(object s,RoutedEventArgs e){
        if(selectedId is null)return; var dlg=new OpenFileDialog(); if(dlg.ShowDialog()!=true)return;
        using var form=new MultipartFormDataContent(); await using var fs=File.OpenRead(dlg.FileName); form.Add(new StreamContent(fs),"file",Path.GetFileName(dlg.FileName)); form.Add(new StringContent(TargetPathBox.Text),"target");
        var r=await http.PostAsync($"api/devices/{selectedId}/upload",form); OutputBox.Text=await r.Content.ReadAsStringAsync();
    }
    public sealed class DeviceRow { public string id{get;set;}=""; public string name{get;set;}=""; public string city{get;set;}=""; public string site{get;set;}=""; public string lastIp{get;set;}=""; public string model{get;set;}=""; public bool online{get;set;} }
}