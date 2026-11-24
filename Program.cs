using Avalonia;
using ResMonNext.Monitoring;
using ResMonNext.Native;

namespace ResMonNext;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            int outIndex = Array.IndexOf(args, "--out");
            if (outIndex < 0 || outIndex + 1 >= args.Length) return SelfTest();
            // Processus élevé lancé via UAC : sa console n'est pas récupérable, on écrit le rapport dans un fichier.
            using var writer = new StreamWriter(args[outIndex + 1], false, System.Text.Encoding.UTF8);
            Console.SetOut(writer);
            try
            {
                return SelfTest();
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION " + ex);
                return 2;
            }
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>« --gpu » (ou RESMONNEXT_GPU=1) réactive le rendu accéléré par la carte graphique.</summary>
    public static bool GpuRendering { get; } =
        Environment.GetCommandLineArgs().Contains("--gpu") || Environment.GetEnvironmentVariable("RESMONNEXT_GPU") == "1";

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        // Rendu logiciel par défaut : le rendu accéléré (ANGLE/Direct3D) coûte ~110 Mo de mémoire privée et ~35 threads
        // de pilote graphique, pour une interface rafraîchie une fois par seconde (mesuré face à resmon).
        if (!GpuRendering)
            builder = builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
        return builder;
    }

    /// <summary>Diagnostic sans interface : tailles des structures natives et deux échantillons.</summary>
    private static unsafe int SelfTest()
    {
        var sizes = new (string Name, int Actual, int Expected)[]
        {
            ("SYSTEM_PROCESS_INFORMATION", sizeof(SYSTEM_PROCESS_INFORMATION), 0x100),
            ("SYSTEM_THREAD_INFORMATION", sizeof(SYSTEM_THREAD_INFORMATION), 0x50),
            ("SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION", sizeof(SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION), 48),
            ("PERFORMANCE_INFORMATION", sizeof(PERFORMANCE_INFORMATION), 104),
            ("MODULEENTRY32W", sizeof(MODULEENTRY32W), 1080),
            ("ENUM_SERVICE_STATUS_PROCESSW", sizeof(ENUM_SERVICE_STATUS_PROCESSW), 56),
            ("PDH_FMT_COUNTERVALUE_ITEM_W", sizeof(PDH_FMT_COUNTERVALUE_ITEM_W), 24),
            ("MIB_TCPROW_OWNER_PID", sizeof(MIB_TCPROW_OWNER_PID), 24),
            ("MIB_TCP6ROW_OWNER_PID", sizeof(MIB_TCP6ROW_OWNER_PID), 56),
            ("MIB_UDP6ROW_OWNER_PID", sizeof(MIB_UDP6ROW_OWNER_PID), 28),
            ("MIB_TCP6ROW", sizeof(MIB_TCP6ROW), 52),
            ("EVENT_TRACE_PROPERTIES", sizeof(EVENT_TRACE_PROPERTIES), 120),
            ("EVENT_TRACE_LOGFILEW", sizeof(EVENT_TRACE_LOGFILEW), 448),
            ("EVENT_HEADER", sizeof(EVENT_HEADER), 80),
            ("EVENT_RECORD", sizeof(EVENT_RECORD), 112),
            ("SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX", sizeof(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX), 40),
            ("THREADENTRY32", sizeof(THREADENTRY32), 28),
            ("WAITCHAIN_NODE_INFO", sizeof(WAITCHAIN_NODE_INFO), 280),
            ("TOKEN_PRIVILEGES", sizeof(TOKEN_PRIVILEGES), 16),
        };
        bool ok = true;
        foreach (var (name, actual, expected) in sizes)
        {
            Console.WriteLine($"{(actual == expected ? "OK " : "KO ")} {name,-42} {actual}/{expected}");
            ok &= actual == expected;
        }

        using var sampler = new Sampler();
        var snapshots = new List<Snapshot>();
        using var done = new ManualResetEventSlim();
        sampler.SnapshotReady += s =>
        {
            lock (snapshots) snapshots.Add(s);
            if (snapshots.Count >= 3) done.Set();
        };
        sampler.Start();
        if (!done.Wait(TimeSpan.FromSeconds(15))) Console.WriteLine("KO  délai dépassé");
        var last = snapshots[^1];
        var m = last.Memory;
        Console.WriteLine($"Processus {last.Processes.Length} (perf {last.ProcessCount}), threads {last.ThreadCount}, handles {last.HandleCount}");
        Console.WriteLine($"CPU {last.CpuTotal:0.0} % sur {last.Cores.Length} cœurs, fréquence {last.MaxFrequencyPercent:0} %");
        Console.WriteLine($"Mémoire : installée {m.Installed >> 20} Mo, totale {m.Total >> 20}, utilisée {m.InUse >> 20}, modifiée {m.Modified >> 20}, en attente {m.Standby >> 20}, libre {m.Free >> 20}, listes={m.ListsAvailable}");
        Console.WriteLine($"Validation {m.CommitTotal >> 20}/{m.CommitLimit >> 20} Mo ; défauts matériels {last.HardFaultsPerSec:0}/s");
        Console.WriteLine($"Disque {last.DiskBytesPerSec:0} o/s, actif {last.DiskActivePercent:0.0} % ; réseau {last.NetBytesPerSec:0} o/s ({last.NetUtilizationPercent:0.00} %)");
        var services = snapshots.Select(s => s.Services).LastOrDefault(s => s is not null) ?? [];
        Console.WriteLine($"Services {services.Length}, en cours {services.Count(s => s.State == Win32.SERVICE_RUNNING)}, avec groupe {services.Count(s => s.Group.Length > 0)}");
        var withNet = snapshots.LastOrDefault(s => s.Connections is not null);
        var firewall = withNet?.Listeners?.GroupBy(l => l.Firewall).Select(g => $"{g.Key} : {g.Count()}") ?? [];
        Console.WriteLine($"Pare-feu des ports d'écoute : {string.Join(" | ", firewall)}");
        var names = last.Processes.GroupBy(p => p.Key.Pid).ToDictionary(g => g.Key, g => g.First().Name);
        foreach (var group in withNet?.Listeners?.GroupBy(l => l.Firewall) ?? [])
            foreach (var l in group.Take(3))
                Console.WriteLine($"  {l.Firewall,-26} {l.Protocol} {l.Address}:{l.Port} ({names.GetValueOrDefault(l.Pid, "?")}, PID {l.Pid})");
        // Contrôles : programme inexistant (aucune règle → refusé si le pare-feu filtre) et bouclage local (jamais filtré).
        string negative = "", loopback = "";
        var probe = new Thread(() =>
        {
            negative = FirewallStatus.Evaluate(@"C:\ResMonNext-controle\inexistant.exe", 999999, null, tcp: true, null, 54321);
            loopback = FirewallStatus.Evaluate(@"C:\ResMonNext-controle\inexistant.exe", 999999, null, tcp: true, "127.0.0.1", 54321);
        });
        probe.Start();
        probe.Join();
        Console.WriteLine($"  {FirewallStatus.RuleCount} règles entrantes actives retenues, {FirewallStatus.PackageRulesSkipped} règles d'applications empaquetées écartées (portée lisible : {FirewallStatus.PackageScopeReadable}) {FirewallStatus.LastError}");
        Console.WriteLine($"  contrôle programme inexistant TCP 54321 : {negative} ; sur 127.0.0.1 : {loopback}");
        var why = new List<string>();
        var explain = new Thread(() => why.AddRange(FirewallStatus.DescribeMatching(@"C:\ResMonNext-controle\inexistant.exe", 999999, true, 54321)));
        explain.Start();
        explain.Join();
        foreach (var line in why.Take(10)) Console.WriteLine($"    règle appliquée : {line}");
        Console.WriteLine($"Réseau : {withNet?.Connections?.Length} connexions TCP, {withNet?.Listeners?.Length} ports d'écoute ; ex. {string.Join(" | ", withNet?.Connections?.Take(3).Select(c => $"{c.Pid} {c.LocalAddress}:{c.LocalPort} -> {c.RemoteAddress}:{c.RemotePort} {c.State}") ?? [])}");
        Console.WriteLine($"Volumes : {string.Join(" | ", snapshots.LastOrDefault(s => s.Volumes is not null)?.Volumes?.Select(v => $"{v.Name} {v.PhysicalDisk} actif {v.ActivePercent:0}% libre {v.FreeBytes >> 30}/{v.TotalBytes >> 30} Go file {v.QueueLength:0.00}") ?? [])}");
        Console.WriteLine("Top E/S :");
        foreach (var p in last.Processes.OrderByDescending(p => p.IoReadPerSec + p.IoWritePerSec).Take(3))
            Console.WriteLine($"  {p.Name,-28} {p.Key.Pid,6}  lecture {p.IoReadPerSec,12:N0} o/s  écriture {p.IoWritePerSec,12:N0} o/s");
        Console.WriteLine("Top CPU :");
        foreach (var p in last.Processes.OrderByDescending(p => p.Cpu).Take(5))
            Console.WriteLine($"  {p.Name,-28} {p.Key.Pid,6}  cpu {p.Cpu,5:0.0}  moy {p.CpuAverage,5:0.00}  ws {p.WorkingSet >> 10,9:N0} Ko  priv {p.PrivateWorkingSet >> 10,9:N0}  commit {p.Commit >> 10,9:N0}  thr {p.Threads,4}  {p.State}  « {p.Description} »");
        var self = last.Processes.FirstOrDefault(p => p.Key.Pid == Environment.ProcessId);
        Console.WriteLine($"Ce processus : {self?.Name} chemin={self?.Path}");
        var modules = ModuleReader.Read(Environment.ProcessId, "self");
        Console.WriteLine($"Modules de ce processus : {modules.Count} (ex. {string.Join(", ", modules.Take(3).Select(x => x.Name + " " + x.Version))})");
        Console.WriteLine($"Administrateur : {SystemActions.IsElevated} ; {sampler.EtwStatus}");
        var handles = HandleSearch.Search(new HashSet<int> { Environment.ProcessId }, "", _ => "self", 50, CancellationToken.None);
        Console.WriteLine($"Handles nommés de ce processus : {handles.Count} (ex. {string.Join(" | ", handles.Take(4).Select(h => h.Type + " " + h.Name))})");
        var chain = WaitChain.Analyze(Environment.ProcessId, _ => "?");
        Console.WriteLine($"Chaîne d'attente de ce processus : {chain.ThreadsAnalyzed} threads analysés, {chain.Lines.Count} attentes externes");

        if (sampler.EtwActive)
        {
            // Activité disque et réseau générée exprès pendant que la trace tourne.
            var etwSnapshots = new List<Snapshot>();
            sampler.SnapshotReady += s =>
            {
                lock (etwSnapshots) etwSnapshots.Add(s);
            };
            string temp = Path.Combine(Path.GetTempPath(), "resmonnext-selftest.bin");
            var payload = new byte[1024 * 1024];
            Random.Shared.NextBytes(payload);
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                for (int i = 0; i < 24; i++) file.Write(payload);
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(() =>
            {
                using var client = listener.AcceptTcpClient();
                using var stream = client.GetStream();
                var buffer = new byte[65536];
                while (stream.Read(buffer) > 0) { }
            });
            using (var tcp = new System.Net.Sockets.TcpClient())
            {
                tcp.Connect(System.Net.IPAddress.Loopback, port);
                using var stream = tcp.GetStream();
                for (int i = 0; i < 16; i++) stream.Write(payload);
                Thread.Sleep(4000);
            }
            server.Wait(3000);
            listener.Stop();
            File.Delete(temp);
            Thread.Sleep(1500);

            List<Snapshot> etwList;
            lock (etwSnapshots) etwList = etwSnapshots.ToList();
            var files = etwList.Where(s => s.Etw is not null).SelectMany(s => s.Etw!.Files).ToList();
            var addresses = etwList.Where(s => s.Etw is not null).SelectMany(s => s.Etw!.Addresses).ToList();
            var lastEtw = etwList.LastOrDefault(s => s.Etw is not null)?.Etw;
            Console.WriteLine($"ETW : {lastEtw?.Events:N0} événements, {files.Count} activités fichier, {files.Count(f => f.File.StartsWith('('))} sans nom");
            foreach (var f in files.GroupBy(f => (f.Pid, f.File)).Select(g => (g.Key.Pid, g.Key.File, Read: g.Sum(x => x.ReadPerSec), Write: g.Sum(x => x.WritePerSec), Resp: g.Max(x => x.ResponseMs), Prio: g.Last().Priority))
                         .OrderByDescending(f => f.Read + f.Write).Take(8))
                Console.WriteLine($"  disque  pid {f.Pid,6}  L {f.Read,12:N0}  É {f.Write,12:N0}  {f.Resp,7:0.0} ms  {f.Prio,-8} {f.File}");
            bool sawSelfFile = files.Any(f => f.Pid == Environment.ProcessId && f.File.Contains("resmonnext-selftest", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"  fichier de test attribué à ce processus : {sawSelfFile}");
            foreach (var a in addresses.GroupBy(a => (a.Pid, a.Address)).Select(g => (g.Key.Pid, g.Key.Address, Send: g.Sum(x => x.SendPerSec), Recv: g.Sum(x => x.ReceivePerSec)))
                         .OrderByDescending(a => a.Send + a.Recv).Take(6))
                Console.WriteLine($"  réseau  pid {a.Pid,6}  envoi {a.Send,12:N0}  réception {a.Recv,12:N0}  {a.Address}");
            Console.WriteLine($"  trafic de test (boucle locale) vu pour ce processus : {addresses.Any(a => a.Pid == Environment.ProcessId && a.SendPerSec > 0)}");
            Console.WriteLine($"  stats TCP : {lastEtw?.TcpStats.Count} connexions, retransmissions totales {lastEtw?.TcpStats.Values.Sum(v => v.Retransmits)}");
            var latencies = etwList.Where(s => s.Connections is not null).SelectMany(s => s.Connections!).Where(c => !double.IsNaN(c.LatencyMs)).ToList();
            Console.WriteLine($"  latence mesurée sur {latencies.Count} connexions (ex. {string.Join(" | ", latencies.Take(4).Select(c => $"{c.RemoteAddress}:{c.RemotePort} {c.LatencyMs:0} ms"))}) ; estats cassé={TcpLatency.IsBroken}");
        }
        Console.WriteLine(ok ? "STRUCTURES OK" : "STRUCTURES KO");
        return ok ? 0 : 1;
    }
}
