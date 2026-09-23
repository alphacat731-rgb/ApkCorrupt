namespace ApkCorrupt;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new NavigationPage(new MainPage())
        {
            BarBackgroundColor = Color.FromArgb("#0B0A10"),
            BarTextColor = Colors.White
        });
    }
}
