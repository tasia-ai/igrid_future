# Handoff — sesja cutover + QUIC (2026-10-02/03)

## Gdzie jesteśmy

- Produkcja działa na **starym buildzie 0.9.3.0** (przywrócony z backupu
  `H:\grid\backups\programhome-bin-20261002-194326`). 31/31 regionów, QUIC
  działa w TasiaOS, LLUDP działa. **Nic nie ruszać bez potrzeby.**
- Cała praca tej sesji jest na branchu `fix/cutover-net10-daemon-and-deploy`
  (wypchnięty na `tasia`), 20 commitów. Working tree czysty.
- Port #238 zaczęty na branchu `port/upstream-pr238-phlox-grants` (1 commit WIP).

## Co udowodnione (nie zgadywać od nowa)

1. `SimQuicHost`/`SimQuicPort` w odpowiedzi logowania są puste — zwykły viewer
   nigdy nie próbuje QUIC. TasiaOS czyta reklamę z event queue.
2. QUIC w net10: listener binduje, cert ważny, endpoint reklamowany, moduł woła
   `ProcessIncomingQuicPacket` — a viewer i tak stoi. `quicready` leci z wnętrza
   ścieżki `UseCircuitCode`, więc skoro viewer czeka, połączenie nie powstało.
3. Podwójne `AssemblyLoadContext` w discovery powodowały, że `is LLUDPServerShim`
   było fałszywe. Naprawione + test regresji (`Tests/OpenSim.PluginLoadContext.Tests`).
4. `LocalConsole` przy EOF drukował `Invalid command` w pętli (136 GB, CPU 100%).
   Naprawione. Regiony odpalać przez managera albo ze stdin otwartym, nie `< nul`.
5. Upstream #238 (granty Phlox) nie da się zmergować — wymaga portowania, bo nasza
   konsolidacja zmieniła układ katalogów.

## Co dalej (kolejność)

1. Manager: regiony już przez niego wstały, sprawdzić lag + OpenSim.log.
2. Dokończyć port #238 (branch `port/upstream-pr238-phlox-grants`).
3. PR z `fix/cutover-net10-daemon-and-deploy`.
4. Moduł QUIC od nowa (natywny, per region; keepalive już w kodzie).

## Szybka weryfikacja stanu

```
31 procesow OpenSim.exe; Robust slucha na 22000 i UDP 22002
UDP 22200-22231: 31 listenerow (net9 build, porty per region 22200+22311)
git -C H:\grid\tranquillity status --porcelain   # ma byc pusto
git -C H:\grid\tranquillity log --oneline -3
```

## Ważne pliki

- `Docs/port-notes.md` — pełna dokumentacja cutoveru (669 linii)
- `Docs/quic-net10-investigation.md` — ustalenia o QUIC w net10
- `tools/deploy-region-server.ps1` — deploy z asercją parytetu wersji
- `tools/verify-deployment.ps1` — health check tylko do odczytu
- Backup binariów sprzed cutoveru: `H:\grid\backups\programhome-bin-20261002-194326`
- Moje śmieci do skasowania: bundle `tranquillity-net10*` v4-v6 w `H:\grid\staging`,
  `H:\grid\nuget-cache` (1.9 GB), backupy `programhome-bin-20261003-*` (3× ~240 MB)
