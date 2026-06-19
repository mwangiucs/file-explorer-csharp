using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace FileExplorerCS;

public partial class RibbonGroup : UserControl
{
    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public UIElementCollection Children => RootPanel.Children;

    public RibbonGroup()
    {
        InitializeComponent();
        this.Loaded += (s, e) =>
        {
            // Move any content added to the UserControl to the RootPanel
            if (Content != null && Content != RootPanel)
            {
                RootPanel.Children.Add(Content as UIElement);
                Content = null;
            }
        };
    }
}
