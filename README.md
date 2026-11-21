# ResMon Next

Moniteur de ressources moderne pour Windows, remplaçant de `resmon.exe`.
C# + Avalonia 12 (thème Fluent, Mica, mode sombre automatique), compilable en exécutable NativeAOT.

## Lancer

```powershell
dotnet run                       # développement
dotnet run -- --page 2           # ouvre un onglet (0 vue d'ensemble … 4 réseau)
dotnet run -- --selftest         # diagnostic console : structures natives + échantillons
ResMonNext.exe --selftest --out rapport.txt   # idem en fichier (utile lancé en administrateur : teste aussi ETW)
ResMonNext.exe --capture-all dossier          # l'application capture elle-même ses onglets puis se ferme
pwsh .\build.ps1                 # exe unique autonome (~30 Mo, aucun prérequis en plus du SDK .NET 10)
pwsh .\build.ps1 -Aot            # NativeAOT (outils C++ de Visual Studio requis)
```

## Releases

Pousser un tag publie une release GitHub (workflow `.github/workflows/release.yml`, runner Windows) :

```powershell
git tag v0.2.0
git push origin v0.2.0
```

| Fichier | Contenu |
|---|---|
| `ResMonNext-<version>-win-x64.exe` | fichier unique autonome : runtime .NET et DLL natives intégrés, à copier n'importe où |
| `ResMonNext-<version>-win-x64-aot.zip` | NativeAOT : exe natif + 3 DLL Avalonia (Skia, HarfBuzz, ANGLE), démarrage le plus rapide |

Chaque build exécute l'autotest des deux exécutables (en administrateur sur le runner, donc trace ETW comprise).

## Couverture de resmon

| resmon | ResMon Next | Source |
|---|---|---|
| Vue d'ensemble : CPU, disque, réseau, mémoire | ✅ cartes + graphes | NtQuerySystemInformation, compteurs PDH |
| Processeur : processus, threads, CPU, moyenne 60 s, état (suspendu / ne répond pas) | ✅ | SystemProcessInformation, IsHungAppWindow |
| Processeur : graphes par cœur, fréquence max. | ✅ | SystemProcessorPerformanceInformation, PDH |
| Services (PID, état, groupe svchost) + démarrer / arrêter / redémarrer | ✅ | Gestionnaire de services |
| Modules associés | ✅ | Toolhelp32 |
| Handles associés (processus cochés ou recherche par nom) | ✅ (tous les processus en admin) | SystemExtendedHandleInformation, NtQueryObject |
| Mémoire : défauts matériels/s, validation, plage de travail, partageable, privée | ✅ | SystemProcessInformation |
| Mémoire physique (réservée, utilisée, modifiée, en attente, libre) | ✅ | PDH Memory |
| Disque : E/S par processus (lecture, écriture, total) | ✅ toutes E/S du processus | compteurs d'E/S du noyau |
| Disque : stockage par volume (temps actif, espace, file d'attente) | ✅ | PDH LogicalDisk, DriveInfo |
| Disque : activité réelle par processus et par fichier, priorité d'E/S, temps de réponse | ✅ en admin | trace ETW du noyau (DiskIo, FileIo, Thread) |
| Réseau : processus, connexions TCP, ports d'écoute TCP/UDP | ✅ | GetExtendedTcpTable / UdpTable |
| Réseau : octets envoyés/reçus par processus et par adresse, perte de paquets | ✅ en admin | trace ETW du noyau (TcpIp, UdpIp) |
| Réseau : latence par connexion | ✅ en admin | statistiques TCP étendues (ESTATS) |
| Réseau : état du pare-feu des ports d'écoute | ⏳ phase 3 | INetFwPolicy2 (COM) |
| Terminer processus / arborescence, suspendre, reprendre, rechercher en ligne | ✅ | TerminateProcess, NtSuspendProcess |
| Analyser la chaîne d'attente | ✅ | Wait Chain Traversal |
| Filtre par cases à cocher partagé entre onglets | ✅ | — |

En plus de resmon : recherche instantanée, pause, tri « vivant », ouverture de l'emplacement du fichier,
démarrage sans UAC avec bouton « Relancer en admin ».

## Architecture

- `Native/Win32.cs` — interop `[LibraryImport]` (généré à la compilation, sans réflexion → compatible AOT).
- `Monitoring/` — collecteur sur son propre thread ; publie un instantané immuable par seconde.
- `ViewModels/` — lignes stables mises à jour par différences (pas de reconstruction des grilles).
- `Controls/LiveGraph.cs` — graphes dessinés directement, sans bibliothèque de graphiques.
- `Views/` — XAML Avalonia avec liaisons compilées.

## Mode administrateur

Au démarrage élevé, ResMon Next ouvre une session ETW privée « ResMonNext Kernel Trace » (mode *system logger*,
Windows 8+ : elle ne gêne ni resmon ni Process Monitor ; repli sur « NT Kernel Logger »), active SeDebugPrivilege
dans son propre processus et la collecte ESTATS des connexions TCP. La session est arrêtée à la fermeture ; une session
orpheline (plantage) est arrêtée au démarrage suivant. Une seconde session brève récupère le nom des fichiers déjà
ouverts avant le démarrage (« rundown ») ; quelques fichiers peuvent rester sans nom.

## Limites connues

- Au-delà de 64 processeurs logiques, seuls ceux du groupe courant sont affichés.
- Sans droits administrateur, l'onglet Disque compte toutes les E/S du processus (fichiers, disque, réseau).
- État du pare-feu des ports d'écoute : non implémenté.
