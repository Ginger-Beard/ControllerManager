using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HIDReorder.ViewModels;

namespace HIDReorder.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.Dashboard.ActivityLog.CollectionChanged += OnActivityLogChanged;
    }

    private void OnActivityLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && ActivityLogList.Items.Count > 0)
            ActivityLogList.ScrollIntoView(ActivityLogList.Items[^1]);
    }

    private void OnDeviceItemRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListViewItem item)
            item.IsSelected = true;
    }
}
