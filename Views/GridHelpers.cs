using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using ResMonNext.ViewModels;

namespace ResMonNext.Views;

internal static class GridHelpers
{
    /// <summary>
    /// Branche le tri des colonnes sur la <see cref="RowTable{T}"/> (tri « vivant » qui suit les valeurs
    /// à chaque rafraîchissement) et affiche la flèche de tri dans l'en-tête.
    /// </summary>
    public static void AttachSorting<T>(DataGrid grid, RowTable<T> table) where T : class
    {
        grid.Sorting += (_, e) =>
        {
            e.Handled = true;
            table.SortBy(e.Column.SortMemberPath);
        };
        table.SortChanged += () => UpdateHeaders(grid, table);
        UpdateHeaders(grid, table);

        object? selected = null;
        table.Syncing += () => selected = grid.SelectedItem;
        table.Synced += () =>
        {
            if (selected is T row && !ReferenceEquals(grid.SelectedItem, row) && table.Items.Contains(row)) grid.SelectedItem = row;
        };
    }

    private static void UpdateHeaders<T>(DataGrid grid, RowTable<T> table) where T : class
    {
        foreach (var column in grid.Columns)
        {
            column.Tag ??= column.Header as string ?? "";
            string title = (string)column.Tag;
            column.Header = column.SortMemberPath == table.SortKey && title.Length > 0
                ? title + (table.Descending ? "  ▾" : "  ▴")
                : title;
        }
    }

    /// <summary>Le clic droit sélectionne la ligne sous le pointeur avant d'ouvrir le menu contextuel.</summary>
    public static void SelectRowOnRightClick(DataGrid grid)
    {
        grid.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) return;
            if (e.Source is Visual visual && visual.FindAncestorOfType<DataGridRow>(includeSelf: true) is { } row)
                grid.SelectedItem = row.DataContext;
        }, RoutingStrategies.Tunnel);
    }
}

/// <summary>Boîtes de dialogue minimalistes construites en code (aucune réflexion, compatible AOT).</summary>
internal static class Dialogs
{
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string confirmText)
    {
        var confirm = new Button { Content = confirmText, MinWidth = 110, HorizontalContentAlignment = HorizontalAlignment.Center };
        confirm.Classes.Add("accent");
        var cancel = new Button { Content = "Annuler", MinWidth = 110, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        var dialog = CreateWindow(title, message, cancel, confirm);
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(owner);
    }

    public static async Task ShowErrorAsync(Window owner, string title, string message)
    {
        var ok = new Button { Content = "OK", MinWidth = 110, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        var dialog = CreateWindow(title, message, ok);
        ok.Click += (_, _) => dialog.Close(false);
        await dialog.ShowDialog<bool>(owner);
    }

    private static Window CreateWindow(string title, string message, params Button[] buttons)
    {
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in buttons) buttonRow.Children.Add(button);
        return new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24, 20),
                Spacing = 20,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    buttonRow,
                },
            },
        };
    }
}
