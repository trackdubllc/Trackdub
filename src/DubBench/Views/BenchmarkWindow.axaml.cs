using Avalonia.Controls;
using DubBench.ViewModels;

namespace DubBench.Views;

public partial class BenchmarkWindow : Window
{
    public BenchmarkWindow()
    {
        InitializeComponent();
    }

    public BenchmarkWindow(BenchmarkWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
