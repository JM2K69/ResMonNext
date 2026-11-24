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
ResMonNext.exe --gpu                          # rendu accéléré par la carte graphique (logiciel par défaut)
pwsh .\build.ps1                 # exe unique autonome (~62 Mo, aucun prérequis en plus du SDK .NET 10)
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
| `ResMonNext-<version>-win-x64-aot.zip` | **recommandé** — NativeAOT : exe natif + 3 DLL Avalonia (Skia, HarfBuzz, ANGLE), démarrage le plus rapide, le plus léger ; idéal pour un déploiement |
| `ResMonNext-<version>-win-x64.exe` | fichier unique autonome (runtime .NET et DLL natives intégrés, code taillé et précompilé, non compressé), à copier n'importe où |

Chaque build exécute l'autotest des deux formats (en administrateur sur le runner, donc trace ETW comprise).

## Performances

Mesures sur un poste 16 cœurs, écran à 200 %, fenêtre sur la vue d'ensemble, rafraîchissement 1 s.

| Exe unique, sans droits admin | Démarrage | Processeur | Mémoire privée | Threads |
|---|---|---|---|---|
| Rendu accéléré (ANGLE/Direct3D), compressé — v0.2.0 | 670 ms | 0,66 % | 243 Mo | 52 |
| **Rendu logiciel, non compressé — par défaut** | **550-700 ms** | 0,83 % | **112 Mo** | **16** |

Le code .NET de l'application ne pèse que 30 à 45 Mo : l'essentiel venait du pilote graphique (~110 Mo, ~35 threads),
inutile pour une interface rafraîchie une fois par seconde, et de la décompression de l'exe en mémoire (~23 Mo).
`--gpu` réactive le rendu accéléré.

Face au Moniteur de ressources de Windows, en administrateur (trace du noyau active des deux côtés), 3 démarrages
puis 60 s de mesure après 10 s de stabilisation :

| | resmon (perfmon.exe) | ResMon Next 0.2.0 | Exe unique (rendu logiciel) | NativeAOT (rendu logiciel) |
|---|---|---|---|---|
| Fenêtre affichée | 0,55-0,61 s | 0,80-1,03 s | **0,53-0,55 s** | — ¹ |
| Fenêtre prête | 0,56-0,61 s | 0,80-1,03 s | **0,62-0,63 s** | — ¹ |
| Processeur | 0,7-0,9 % | 0,54 % | 0,8 % | **0,55 %** |
| Mémoire privée | 55-58 Mo | 278 Mo | 141-144 Mo | **129 Mo** |
| Plage de travail | 82-85 Mo | 346 Mo | 204-207 Mo | **164 Mo** |

¹ Non remesuré depuis que les compteurs et la trace du noyau démarrent en arrière-plan ; sans droits admin,
NativeAOT démarre 0,15 à 0,3 s plus vite que l'exe unique (0,44-0,56 s).

Les compteurs de performance et la trace du noyau sont initialisés sur le thread de collecte, après l'affichage :
la fenêtre apparaît aussi vite que celle de resmon, les tableaux détaillés (disque par fichier, réseau par adresse)
une à deux secondes plus tard. ResMon Next consomme autant de processeur que resmon (moins en NativeAOT) mais
reste 2,2 à 2,5 fois plus gourmand en mémoire (runtime .NET, moteur de rendu Skia).

Effet de NativeAOT, mesuré à code identique (v0.1.0, sans droits admin) :

| | Exe unique (compressé) | NativeAOT |
|---|---|---|
| Démarrage | 0,77-1,12 s | **0,39-0,50 s** |
| Processeur | 0,62 % | **0,44 %** |
| Mémoire privée | 250 Mo | **208 Mo** |
| Plage de travail | 324 Mo | **254 Mo** |

Plus de compilation à la volée : démarrage deux fois plus rapide (plus rapide que resmon) et moins de processeur
en continu. C'est pourquoi le zip NativeAOT est le format recommandé.

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
| Réseau : état du pare-feu des ports d'écoute | ✅ | règles HNetCfg.FwPolicy2 évaluées par ResMon Next (lecture seule) |
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
- État du pare-feu : l'API historique `INetFwMgr.IsPortAllowed` (probablement utilisée par resmon) répond « autorisé »
  pour tout sur Windows 11, même pour un programme inexistant. ResMon Next évalue donc lui-même les règles entrantes
  actives du profil courant (protocole, port, programme, service, blocage prioritaire, action par défaut) :
  - les règles des applications empaquetées (Store, Teams, Game Bar…) sont ignorées, car elles ne visent que leur application ;
  - les mots-clés de ports sont approximés (`RPC` = ports dynamiques 49152-65535, `RPC-EpMap` = 135…) ;
  - un port en écoute sur l'adresse de bouclage est affiché « Non filtré » ;
  - sans droits administrateur, le chemin de certains processus système est déduit de System32, sinon l'état est « Indéterminé » ;
  - les règles déployées par GPO n'ont pas pu être vérifiées (aucune sur le poste de test) : l'API COM lit le magasin local.
