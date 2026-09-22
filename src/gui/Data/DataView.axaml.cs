using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using backs2.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace backs2.Views;

public partial class DataView : UserControl
{
    public DataView()
    {
        InitializeComponent();

        if (Application.Current is App app)
        {
            DataContext = app.Services.GetRequiredService<DataViewModel>();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        Dispatcher.UIThread.Post(() =>
        {
            var grid = this.FindControl<DataGrid>("TheGrid");

            if (grid == null)
            {
                Console.WriteLine("GRID NOT FOUND");
                return;
            }

            Console.WriteLine($"GRID BOUNDS: {grid.Bounds}");
            Console.WriteLine($"GRID VISIBLE: {grid.IsVisible}");
            Console.WriteLine($"COLUMNS: {grid.Columns.Count}");
            Console.WriteLine($"ITEMS: {grid.ItemsSource?.Cast<object>().Count() ?? -1}");

            var first = grid.ItemsSource?.Cast<object>().FirstOrDefault();
            Console.WriteLine($"ITEM TYPE: {first?.GetType().FullName ?? "<null>"}");

            var rows = grid.GetVisualDescendants().OfType<DataGridRow>().Count();
            Console.WriteLine($"REALIZED ROWS: {rows}");

            var headers = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().Count();
            Console.WriteLine($"REALIZED HEADERS: {headers}");

            var theme = grid.GetType().GetProperty("Theme")?.GetValue(grid);
            Console.WriteLine($"GRID THEME: {theme?.GetType().Name ?? "<null>"}");

            var template = grid.GetType().GetProperty("Template")?.GetValue(grid);
            Console.WriteLine($"GRID TEMPLATE: {template?.GetType().Name ?? "<null>"}");

            // Count children directly under the grid — the ScrollViewer the template should have produced.
            var directChildren = grid.GetVisualChildren().Select(c => c.GetType().Name).ToList();
            Console.WriteLine($"GRID VISUAL CHILDREN: [{string.Join(", ", directChildren)}]");
        });
    }
}