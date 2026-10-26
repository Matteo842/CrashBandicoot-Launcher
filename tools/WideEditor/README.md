# Editor 16:9 (WideEditor)

Editor grafico per sistemare a mano i buchi delle bande laterali 16:9.

Avvio: doppio clic su `editor.bat` nella cartella principale (compila e apre l'editor),
oppure direttamente `tools/WideEditor/bin/Release/net10.0/WideEditor.exe`.
Il disco viene preso dalle impostazioni del launcher (`CdPath`).

## Cosa salva

`RecompOne.Runtime/WideEdits/level_NN.json` (finisce dentro il gioco alla prossima
compilazione) e una copia in `CrashBandicoot.Launcher/bin/<Release|Debug>/net10.0-windows/WideEdits/`:
il gioco aperto la rilegge entro mezzo secondo dal salvataggio, senza riavviare.

Per ogni mesh del livello:

- `triangles`: triangoli tuoi, con la texture di un poligono della stessa mesh
  (`poly`), posizioni assolute, UV nella pagina texture (V 0-127), colori (128 = neutro);
  `behind` = disegnato dietro la scena (riempie solo i buchi), altrimenti vera profondità;
- `hidden`: poligoni originali non disegnati nelle bande 16:9;
- `hiddenRepairs`: riparazioni automatiche spente (poligono + centro del triangolo).

Lo legge `FramePacing.NativeWideUserEdits.cs`. Un triangolo si vede solo quando la sua
mesh è disegnata (zona della camera + zone vicine), come nell'editor in "Come nel gioco".

## Comandi

- Tasto destro trascinato: guarda intorno (W A S D Q E per volare, Shift veloce).
- Rotella: avanti/indietro verso il mouse. Tasto centrale: sposta la vista.
- Clic sinistro: seleziona; trascinando nel vuoto: rettangolo. Shift aggiunge, Ctrl toglie.
- Frecce X/Y/Z (o quadrato centrale) sui triangoli tuoi selezionati: sposta, con aggancio ai vertici.
- Ctrl+Z / Ctrl+Y / Ctrl+S / Ctrl+D (copia) / Canc / F (inquadra) / Esc.
