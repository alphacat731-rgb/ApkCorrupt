namespace ApkCorrupt;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        MainPage = new NavigationPage(new MainPage())
        {
            BarBackgroundColor = Color.FromArgb("#0B0A10"),
            BarTextColor = Colors.White
        };
    }
}
