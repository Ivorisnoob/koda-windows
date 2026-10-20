namespace Koda.Pages;

public sealed partial class SettingsPage : Microsoft.UI.Xaml.Controls.Page
{
    private bool _loaded;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        CloseToTrayCheckBox.IsChecked = App.CloseToTray;
        _loaded = true;
    }

    private void OnCloseToTrayChecked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_loaded)
        {
            App.SetCloseToTray(CloseToTrayCheckBox.IsChecked == true);
        }
    }
}
