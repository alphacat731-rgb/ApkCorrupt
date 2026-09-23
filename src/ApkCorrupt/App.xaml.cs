namespace ApkCorrupt;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // The app only has one screen right now. Avoid wrapping it in a
        // NavigationPage: on Android/.NET 10, NavigationPage adds another
        // layer to the startup view lifecycle that we don't need.
        return new Window(new MainPage());
    }
}
