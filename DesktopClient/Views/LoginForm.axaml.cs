using Avalonia;
using Avalonia.Controls;

using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using DesktopClient.ViewModels;
using ReactiveUI;

namespace DesktopClient.Views
{
    public partial class LoginForm : UserControl
    {
        public LoginForm()
        {
            InitializeComponent();
            DataContext = new LoginFormViewModel();
        }
        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
