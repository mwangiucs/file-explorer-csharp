using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace FileExplorerCS;

public partial class RibbonButton : UserControl
{
    public string Icon { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsDanger { get; set; } = false;

    public event RoutedEventHandler Click;

    public RibbonButton()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        Click?.Invoke(this, e);
    }
}
