using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TbhStatsAnalyzer;
public partial class App : Application
{
    private void ColumnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { ((TextBox)sender).Clear(); e.Handled = true; }
        else if (e.Key == Key.Enter) e.Handled = true;
    }
}
