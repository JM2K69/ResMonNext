using System.Runtime.InteropServices;

namespace ResMonNext.Monitoring;

/// <summary>
/// État du pare-feu Windows pour un port d'écoute (colonne « État du pare-feu » de resmon).
///
/// L'API historique INetFwMgr.IsPortAllowed répond « autorisé » pour tout sur les Windows récents
/// (vérifié : même un programme inexistant sur un profil qui bloque l'entrant). On évalue donc nous-mêmes
/// les règles du pare-feu (HNetCfg.FwPolicy2), comme le moteur : règles entrantes activées du profil courant,
/// protocole, port, programme, service ; une règle de blocage l'emporte ; sinon action par défaut du profil.
///
/// Sécurité : les règles sont lues exclusivement par IDispatch.Invoke en DISPATCH_PROPERTYGET (lecture seule),
/// aucun appel positionnel dans les tables de méthodes ne peut modifier une règle par erreur.
/// Compatible taille réduite / NativeAOT (pas d'interop COM intégrée). Appelé depuis le thread du collecteur.
/// </summary>
internal static unsafe partial class FirewallStatus
{
    public const string Pending = "Évaluation…";
    public const string Loopback = "Non filtré (bouclage)";

    private static readonly Guid ClsidNetFwPolicy2 = new("E2B3C97F-6AE1-41AC-817A-F6F92166D7DD");
    private static readonly Guid IidIDispatch = new("00020400-0000-0000-C000-000000000046");
    private static readonly Guid IidIEnumVariant = new("00020404-0000-0000-C000-000000000046");
    private static readonly Guid IidINetFwRule3 = new("B21563FF-D696-4222-AB46-4E89B73AB34A");
    private const ushort DISPATCH_METHOD = 1, DISPATCH_PROPERTYGET = 2;
    private const int DISPID_NEWENUM = -4;
    private const uint LOCALE_USER_DEFAULT = 0x0400;
    private const ushort VT_I4 = 3, VT_BSTR = 8, VT_DISPATCH = 9, VT_BOOL = 11, VT_UNKNOWN = 13;
    private const int ProtocolAny = 256, DirectionIn = 1, ActionAllow = 1;
    private const long RulesLifetimeMs = 60_000;

    private sealed record Rule(string? Name, string? Application, string? Service, int Protocol, string? LocalPorts, string? RemoteAddresses, bool Allow,
        string? Package = null);

