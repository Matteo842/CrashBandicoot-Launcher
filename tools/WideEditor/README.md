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

## Interfaccia

Come Photoshop: in alto il menu (File, Modifica, Selezione, Vista, Aiuto) e la barra opzioni
(livello, salva, annulla/ripeti, aggancio, griglia, cosa si può selezionare, opzioni dello
strumento attivo); a sinistra la striscia degli strumenti a icone e sotto il pennello (clic =
contagocce); a destra i pannelli Strumento, Vista, Camera, Selezione. Passa il mouse su
un'icona per il nome e la spiegazione. Le icone vengono dal font di Windows `segmdl2.ttf`;
senza quel font i pulsanti mostrano il testo.

## Comandi

- Tasto destro trascinato: guarda intorno (W A S D Q E per volare, Shift veloce).
- Rotella: avanti/indietro verso il mouse. Tasto centrale: sposta la vista.
- Clic sinistro: seleziona; trascinando nel vuoto: rettangolo. Ctrl+clic aggiunge o toglie, Shift+clic aggiunge, Alt+rettangolo toglie.
- Tasto destro senza trascinare: menu (duplica, elimina, ruota, specchia, texture, profondità...).
- Gizmo su qualsiasi selezione: W sposta (frecce, quadratini = piani, quadrato centrale = schermo),
  E ruota (anelli, passo in gradi), R scala. Gli automatici trascinati diventano tuoi; gli originali
  si copiano (l'originale resta). Alt+trascina: sposta una copia. Aggancio ai vertici.
- Ctrl+Z / Ctrl+Y / Ctrl+S / Ctrl+D (copia) / Canc / F (inquadra) / L (seleziona collegati) / Esc.

## Terreno e pulizia

- **Estendi il bordo** (pannello Selezione > Terreno, o tasto destro): i bordi liberi della
  selezione continuano con nuove celle grandi come quella dietro, texture specchiata, altezza
  che continua / si spegne / piatta. Di default solo verso le bande (sinistra e destra).
  Salta i bordi dei buchi interni, i triangoli isolati e le file che finirebbero sopra altra
  geometria (quindi prima elimina gli automatici che coprono quel bordo).
- **Ponte tra due pezzi** (Selezione > Ponte, o tasto destro): seleziona i due pezzi insieme
  (oppure segna prima il pezzo A) e premi "Crea ponte": riempie lo spazio tra i bordi che si
  guardano, attaccato a tutti i loro vertici, altezza curva (segue le pendenze) o dritta, texture
  del pennello o del pezzo A, specchiata cella per cella. **Strumento Ponte (clic sui vertici)**:
  clic sui vertici del bordo A in fila, Invio, clic sui vertici del bordo B, Invio
  (Backspace toglie l'ultimo, Esc ricomincia).
- **Scolpisci terreno** (strumento): tieni premuto sui tuoi triangoli per alzare, abbassare,
  lisciare o appiattire (solo in altezza). Gli automatici toccati diventano tuoi; i vertici
  attaccati agli originali restano fermi. Ctrl = al contrario, Shift = liscia, Ctrl+rotella = raggio.
- **Seleziona collegati (L)**: aggiunge tutto ciò che è attaccato (tuoi con tuoi, automatici
  della stessa mesh, originali nella loro mesh). **Automatici della mesh**: tutti in blocco.
- **Rettangolo attraverso** (Selezionabili): il rettangolo prende anche quello che sta dietro.
