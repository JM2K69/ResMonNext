using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ResMonNext.Monitoring;
using ResMonNext.ViewModels;

namespace ResMonNext.Views;

public partial class MainWindow : Window
{
    private readonly Control[] _pages;
    private bool _gridsAttached;

    public MainWindow()
    {
        InitializeComponent();
        _pages = [PageOverview, PageCpu, PageMemory, PageDisk, PageNetwork];
        Nav.SelectionChanged += (_, _) => ShowPage(Nav.SelectedIndex);

        // « ResMonNext.exe --page 2 » ouvre directement un onglet (0 = vue d'ensemble … 4 = réseau).
        var args = Environment.GetCommandLineArgs();
        int pageArg = Array.IndexOf(args, "--page");
        if (pageArg >= 0 && pageArg + 1 < args.Length && int.TryParse(args[pageArg + 1], out int startPage))
            Nav.SelectedIndex = Math.Clamp(startPage, 0, _pages.Length - 1);

        // « --capture-all <dossier> » : l'application capture elle-même ses onglets puis se ferme (tests, documentation).
        int captureArg = Array.IndexOf(args, "--capture-all");
        if (captureArg >= 0 && captureArg + 1 < args.Length)
        {
            string folder = args[captureArg + 1];
            Opened += async (_, _) => await CaptureAllAsync(folder);
        }

        Opened += (_, _) =>
        {
            // Sans Mica (Windows 10), on remplace le fond transparent par un fond uni.
            if (ActualTransparencyLevel != WindowTransparencyLevel.Mica &&
                this.TryFindResource("WindowFallbackBrush", ActualThemeVariant, out var brush) && brush is IBrush b)
                Background = b;
        };

        DataContextChanged += (_, _) =>
        {
            if (_gridsAttached || DataContext is not MainViewModel vm) return;
            _gridsAttached = true;
            GridHelpers.AttachSorting(ServicesGrid, vm.Services);
            GridHelpers.AttachSorting(ModulesGrid, vm.Modules);
            GridHelpers.AttachSorting(ConnectionsGrid, vm.Connections);
            GridHelpers.AttachSorting(ListenersGrid, vm.Listeners);
            GridHelpers.AttachSorting(StorageGrid, vm.Storage);
            GridHelpers.AttachSorting(FilesGrid, vm.Files);
            GridHelpers.AttachSorting(AddressesGrid, vm.Addresses);
            GridHelpers.AttachSorting(HandlesGrid, vm.Handles);
            HandleSearchBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) vm.SearchHandles();
            };
            GridHelpers.SelectRowOnRightClick(ServicesGrid);
            ServicesGrid.ContextMenu = CreateServiceMenu();
        };
    }

    private async Task CaptureAllAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        if (this.TryFindResource("WindowFallbackBrush", ActualThemeVariant, out var resource) && resource is IBrush brush) Background = brush;

        void Save(string name)
        {
            double scale = RenderScaling;
            var size = new PixelSize((int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(this);
#pragma warning disable CS0618 // surcharge simple suffisante pour les captures de test
            bitmap.Save(Path.Combine(folder, name + ".png"));
#pragma warning restore CS0618
        }

        async Task Shot(int page, string name, Action? before = null)
        {
            Nav.SelectedIndex = page;
            before?.Invoke();
            await Task.Delay(2000);
            Nav.SelectedIndex = page;
            await Task.Delay(300);
            Save(name);
        }

        await Task.Delay(9000);
        await Shot(4, "reseau");
        await Shot(4, "reseau-connexions", () => NetScroll.Offset = new Vector(0, 640));
        await Shot(4, "reseau-ports", () => NetScroll.ScrollToEnd());
        await Shot(3, "disque");
        if (DataContext is MainViewModel vm) vm.CheckProcess(Environment.ProcessId);
        Nav.SelectedIndex = 1;
        await Task.Delay(5000);
        CpuScroll.ScrollToEnd();
        await Task.Delay(1500);
        Save("processeur-handles");
        Close();
    }

    private void ShowPage(int index)
    {
        for (int i = 0; i < _pages.Length; i++) _pages[i].IsVisible = i == index;
    }

    private void OnKpiPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: string tag } && int.TryParse(tag, out int index)) Nav.SelectedIndex = index;
    }

    private void OnClearFilter(object? sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.ClearFilter();

    private void OnSearchHandles(object? sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.SearchHandles();

    private async void OnRestartElevated(object? sender, RoutedEventArgs e)
    {
        if (SystemActions.RestartElevated()) Close();
        else await Dialogs.ShowErrorAsync(this, "Élévation annulée", "ResMon Next continue en mode utilisateur standard.");
    }

    private ContextMenu CreateServiceMenu()
    {
        ServiceRow? Selected() => ServicesGrid.SelectedItem as ServiceRow;

        async Task Run(string title, Func<string, string?> action)
        {
            if (Selected() is not { } service) return;
            string? error = await Task.Run(() => action(service.Name));
            if (error is not null) await Dialogs.ShowErrorAsync(this, title, error);
        }

        var start = new MenuItem { Header = "Démarrer le service" };
        var stop = new MenuItem { Header = "Arrêter le service" };
        var restart = new MenuItem { Header = "Redémarrer le service" };
        var search = new MenuItem { Header = "Rechercher en ligne" };
        start.Click += async (_, _) => await Run("Impossible de démarrer le service", SystemActions.StartService);
        stop.Click += async (_, _) => await Run("Impossible d'arrêter le service", SystemActions.StopService);
        restart.Click += async (_, _) => await Run("Impossible de redémarrer le service", SystemActions.RestartService);
        search.Click += (_, _) =>
        {
            if (Selected() is { } service) SystemActions.SearchOnline(service.Name + " service Windows");
        };

        var menu = new ContextMenu { Items = { start, stop, restart, new Separator(), search } };
        menu.Opening += (_, e) =>
        {
            if (Selected() is not { } service)
            {
                e.Cancel = true;
                return;
            }
            start.IsEnabled = !service.IsRunning;
            stop.IsEnabled = service.IsRunning;
            restart.IsEnabled = service.IsRunning;
        };
        return menu;
    }
}
