namespace MauiApp1;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        IpEntry.Text = Preferences.Get("IP", "192.168.6.6");
        PortEntry.Text = Preferences.Get("Port", "5050");
    }

    private void SaveSettings(object sender, EventArgs e)
    {
        Preferences.Set("IP", IpEntry.Text);
        Preferences.Set("Port", PortEntry.Text);
    }
}