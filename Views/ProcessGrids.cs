using System.Diagnostics;
using Avalonia.Controls;
using ResMonNext.Monitoring;
using ResMonNext.ViewModels;

namespace ResMonNext.Views;

public partial class ProcessCpuGrid : UserControl
{
    public ProcessCpuGrid()
    {
        InitializeComponent();
        ProcessGridSetup.Attach(this, Grid);
    }
}

public partial class ProcessMemoryGrid : UserControl
{
    public ProcessMemoryGrid()
    {
        InitializeComponent();
        ProcessGridSetup.Attach(this, Grid);
    }
}

public partial class ProcessDiskGrid : UserControl
{
    public ProcessDiskGrid()
    {
        InitializeComponent();
        ProcessGridSetup.Attach(this, Grid);
    }
}

public partial class ProcessNetworkGrid : UserControl
{
    public ProcessNetworkGrid()
    {
        InitializeComponent();
        ProcessGridSetup.Attach(this, Grid);
    }
}

/// <summary>Tri, sélection au clic droit et menu contextuel communs aux grilles de processus.</summary>
internal static class ProcessGridSetup
{
    public static void Attach(UserControl host, DataGrid grid)
    {
        GridHelpers.SelectRowOnRightClick(grid);
        bool sortingAttached = false;
        host.DataContextChanged += (_, _) =>
        {
            if (host.DataContext is ProcessTable table && !sortingAttached)
            {
                sortingAttached = true;
                GridHelpers.AttachSorting(grid, table);
                grid.ContextMenu = CreateMenu(grid, table);
            }
        };
    }

    private static ContextMenu CreateMenu(DataGrid grid, ProcessTable table)
    {
        ProcessRow? Selected() => grid.SelectedItem as ProcessRow;
        Window? Owner() => TopLevel.GetTopLevel(grid) as Window;

        var kill = new MenuItem { Header = "Terminer le processus" };
        var killTree = new MenuItem { Header = "Terminer l'arborescence du processus" };
        var suspend = new MenuItem { Header = "Suspendre le processus" };
        var resume = new MenuItem { Header = "Reprendre le processus" };
        var openLocation = new MenuItem { Header = "Ouvrir l'emplacement du fichier" };
        var search = new MenuItem { Header = "Rechercher en ligne" };
        var waitChain = new MenuItem { Header = "Analyser la chaîne d'attente…" };
        waitChain.Click += async (_, _) =>
        {
            if (Selected() is not { } row || Owner() is not { } owner) return;
            var imageOf = table.Owner.ImageLookup();
            var result = await Task.Run(() => WaitChain.Analyze(row.Pid, imageOf));
            string message = result.Lines.Count == 0
                ? $"Aucun des {result.ThreadsAnalyzed} threads de {row.Name} n'attend un autre processus."
                  + (row.State == "Ne répond pas" ? " Le blocage est interne au processus (calcul long ou attente non suivie par Windows)." : "")
                : string.Join("\n", result.Lines.Distinct().Take(12))
                  + (result.Deadlock ? "\n\nInterblocage détecté : des threads s'attendent mutuellement." : "")
                  + $"\n\nProcessus bloquant(s) : {string.Join(", ", result.BlockingProcesses.Select(b => $"{b.Image} ({b.Pid})"))}. Vous pouvez les terminer depuis la liste.";
            if (!SystemActions.IsElevated) message += "\n\nRelancez en administrateur pour analyser les processus des autres utilisateurs et des services.";
            await Dialogs.ShowErrorAsync(owner, $"Chaîne d'attente de {row.Name}", message);
        };

        kill.Click += async (_, _) =>
        {
            if (Selected() is not { } row || Owner() is not { } owner) return;
            if (!await Dialogs.ConfirmAsync(owner, $"Terminer {row.Name} ?",
                    "Si un processus ouvert est associé à un programme, celui-ci se ferme et vous perdez les données non enregistrées. " +
                    "Si vous terminez un processus système, le système peut devenir instable.", "Terminer le processus")) return;
            if (SystemActions.Terminate(row.Pid) is { } error) await Dialogs.ShowErrorAsync(owner, "Impossible de terminer le processus", error);
        };

        killTree.Click += async (_, _) =>
        {
            if (Selected() is not { } row || Owner() is not { } owner) return;
            var tree = table.Owner.GetTree(row);
            if (!await Dialogs.ConfirmAsync(owner, $"Terminer l'arborescence de {row.Name} ?",
                    $"{tree.Count} processus seront terminés : {string.Join(", ", tree.Take(8).Select(r => r.Name))}{(tree.Count > 8 ? "…" : "")}",
                    "Terminer l'arborescence")) return;
            // Les descendants d'abord, la racine en dernier.
            var errors = tree.AsEnumerable().Reverse()
                .Select(r => (r.Name, Error: SystemActions.Terminate(r.Pid)))
                .Where(r => r.Error is not null)
                .Select(r => $"{r.Name} : {r.Error}")
                .ToList();
            if (errors.Count > 0) await Dialogs.ShowErrorAsync(owner, "Certains processus n'ont pas été terminés", string.Join("\n", errors));
        };

        suspend.Click += async (_, _) =>
        {
            if (Selected() is not { } row || Owner() is not { } owner) return;
            if (!await Dialogs.ConfirmAsync(owner, $"Suspendre {row.Name} ?",
                    "Le processus cessera de s'exécuter jusqu'à sa reprise. Suspendre un processus système peut bloquer Windows.", "Suspendre")) return;
            if (SystemActions.Suspend(row.Pid) is { } error) await Dialogs.ShowErrorAsync(owner, "Impossible de suspendre le processus", error);
        };

        resume.Click += async (_, _) =>
        {
            if (Selected() is not { } row || Owner() is not { } owner) return;
            if (SystemActions.Resume(row.Pid) is { } error) await Dialogs.ShowErrorAsync(owner, "Impossible de reprendre le processus", error);
        };

        openLocation.Click += (_, _) =>
        {
            if (Selected()?.Path is { } path) Process.Start("explorer.exe", $"/select,\"{path}\"");
        };

        search.Click += (_, _) =>
        {
            if (Selected() is { } row) SystemActions.SearchOnline(row.Name);
        };

        var menu = new ContextMenu
        {
            Items =
            {
                kill, killTree, new Separator(), suspend, resume, new Separator(), waitChain, new Separator(), openLocation, search,
            },
        };
        menu.Opening += (_, e) =>
        {
            if (Selected() is not { } row)
            {
                e.Cancel = true;
                return;
            }
            suspend.IsEnabled = !row.IsSuspended;
            resume.IsEnabled = row.IsSuspended;
            openLocation.IsEnabled = row.Path is not null;
        };
        return menu;
    }
}