    private static List<Rule>? _rules;
    private static long _rulesExpire;
    private static bool _firewallDisabled, _blockAllInbound, _defaultAllow;
    private static bool _comReady;
    public static string? LastError { get; private set; }
    public static int RuleCount => _rules?.Count ?? 0;
    public static int PackageRulesSkipped { get; private set; }
    public static bool PackageScopeReadable { get; private set; }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);

    [LibraryImport("oleaut32.dll")]
    private static partial int VariantClear(VARIANT* variant);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct VARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public int intVal;
        [FieldOffset(8)] public short boolVal;
        [FieldOffset(8)] public nint pointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPPARAMS
    {
        public VARIANT* rgvarg;
        public int* rgdispidNamedArgs;
        public uint cArgs;
        public uint cNamedArgs;
    }

    // ───────────── API ─────────────

    /// <summary>Annote chaque port d'écoute avec l'état du pare-feu.</summary>
    public static ListenerSample[] Annotate(ListenerSample[] listeners, IReadOnlyDictionary<int, string?> pathByPid,
        IReadOnlyDictionary<int, HashSet<string>> servicesByPid)
    {
        RefreshRules();
        var result = new ListenerSample[listeners.Length];
        for (int i = 0; i < listeners.Length; i++)
        {
            var l = listeners[i];
            result[i] = l with
            {
                Firewall = Evaluate(pathByPid.GetValueOrDefault(l.Pid), l.Pid, servicesByPid.GetValueOrDefault(l.Pid),
                    l.Protocol == "TCP", l.RawAddress, l.Port),
            };
        }
        return result;
    }

    /// <summary>Évalue un port d'écoute (public pour l'autotest).</summary>
    internal static string Evaluate(string? path, int pid, IReadOnlySet<string>? services, bool tcp, string? rawAddress, int port)
    {
        RefreshRules();
        if (_rules is null) return "—";
        if (rawAddress is not null && (rawAddress.StartsWith("127.", StringComparison.Ordinal) || rawAddress == "::1")) return Loopback;
        if (_firewallDisabled) return "Autorisé (pare-feu désactivé)";
        if (_blockAllInbound) return "Non autorisé (tout l'entrant bloqué)";

        bool allowed = false, unrestricted = false;
        foreach (var rule in Matching(path, pid, services, tcp, port, out bool unknownProgram))
        {
            if (!rule.Allow) return "Non autorisé (règle de blocage)";
            allowed = true;
            unrestricted |= string.IsNullOrEmpty(rule.RemoteAddresses) || rule.RemoteAddresses == "*";
        }
        if (allowed) return unrestricted ? "Autorisé, non restreint" : "Autorisé, restreint";
        if (_defaultAllow) return "Autorisé (action par défaut)";
        Matching(path, pid, services, tcp, port, out bool unknown);
        return unknown ? "Indéterminé (chemin inaccessible)" : "Non autorisé";
    }

    private static List<Rule> Matching(string? path, int pid, IReadOnlySet<string>? services, bool tcp, int port, out bool unknownProgram)
    {
        unknownProgram = false;
        var result = new List<Rule>();
        int protocol = tcp ? 6 : 17;
        foreach (var rule in _rules ?? [])
        {
            if (rule.Protocol != ProtocolAny && rule.Protocol != protocol) continue;
            if (!PortMatches(rule.LocalPorts, port)) continue;
            if (!ServiceMatches(rule.Service, services)) continue;
            bool? program = ProgramMatches(rule.Application, pid, path);
            if (program is null)
            {
                unknownProgram = true;
                continue;
            }
            if (program == true) result.Add(rule);
        }
        return result;
    }

    /// <summary>Diagnostic : règles qui s'appliquent à un port (autotest).</summary>
    internal static IEnumerable<string> DescribeMatching(string? path, int pid, bool tcp, int port) =>
        Matching(path, pid, null, tcp, port, out _).Select(r =>
            $"{r.Name} [{(r.Allow ? "autoriser" : "bloquer")}, app={r.Application ?? "*"}, svc={r.Service ?? "*"}, ports={r.LocalPorts ?? "*"}, proto={r.Protocol}, pkg={r.Package ?? "-"}]");

    // ───────────── Correspondance des règles ─────────────

    /// <summary>true / false, ou null si le programme du processus est inconnu (sans droits administrateur).</summary>
    private static bool? ProgramMatches(string? application, int pid, string? path)
    {
        if (string.IsNullOrEmpty(application) || application == "*") return true;
        if (application.Equals("System", StringComparison.OrdinalIgnoreCase)) return pid == 4;
        if (path is null) return null;
        return string.Equals(Environment.ExpandEnvironmentVariables(application), path, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ServiceMatches(string? service, IReadOnlySet<string>? services)
    {
        if (string.IsNullOrEmpty(service)) return true;
        if (service == "*") return services is { Count: > 0 };
        return services is not null && services.Contains(service);
    }

    private static bool PortMatches(string? spec, int port)
    {
        if (string.IsNullOrEmpty(spec) || spec == "*") return true;
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = raw.IndexOf('-');
            if (dash > 0 && int.TryParse(raw[..dash], out int low) && int.TryParse(raw[(dash + 1)..], out int high))
            {
                if (port >= low && port <= high) return true;
                continue;
            }
            if (int.TryParse(raw, out int single))
            {
                if (single == port) return true;
                continue;
            }
            // Mots-clés du pare-feu
            bool keyword = raw.ToUpperInvariant() switch
            {
                "RPC-EPMAP" => port == 135,
                "RPC" => port >= 49152,           // ports dynamiques RPC
                "IPHTTPS" or "IPHTTPSIN" => port == 443,
                "MDNS" => port == 5353,
                "DHCP" => port is 67 or 68,
                "PLAYTODISCOVERY" => port is 1900 or 2869,
                _ => false,
            };
            if (keyword) return true;
        }
        return false;
    }

    // ───────────── Lecture des règles (COM, lecture seule) ─────────────

    private static void RefreshRules()
    {
        if (_rules is not null && Environment.TickCount64 < _rulesExpire) return;
        _rulesExpire = Environment.TickCount64 + RulesLifetimeMs;
        try
        {
            if (!_comReady)
            {
                CoInitializeEx(0, 0); // COINIT_MULTITHREADED (S_FALSE si déjà initialisé)
                _comReady = true;
            }
            Guid clsid = ClsidNetFwPolicy2, iid = IidIDispatch;
            nint policy;
            int hr = CoCreateInstance(&clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, &iid, &policy);
            if (hr < 0 || policy == 0)
            {
                LastError = $"Pare-feu inaccessible (0x{hr:X8})";
                return;
            }
            try
            {
                LoadPolicy(policy);
            }
            finally
            {
                Release(policy);
            }
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private static void LoadPolicy(nint policy)
    {
        int profiles = GetInt(policy, "CurrentProfileTypes") ?? 7;
        bool anyEnabled = false, blockAll = false, defaultAllow = false;
        foreach (int profile in new[] { 1, 2, 4 })
        {
            if ((profiles & profile) == 0) continue;
            anyEnabled |= GetBool(policy, "FirewallEnabled", profile) ?? true;
            blockAll |= GetBool(policy, "BlockAllInboundTraffic", profile) ?? false;
            defaultAllow |= GetInt(policy, "DefaultInboundAction", profile) == ActionAllow;
        }

        var rules = new List<Rule>(256);
        VARIANT rulesVar = default;
        if (GetProperty(policy, DispId(policy, "Rules"), null, &rulesVar) < 0 || rulesVar.vt != VT_DISPATCH) return;
        try
        {
            VARIANT enumVar = default;
            if (Invoke(rulesVar.pointer, DISPID_NEWENUM, DISPATCH_METHOD | DISPATCH_PROPERTYGET, null, &enumVar) < 0) return;
            try
            {
                if (enumVar.vt is not (VT_UNKNOWN or VT_DISPATCH)) return;
                Guid iidEnum = IidIEnumVariant;
                nint enumerator;
                if (QueryInterface(enumVar.pointer, &iidEnum, &enumerator) < 0) return;
                try
                {
                    ReadRules(enumerator, profiles, rules);
                }
                finally
                {
                    Release(enumerator);
                }
            }
            finally
            {
                VariantClear(&enumVar);
            }
        }
        finally
        {
            VariantClear(&rulesVar);
        }

        _rules = rules;
        _firewallDisabled = !anyEnabled;
        _blockAllInbound = blockAll;
        _defaultAllow = defaultAllow;
    }

    private static void ReadRules(nint enumerator, int profiles, List<Rule> rules)
    {
        var next = (delegate* unmanaged[Stdcall]<nint, uint, VARIANT*, uint*, int>)(*(nint**)enumerator)[3];
        int dDirection = 0, dEnabled = 0, dProfiles = 0, dAction = 0, dApplication = 0, dService = 0, dProtocol = 0, dPorts = 0, dRemote = 0, dName = 0;
        int dPackage = int.MinValue, dOwner = int.MinValue;
        bool idsReady = false;
        int skipped = 0;
        Guid iidRule3 = IidINetFwRule3;
        while (true)
        {
            VARIANT item = default;
            uint fetched = 0;
            if (next(enumerator, 1, &item, &fetched) != 0 || fetched == 0) break;
            try
            {
                if (item.vt != VT_DISPATCH || item.pointer == 0) continue;
                nint rule = item.pointer;
                if (!idsReady)
                {
                    dDirection = DispId(rule, "Direction");
                    dEnabled = DispId(rule, "Enabled");
                    dProfiles = DispId(rule, "Profiles");
                    dAction = DispId(rule, "Action");
                    dApplication = DispId(rule, "ApplicationName");
                    dService = DispId(rule, "ServiceName");
                    dProtocol = DispId(rule, "Protocol");
                    dPorts = DispId(rule, "LocalPorts");
                    dRemote = DispId(rule, "RemoteAddresses");
                    dName = DispId(rule, "Name");
                    idsReady = true;
                }
                if (ReadInt(rule, dDirection) != DirectionIn) continue;
                if (ReadBool(rule, dEnabled) != true) continue;
                if (((ReadInt(rule, dProfiles) ?? 0x7FFFFFFF) & profiles) == 0) continue;
                // Règles d'applications du Store : limitées à leur package (AppContainer), sans effet sur les programmes classiques.
                // La propriété n'existe que sur INetFwRule3, qu'il faut demander explicitement.
                nint rule3;
                if (QueryInterface(rule, &iidRule3, &rule3) >= 0 && rule3 != 0)
                {
                    try
                    {
                        if (dPackage == int.MinValue) dPackage = DispId(rule3, "LocalAppPackageId");
                        if (dOwner == int.MinValue) dOwner = DispId(rule3, "LocalUserOwner");
                        PackageScopeReadable = dPackage != int.MinValue && dOwner != int.MinValue;
                        // Règles d'applications empaquetées : un package, ou un propriétaire sans programme désigné
                        // (règles générées par les capacités réseau des applications Store/Win32 empaquetées).
                        bool packaged = !string.IsNullOrEmpty(ReadString(rule3, dPackage))
                            || (!string.IsNullOrEmpty(ReadString(rule3, dOwner)) && string.IsNullOrEmpty(ReadString(rule, dApplication)));
                        if (packaged)
                        {
                            skipped++;
                            continue;
                        }
                    }
                    finally
                    {
                        Release(rule3);
                    }
                }
                rules.Add(new Rule(
                    ReadString(rule, dName),
                    ReadString(rule, dApplication),
                    ReadString(rule, dService),
                    ReadInt(rule, dProtocol) ?? ProtocolAny,
                    ReadString(rule, dPorts),
                    ReadString(rule, dRemote),
                    ReadInt(rule, dAction) == ActionAllow));
            }
            finally
            {
                VariantClear(&item);
            }
        }
        PackageRulesSkipped = skipped;
    }

    // ───────────── Aides IDispatch (lecture seule) ─────────────

    private static int? GetInt(nint target, string name, int? argument = null) => ReadInt(target, DispId(target, name), argument);

    private static bool? GetBool(nint target, string name, int? argument = null) => ReadBool(target, DispId(target, name), argument);

    private static int? ReadInt(nint target, int dispId, int? argument = null)
    {
        VARIANT v = default;
        try
        {
            if (GetProperty(target, dispId, argument, &v) < 0) return null;
            return v.vt == VT_I4 ? v.intVal : null;
        }
        finally
        {
            VariantClear(&v);
        }
    }

    private static bool? ReadBool(nint target, int dispId, int? argument = null)
    {
        VARIANT v = default;
        try
        {
            if (GetProperty(target, dispId, argument, &v) < 0) return null;
            return v.vt == VT_BOOL ? v.boolVal != 0 : null;
        }
        finally
        {
            VariantClear(&v);
        }
    }

    private static string? ReadString(nint target, int dispId)
    {
        VARIANT v = default;
        try
        {
            if (GetProperty(target, dispId, null, &v) < 0) return null;
            return v.vt == VT_BSTR && v.pointer != 0 ? new string((char*)v.pointer) : null;
        }
        finally
        {
            VariantClear(&v);
        }
    }

    /// <summary>Lecture de propriété uniquement (DISPATCH_PROPERTYGET) : aucune écriture possible.</summary>
    private static int GetProperty(nint target, int dispId, int? argument, VARIANT* result)
    {
        if (dispId == int.MinValue) return -1;
        if (argument is null) return Invoke(target, dispId, DISPATCH_PROPERTYGET, null, result);
        VARIANT arg = default;
        arg.vt = VT_I4;
        arg.intVal = argument.Value;
        return Invoke(target, dispId, DISPATCH_PROPERTYGET, &arg, result);
    }

    private static int Invoke(nint target, int dispId, ushort flags, VARIANT* argument, VARIANT* result)
    {
        var invoke = (delegate* unmanaged[Stdcall]<nint, int, Guid*, uint, ushort, DISPPARAMS*, VARIANT*, nint, uint*, int>)(*(nint**)target)[6];
        Guid iidNull = Guid.Empty;
        DISPPARAMS parameters = default;
        if (argument is not null)
        {
            parameters.rgvarg = argument;
            parameters.cArgs = 1;
        }
        uint argError;
        return invoke(target, dispId, &iidNull, LOCALE_USER_DEFAULT, flags, &parameters, result, 0, &argError);
    }

    private static int DispId(nint target, string name)
    {
        var getIds = (delegate* unmanaged[Stdcall]<nint, Guid*, char**, uint, uint, int*, int>)(*(nint**)target)[5];
        Guid iidNull = Guid.Empty;
        int id;
        fixed (char* text = name)
        {
            char* names = text;
            return getIds(target, &iidNull, &names, 1, LOCALE_USER_DEFAULT, &id) < 0 ? int.MinValue : id;
        }
    }

    private static int QueryInterface(nint target, Guid* iid, nint* result) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)target)[0])(target, iid, result);

    private static void Release(nint target) =>
        ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)target)[2])(target);
}
