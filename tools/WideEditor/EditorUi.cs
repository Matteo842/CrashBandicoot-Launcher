// Panels (Italian UI): level choice, tools, view and camera, selection actions,
// and the 2D overlay drawn over the 3D view (4:3 frame, selection, gizmo, points).
using System.Numerics;
using ImGuiNET;
using NativeFileDialogSharp;

namespace WideEditor;

sealed partial class Editor
{
    // Panel sizes follow the screen (_ui) and the A-/A+ text size (_text).
    float S => _ui * _text;
    float LeftWidth => 320 * S;
    float RightWidth => 400 * S;
    float StatusHeight => 32 * S;
    const ImGuiWindowFlags Fixed = ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus;

    string _pathZone = "";
    int _pathIndex, _pathPoint;
    Vector3 _rotDrag, _scaleDrag = new(100);
    Vector2 _uvShift;
    float _bright = 100, _uniformScale = 100;
    bool _confirmLeave;
    uint _brushThumb, _selThumb;
    string? _brushThumbKey, _selThumbKey;

    static uint Col(float r, float g, float b, float a = 1) => ImGui.GetColorU32(new Vector4(r, g, b, a));

    // ---- Screens ----

    void PickScreen()
    {
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(io.DisplaySize);
        ImGui.Begin("##pick", Fixed | ImGuiWindowFlags.NoTitleBar);
        ImGui.SetWindowFontScale(1.6f);
        ImGui.Text("Crash Bandicoot - Editor 16:9");
        ImGui.SetWindowFontScale(1);
        TextSizeButtons();
        ImGui.TextDisabled("Scegli un livello. Le modifiche finiscono in RecompOne.Runtime/WideEdits e nel gioco compilato.");
        ImGui.Separator();
        ImGui.Text($"Disco: {_discPath ?? "(nessuno)"}");
        ImGui.SameLine();
        if (ImGui.Button("Scegli disco..."))
        {
            var result = Dialog.FileOpen("cue,chd,bin");
            if (result.IsOk)
            {
                _discPath = result.Path;
                _prefs.Disc = _discPath;
                OpenDisc();
            }
        }
        if (_discError != null) ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), _discError);
        if (Env.Repo == null)
            ImGui.TextColored(new Vector4(1, 0.8f, 0.3f, 1), "Cartella del progetto non trovata: salvo accanto all'editor.");
        ImGui.Spacing();

        if (_slots.Count > 0)
        {
            ImGui.SeparatorText("Apri da un salvataggio rapido del gioco (camera esatta)");
            foreach (var slot in _slots)
            {
                var level = _levels.FirstOrDefault(l => l.Id == slot.Level);
                string label = $"{slot.Name}  -  {(level.Name ?? $"livello {slot.Level}")}##{slot.File}";
                ImGui.BeginDisabled(_disc == null || level.Name == null);
                if (ImGui.Button(label)) StartLoad(slot.Level, level.Name!, slot);
                ImGui.EndDisabled();
                ImGui.SameLine();
            }
            ImGui.NewLine();
        }

        ImGui.SeparatorText("Livelli");
        ImGui.BeginDisabled(_disc == null);
        float width = ImGui.GetContentRegionAvail().X;
        int columns = Math.Max(1, (int)(width / (290 * S)));
        ImGui.BeginChild("levels", new Vector2(0, 0));
        int col = 0;
        foreach (var (id, name, kind) in _levels)
        {
            bool edited = File.Exists(Env.EditsFile(id));
            string label = $"{id,2}  {name}{(kind == "bonus" ? "  (bonus)" : "")}{(edited ? "  *" : "")}";
            if (edited) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.45f, 0.25f, 1));
            if (ImGui.Button(label, new Vector2(280 * S, 44 * S))) StartLoad(id, name);
            if (edited) ImGui.PopStyleColor();
            if (++col < columns) ImGui.SameLine();
            else col = 0;
        }
        ImGui.EndChild();
        ImGui.EndDisabled();
        ImGui.End();
    }

    void LoadingScreen()
    {
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(io.DisplaySize / 2, ImGuiCond.Always, new Vector2(0.5f));
        ImGui.Begin("##loading", Fixed | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize);
        string dots = new('.', 1 + (int)(_time * 3) % 3);
        ImGui.Text($"Carico {_loadingName}{dots}");
        ImGui.TextDisabled("Leggo il disco e calcolo le riparazioni automatiche del gioco.");
        ImGui.End();
    }

    // ---- Edit screen panels ----

    void Panels()
    {
        var io = ImGui.GetIO();
        LeftPanel(io);
        RightPanel(io);
        StatusBar(io);
        GizmoBar();
        LeavePopup();
        ContextMenu();
    }

    void LeftPanel(ImGuiIOPtr io)
    {
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(LeftWidth, io.DisplaySize.Y - StatusHeight));
        ImGui.Begin("Strumenti", Fixed);
        ImGui.TextWrapped($"{L!.Id} - {L.Name}");
        TextSizeButtons();
        if (ImGui.Button("Cambia livello"))
        {
            if (_dirty) _confirmLeave = true;
            else LeaveLevel();
        }
        ImGui.SameLine();
        if (ImGui.Button(_dirty ? "Salva *" : "Salva")) Save();
        ImGui.BeginDisabled(_undo.Count == 0);
        if (ImGui.Button("Annulla (Ctrl+Z)")) Undo();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(_redo.Count == 0);
        if (ImGui.Button("Ripeti (Ctrl+Y)")) Redo();
        ImGui.EndDisabled();

        ImGui.SeparatorText("Strumento");
        ToolButton(Tool.Select, "Seleziona / sposta", "Clic: un triangolo. Doppio clic: tutto il pezzo. Trascina nel vuoto: selezione a rettangolo.\nCtrl+clic: aggiunge o toglie un pezzo. Shift+clic: aggiunge. Alt+clic: tutto il pezzo.\nRettangolo con Shift o Ctrl: aggiunge, con Alt: toglie.\nTrascina le frecce (X rossa, Y verde, Z blu), i quadratini (piani) o il quadrato centrale.\nW sposta, E ruota, R scala. Alt+trascina: sposta una copia.");
        ToolButton(Tool.Vertex, "Muovi vertici", "Trascina un pallino giallo: il vertice si aggancia ai vertici vicini.");
        ToolButton(Tool.Quad, "Nuovo quad (4 clic)", "4 clic sui vertici (si agganciano) in giro: crea un quadrilatero con la texture del pennello.\nIl pallino arancione mostra dove va il punto. Nel vuoto il punto resta sul piano dei punti già messi; Shift+clic: sempre sul piano.");
        ToolButton(Tool.Triangle, "Nuovo triangolo (3 clic)", "3 clic: crea un triangolo con la texture del pennello, mappata come il triangolo d'origine.");
        if (ImGui.Button("Contagocce texture", new Vector2(-1, 0)))
        {
            _toolBefore = _tool == Tool.Eyedropper ? _toolBefore : _tool;
            _tool = Tool.Eyedropper;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Poi clic su un triangolo: la sua texture diventa il pennello.");
        if (_tool == Tool.Eyedropper) ImGui.TextColored(new Vector4(1, 0.9f, 0.3f, 1), "Clic su un triangolo per prendere la texture");
        ToolButton(Tool.Sculpt, "Scolpisci terreno", "Tieni premuto e passa sui tuoi triangoli: li alza, abbassa, liscia o appiattisce (solo in altezza).\nGli automatici toccati diventano tuoi (si può spegnere qui sotto).\nCtrl: al contrario. Shift: liscia. Ctrl+rotella: raggio del pennello.");
        if (_tool == Tool.Sculpt) SculptPanel();
        ToolButton(Tool.Bridge, "Ponte (clic sui vertici)", "Collega due bordi con un pezzo di terreno nuovo.\nClic sui vertici del primo bordo in fila, Invio, clic sui vertici dell'altro bordo, Invio.\nBackspace toglie l'ultimo punto, Esc ricomincia.");
        if (_tool == Tool.Bridge) BridgeToolPanel();
        if (_tool is Tool.Quad or Tool.Triangle)
        {
            ImGui.Text($"Punti: {_points.Count}/{(_tool == Tool.Quad ? 4 : 3)}");
            ImGui.SameLine();
            if (ImGui.Button("Annulla punti")) _points.Clear();
        }

        ImGui.SeparatorText("Pennello (texture)");
        if (_brush != null)
        {
            BrushThumb();
            ImGui.Image((IntPtr)_brushThumb, new Vector2(96 * S, 96 * S));
            ImGui.SameLine();
            ImGui.TextWrapped($"mesh {_brush.Mesh}\npoligono {_brush.Poly}");
        }
        else ImGui.TextDisabled("Nessuno: usa il contagocce.");

        ImGui.SeparatorText("Aggancio");
        ImGui.Checkbox("Aggancia ai vertici", ref _snapVerts);
        ImGui.Checkbox("Muovi insieme i vertici uniti", ref _weld);
        ImGui.SetNextItemWidth(120 * S);
        ImGui.DragFloat("Griglia (0 = no)", ref _grid, 1, 0, 2000, "%.0f");

        ImGui.SeparatorText("Nuovi triangoli");
        if (ImGui.RadioButton("Vera profondità##new", !_newBehind)) _newBehind = false;
        if (ImGui.RadioButton("Dietro la scena##new", _newBehind)) _newBehind = true;

        ImGui.SeparatorText("Selezionabili");
        ImGui.Checkbox("Originali", ref _pickRetail);
        ImGui.SameLine();
        ImGui.Checkbox("Automatici", ref _pickAuto);
        ImGui.SameLine();
        ImGui.Checkbox("Miei", ref _pickUser);
        ImGui.Checkbox("Rettangolo attraverso", ref _boxThrough);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Il rettangolo prende anche i triangoli nascosti dietro altri (conta il centro del triangolo).\nUtile per ripulire strati di automatici uno sopra l'altro.");

        ImGui.SeparatorText("Comandi");
        ImGui.TextWrapped("Tasto destro: menu. Tasto destro trascinato: guarda intorno (con W A S D Q E vola).\nW / E / R: sposta / ruota / scala.\nRotella: avanti/indietro verso il mouse.\nTasto centrale trascinato: sposta la vista.\nCtrl+Z annulla, Ctrl+Y ripeti, Ctrl+S salva, Ctrl+D copia, Canc elimina, F inquadra, T sposta+ruota, L seleziona collegati, Esc annulla.");
        ImGui.End();
    }

    void TextSizeButtons()
    {
        if (ImGui.Button("A-")) _text = Math.Max(0.7f, _text - 0.1f);
        ImGui.SameLine();
        if (ImGui.Button("A+")) _text = Math.Min(2f, _text + 0.1f);
        ImGui.SameLine();
        ImGui.TextDisabled("dimensione testo");
    }

    void ToolButton(Tool tool, string label, string help)
    {
        bool active = _tool == tool;
        if (active) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.25f, 0.45f, 0.8f, 1));
        if (ImGui.Button(label, new Vector2(-1, 0)))
        {
            _tool = tool;
            _points.Clear();
        }
        if (active) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(help);
    }

    void RightPanel(ImGuiIOPtr io)
    {
        ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X - RightWidth, 0));
        ImGui.SetNextWindowSize(new Vector2(RightWidth, io.DisplaySize.Y - StatusHeight));
        ImGui.Begin("Vista e selezione", Fixed);
        if (ImGui.CollapsingHeader("Vista", ImGuiTreeNodeFlags.DefaultOpen)) ViewSection();
        if (ImGui.CollapsingHeader("Camera", ImGuiTreeNodeFlags.DefaultOpen)) CameraSection();
        if (ImGui.CollapsingHeader("Selezione", ImGuiTreeNodeFlags.DefaultOpen)) SelectionSection();
        if (ImGui.CollapsingHeader("Nascosti e disattivati")) HiddenSection();
        ImGui.End();
    }

    void ViewSection()
    {
        if (ImGui.RadioButton("Come nel gioco", _gameView)) _gameView = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Centro 4:3: solo la scena originale. Bande laterali: scena + riparazioni + tue modifiche, come le disegna il gioco.");
        ImGui.SameLine();
        if (ImGui.RadioButton("Tutto ovunque", !_gameView)) _gameView = false;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mostra tutto anche al centro. I poligoni nascosti appaiono rossi.");
        ImGui.Checkbox("Buchi in magenta", ref _void);
        ImGui.SameLine();
        ImGui.Checkbox("Fil di ferro", ref _wire);
        if (_gameView)
        {
            ImGui.Checkbox("Mie anche al centro (a griglia)", ref _ghostCore);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Al centro 4:3 il gioco NON disegna riparazioni né modifiche: solo la scena originale.\nQui le tue si vedono a griglia per non perderle; nel gioco contano solo nelle bande laterali.");
        }
        if (!_gameView)
        {
            ImGui.Checkbox("Segna in rosso i nascosti nelle bande", ref _markHidden);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Rosso = il gioco NON lo disegna nelle bande 16:9.\nAl centro 4:3 lo disegna comunque (lì non si può togliere).");
        }
        ImGui.Checkbox("Riparazioni automatiche", ref _showAuto);
        ImGui.SameLine();
        if (ImGui.Checkbox("eliminati (rossi)", ref _showOff)) _autoDirty = _retailDirty = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Mostra in rosso quello che hai eliminato (automatici e originali),\nper ripristinarle (tasto destro > Ripristina).");
        ImGui.SameLine();
        ImGui.Checkbox("Mie", ref _showUser);
        if (ImGui.Checkbox("Colora automatiche (azzurro)", ref _tintAuto)) { }
        ImGui.Checkbox("Colora mie (verde)", ref _tintUser);
        ImGui.Checkbox("Tutto il livello (non solo zona + vicine)", ref _allMeshes);
        ImGui.Checkbox("Senza ombre (luce piena)", ref _flatLight);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Solo nell'editor: ignora le ombre dei vertici e mostra le texture come sono. Il gioco non cambia.");
        ImGui.SetNextItemWidth(160 * S);
        ImGui.SliderFloat("Luminosità vista", ref _viewBright, 0.5f, 4, "%.1f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Solo nell'editor. Doppio clic per scrivere il valore (1 = normale).");
        ImGui.SetNextItemWidth(160 * S);
        ImGui.SliderFloat("Velocità camera", ref _speed, 0.1f, 5, "%.1f");
    }

    void CameraSection()
    {
        if (L == null) return;
        if (_follow) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.55f, 0.3f, 1));
        if (ImGui.Button(_follow ? "Segue la camera del gioco (clic per fermare)" : "Segui la camera del gioco", new Vector2(-1, 0)))
        {
            _follow = !_follow;
            _followNote = "";
        }
        if (_follow) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Apri il gioco nello stesso livello e mettilo in pausa dove vedi il problema:\nl'editor mostra la stessa inquadratura. Muovere la camera qui la stacca.");
        if (_follow && _followNote.Length > 0) ImGui.TextWrapped(_followNote);
        bool auto = !_lockZone;
        if (ImGui.Checkbox("Zona automatica (dove sta la camera)", ref auto)) _lockZone = !auto;
        ImGui.SetNextItemWidth(200 * S);
        if (ImGui.BeginCombo("Zona", _zone?.Name ?? "(nessuna)"))
        {
            foreach (var z in L.Zones.Values)
                if (ImGui.Selectable($"{z.Name}  ({z.Paths.Sum(p => p.Points.Count)} punti)", z == _zone))
                {
                    _zone = z;
                    _lockZone = true;
                    _pathZone = z.Name;
                    _pathIndex = 0;
                    _pathPoint = 0;
                    GoPath();
                }
            ImGui.EndCombo();
        }
        if (L.Zones.TryGetValue(_pathZone, out var pz) && pz.Paths.Count > 0)
        {
            _pathIndex = Math.Clamp(_pathIndex, 0, pz.Paths.Count - 1);
            ImGui.SetNextItemWidth(200 * S);
            if (ImGui.SliderInt("Percorso", ref _pathIndex, 0, pz.Paths.Count - 1)) { _pathPoint = 0; GoPath(); }
            int n = pz.Paths[_pathIndex].Points.Count;
            if (n > 0)
            {
                ImGui.SetNextItemWidth(200 * S);
                if (ImGui.SliderInt("Punto camera", ref _pathPoint, 0, n - 1)) GoPath();
                if (ImGui.Button("<")) { _pathPoint = Math.Max(0, _pathPoint - 1); GoPath(); }
                ImGui.SameLine();
                if (ImGui.Button(">")) { _pathPoint = Math.Min(n - 1, _pathPoint + 1); GoPath(); }
                ImGui.SameLine();
                if (ImGui.Button("Torna sul percorso")) GoPath();
            }
        }
        var own = _slots.Where(s => s.Level == L.Id).ToList();
        if (own.Count > 0)
        {
            ImGui.Text("Salvataggi rapidi:");
            foreach (var s in own)
            {
                if (ImGui.SmallButton($"{s.Name}##{s.File}")) ApplySlot(s);
                ImGui.SameLine();
            }
            ImGui.NewLine();
        }
        if (ImGui.SmallButton("Ricarica salvataggi")) _slots = SaveSlot.All();
        ImGui.TextDisabled($"Camera {_cam.Pos.X:0} {_cam.Pos.Y:0} {_cam.Pos.Z:0}");
    }

    void GoPath()
    {
        if (L == null || !L.Zones.TryGetValue(_pathZone, out var z) || _pathIndex >= z.Paths.Count) return;
        var path = z.Paths[_pathIndex];
        if (path.Points.Count == 0) return;
        var pt = path.Points[Math.Clamp(_pathPoint, 0, path.Points.Count - 1)];
        _cam.SetPathPoint(pt.Pos, pt.Pitch, pt.Yaw);
        _zone = z;
        _lockZone = true;
    }

    void ApplySlot(SaveSlot slot)
    {
        _cam.SetMatrix(slot.Cam, slot.Matrix);
        if (L != null && L.Zones.TryGetValue(slot.Zone, out var z))
        {
            _zone = z;
            _lockZone = true;
            _pathZone = z.Name;
        }
        Say($"Camera del salvataggio {slot.Name}.");
    }

    void SelectionSection()
    {
        if (L == null) return;
        int retail = _sel.Count(k => KindOf(k) == Kind.Retail), auto = _sel.Count(k => KindOf(k) == Kind.Auto);
        var user = SelUser.ToList();
        if (_sel.Count == 0)
        {
            ImGui.TextWrapped("Niente selezionato. Clic su un triangolo, oppure trascina un rettangolo.");
            return;
        }
        ImGui.Text($"Originali {retail}   Automatici {auto}   Miei {user.Count}");
        if (_sel.Count == 1) ImGui.TextDisabled(Describe(_sel.First()));
        if (ImGui.Button("Deseleziona")) _sel.Clear();
        ImGui.SameLine();
        if (ImGui.Button("Inquadra (F)")) FrameSelection();

        if (retail + auto > 0)
        {
            ImGui.SeparatorText("Originali / automatici");
            if (ImGui.Button("Copia come miei (modificabili)", new Vector2(-1, 0))) CopyAsMine(false);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Crea una copia tua, uguale, da spostare/girare. L'originale resta.");
            if (retail > 0)
            {
                if (ImGui.Button("Elimina originali (anche al centro) (Canc)")) SetRemoved(true);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Spariscono ovunque nel gioco, anche nel centro 4:3. Solo grafica: i muri restano solidi.");
                ImGui.BeginDisabled(_brush == null);
                if (ImGui.Button("Metti texture del pennello sull'originale")) SetRetexture(true);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Cambia la texture del poligono del gioco: si vede ovunque, anche al centro 4:3.\nIl pennello deve venire dalla stessa mesh. I colori dei vertici restano quelli dell'originale.\nCon i 2 triangoli di un quad selezionati mette le due metà della texture, girate come sul pennello.");
                if (ImGui.Button("Gira angoli##retex")) CornerUvs(false);
                ImGui.SameLine();
                if (ImGui.Button("Texture originale##retex")) SetRetexture(false);
                if (ImGui.Button("Nascondi nelle bande 16:9")) SetHidden(true);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Il gioco non disegna più questi poligoni nelle bande laterali (al centro restano).");
                ImGui.SameLine();
                if (ImGui.Button("Mostra##retail")) SetHidden(false);
            }
            if (auto > 0)
            {
                if (ImGui.Button("Elimina automatici (Canc)")) SetRepairsOff(true);
                ImGui.SameLine();
                if (ImGui.Button("Ripristina")) SetRepairsOff(false);
                if (ImGui.Button("Rendi modificabili (sostituisci)", new Vector2(-1, 0))) CopyAsMine(true);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Disattiva questi automatici e ne crea una copia tua uguale da sistemare.");
            }
        }
        LightSelectionSection();
        BridgeSelectionSection();
        TerrainSelectionSection();
        if (user.Count > 0) UserSection(user);
    }

    string Describe(long key)
    {
        int i = IndexOf(key);
        return KindOf(key) switch
        {
            Kind.Retail when Valid(key) => $"Originale: mesh {RetailOf(i).Mesh.Name} poligono {RetailOf(i).Poly}"
                + (_doc.Retex.ContainsKey((RetailOf(i).Mesh.Name, RetailOf(i).Poly)) ? "  [texture cambiata]" : "")
                + (_doc.Removed.Contains((RetailOf(i).Mesh.Name, RetailOf(i).Poly)) ? "  [eliminato]"
                    : _doc.Hidden.Contains((RetailOf(i).Mesh.Name, RetailOf(i).Poly)) ? "  [nascosto nelle bande da te]"
                    : RuntimeHidden(RetailOf(i).Mesh, RetailOf(i).Poly) ? "  [nascosto nelle bande dal codice del gioco]" : ""),
            Kind.Auto when Valid(key) => $"Automatico: mesh {_auto!.Tris[i].Mesh.Name} poligono {_auto.Tris[i].Poly} (n. {i})"
                + (_doc.IsRepairHidden(_auto.Tris[i]) ? "  [disattivato]" : ""),
            Kind.User when Valid(key) => $"Mio: mesh {_doc.Tris[i].Mesh} poligono {_doc.Tris[i].Poly} gruppo {_doc.Tris[i].Group}",
            _ => "",
        };
    }

    void UserSection(List<int> user)
    {
        ImGui.SeparatorText("Miei");
        if (ImGui.Button("Duplica (Ctrl+D)")) CopyAsMine(false);
        ImGui.SameLine();
        if (ImGui.Button("Elimina (Canc)")) DeleteSelection();

        ImGui.SeparatorText("Posizione");
        var pivot = SelPivot();
        var moved = pivot;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.DragFloat3("##pos", ref moved, 2, 0, 0, "%.0f"))
        {
            BeginChange();
            var d = moved - pivot;
            TransformSel(p => p + d);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Centro della selezione (X Y Z). Trascina o doppio clic per scrivere.");
        float step = _grid > 0 ? _grid : 32;
        string[] names = ["X", "Y", "Z"];
        for (int a = 0; a < 3; a++)
        {
            if (ImGui.Button($"-{names[a]}##m{a}")) { PushUndo(); var d = -Axes[a] * step; TransformSel(p => p + d); }
            ImGui.SameLine();
            if (ImGui.Button($"+{names[a]}##m{a}")) { PushUndo(); var d = Axes[a] * step; TransformSel(p => p + d); }
            if (a < 2) ImGui.SameLine();
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"(passo {step:0})");

        ImGui.SeparatorText("Rotazione");
        for (int a = 0; a < 3; a++)
        {
            float v = a == 0 ? _rotDrag.X : a == 1 ? _rotDrag.Y : _rotDrag.Z;
            ImGui.SetNextItemWidth(130 * S);
            if (ImGui.DragFloat($"{names[a]}°##r{a}", ref v, 0.5f, -360, 360, "%.1f"))
            {
                BeginChange();
                float old = a == 0 ? _rotDrag.X : a == 1 ? _rotDrag.Y : _rotDrag.Z;
                RotateSel(Axes[a], v - old);
                if (a == 0) _rotDrag.X = v; else if (a == 1) _rotDrag.Y = v; else _rotDrag.Z = v;
            }
            if (ImGui.IsItemDeactivated()) _rotDrag = Vector3.Zero;
            ImGui.SameLine();
            if (ImGui.Button($"-90##r{a}")) { PushUndo(); RotateSel(Axes[a], -90); }
            ImGui.SameLine();
            if (ImGui.Button($"+90##r{a}")) { PushUndo(); RotateSel(Axes[a], 90); }
            ImGui.SameLine();
            if (ImGui.Button($"180##r{a}")) { PushUndo(); RotateSel(Axes[a], 180); }
        }

        if (user.Any(i => OnBackdrop(_doc.Tris[i])))
        {
            ImGui.SeparatorText("Cielo");
            ImGui.TextWrapped("Il cielo gira attorno alla camera: per allungarlo copia un pezzo e giralo qui.");
            if (ImGui.Button("<- Gira##sky")) { PushUndo(); RotateAroundSky(-_skyStep); }
            ImGui.SameLine();
            if (ImGui.Button("Gira ->##sky")) { PushUndo(); RotateAroundSky(_skyStep); }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70 * S);
            ImGui.DragFloat("gradi##sky", ref _skyStep, 0.5f, 0.5f, 180, "%.1f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Di quanto gira ogni clic, attorno all'asse verticale che passa per la camera.");
        }

        ImGui.SeparatorText("Scala e specchio");
        ImGui.SetNextItemWidth(130 * S);
        float us = _uniformScale;
        if (ImGui.DragFloat("Scala %", ref us, 0.5f, 1, 1000, "%.0f"))
        {
            BeginChange();
            float f = us / _uniformScale;
            var c = SelPivot();
            TransformSel(p => c + (p - c) * f);
            _uniformScale = us;
        }
        if (ImGui.IsItemDeactivated()) _uniformScale = 100;
        var sv = _scaleDrag;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.DragFloat3("##scale", ref sv, 0.5f, 1, 1000, "%.0f%%"))
        {
            BeginChange();
            var f = sv / _scaleDrag;
            var c = SelPivot();
            TransformSel(p => c + (p - c) * f);
            _scaleDrag = sv;
        }
        if (ImGui.IsItemDeactivated()) _scaleDrag = new(100);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Scala per asse X Y Z (%).");
        for (int a = 0; a < 3; a++)
        {
            if (ImGui.Button($"Specchia {names[a]}"))
            {
                PushUndo();
                var c = SelPivot();
                int axis = a;
                TransformSel(p =>
                {
                    var q = p;
                    if (axis == 0) q.X = 2 * c.X - p.X; else if (axis == 1) q.Y = 2 * c.Y - p.Y; else q.Z = 2 * c.Z - p.Z;
                    return q;
                }, mirror: true);
            }
            if (a < 2) ImGui.SameLine();
        }

        ImGui.SeparatorText("Texture");
        SelThumb(user);
        ImGui.Image((IntPtr)_selThumb, new Vector2(72 * S, 72 * S));
        ImGui.SameLine();
        ImGui.BeginGroup();
        if (ImGui.Button("Ruota a sinistra")) { PushUndo(); UvSel(st => new(st.Y, 1 - st.X)); }
        ImGui.SameLine();
        if (ImGui.Button("a destra")) { PushUndo(); UvSel(st => new(1 - st.Y, st.X)); }
        if (ImGui.Button("Specchia orizz.")) { PushUndo(); UvSel(st => new(1 - st.X, st.Y)); }
        ImGui.SameLine();
        if (ImGui.Button("vert.")) { PushUndo(); UvSel(st => new(st.X, 1 - st.Y)); }
        if (ImGui.Button("Gira angoli")) { PushUndo(); CornerUvs(false); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Per ogni triangolo: gli angoli della texture passano al vertice accanto.\nPremi più volte (e prova Scambia) finché le righe vanno nel verso giusto.");
        ImGui.SameLine();
        if (ImGui.Button("Scambia angoli")) { PushUndo(); CornerUvs(true); }
        ImGui.EndGroup();
        var shift = _uvShift;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.DragFloat2("##uvshift", ref shift, 0.2f, -256, 256, "%.0f"))
        {
            BeginChange();
            var d = new Vector2(MathF.Round(shift.X) - MathF.Round(_uvShift.X), MathF.Round(shift.Y) - MathF.Round(_uvShift.Y));
            foreach (int i in user) for (int k = 0; k < 3; k++) _doc.Tris[i].V[k].Uv += d;
            Changed();
            _uvShift = shift;
        }
        if (ImGui.IsItemDeactivated()) _uvShift = Vector2.Zero;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Sposta la texture (U, V in texel). Attento: fuori dal riquadro esce roba a caso.");
        ImGui.BeginDisabled(_brush == null);
        if (ImGui.Button("Metti a celle (come l'originale)")) ApplyBrushCells(false);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Su un quad (2 triangoli): lo divide in celle grandi come quelle del triangolo\nda cui hai preso il pennello, la texture intera su ogni cella: stessa grandezza e verso dell'originale.");
        var (ax, ay) = BrushCells();
        ImGui.SetNextItemWidth(60 * S);
        ImGui.DragInt("##cx", ref _cellsX, 0.1f, 0, 64, _cellsX == 0 ? $"auto {ax}" : "%d");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60 * S);
        ImGui.DragInt("celle (0 = auto)", ref _cellsY, 0.1f, 0, 64, _cellsY == 0 ? $"auto {ay}" : "%d");
        if (ImGui.Button("Metti texture del pennello")) ApplyBrush(false);
        ImGui.SameLine();
        if (ImGui.Button("+ colori")) ApplyBrush(true);
        ImGui.EndDisabled();
        if (ImGui.Button("Prendi come pennello")) TakeBrush(PickId(Kind.User, user[0]));

        ImGui.SeparatorText("Colore");
        ImGui.SetNextItemWidth(130 * S);
        float br = _bright;
        if (ImGui.DragFloat("Luminosità %", ref br, 0.5f, 5, 400, "%.0f"))
        {
            BeginChange();
            float f = br / _bright;
            foreach (int i in user) for (int k = 0; k < 3; k++) _doc.Tris[i].V[k].Rgb = Vector3.Clamp(_doc.Tris[i].V[k].Rgb * f, Vector3.Zero, new(255));
            Changed();
            _bright = br;
        }
        if (ImGui.IsItemDeactivated()) _bright = 100;
        var colour = _doc.Tris[user[0]].V[0].Rgb / 255;
        if (ImGui.ColorEdit3("Colore di tutti", ref colour, ImGuiColorEditFlags.NoInputs))
        {
            BeginChange();
            foreach (int i in user) for (int k = 0; k < 3; k++) _doc.Tris[i].V[k].Rgb = colour * 255;
            Changed();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("128 = colore della texture così com'è (il gioco moltiplica).");

        ImGui.SeparatorText("Profondità");
        bool behind = _doc.Tris[user[0]].Behind;
        if (ImGui.RadioButton("Vera profondità", !behind)) { PushUndo(); foreach (int i in user) _doc.Tris[i].Behind = false; Changed(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Davanti o dietro come un oggetto vero della scena.");
        if (ImGui.RadioButton("Dietro la scena (tappa solo i buchi)", behind)) { PushUndo(); foreach (int i in user) _doc.Tris[i].Behind = true; Changed(); }
        if (behind)
        {
            int rank = (int)_doc.Tris[user[0]].Rank;
            ImGui.SetNextItemWidth(130 * S);
            if (ImGui.SliderInt("Strato", ref rank, 0, 40))
            {
                BeginChange();
                foreach (int i in user) _doc.Tris[i].Rank = rank;
                Changed();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Tra riempimenti dietro la scena: numero più basso = davanti.");
        }
        bool culled = _doc.Tris[user[0]].Culled;
        if (ImGui.Checkbox("Visibile solo dal davanti", ref culled))
        {
            PushUndo();
            foreach (int i in user) _doc.Tris[i].Culled = culled;
            Changed();
        }
    }

    void HiddenSection()
    {
        if (L == null) return;
        ImGui.Text($"Originali eliminati ovunque: {_doc.Removed.Count}");
        if (_doc.Removed.Count > 0 && ImGui.SmallButton("Ripristina tutti gli originali eliminati"))
        {
            PushUndo();
            _doc.Removed.Clear();
            _retailDirty = true;
            Changed();
        }
        ImGui.Text($"Originali nascosti nelle bande: {_doc.Hidden.Count}");
        foreach (var h in _doc.Hidden.OrderBy(h => h.Mesh).ThenBy(h => h.Poly).Take(200).ToList())
        {
            ImGui.Text($"{h.Mesh} #{h.Poly}");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Seleziona##h{h.Mesh}{h.Poly}") && L.MeshByName.TryGetValue(h.Mesh, out var m))
            {
                _sel.Clear();
                _sel.Add(Key(Kind.Retail, _meshBase[m.Index] + h.Poly));
                FrameSelection();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton($"Mostra##h{h.Mesh}{h.Poly}"))
            {
                PushUndo();
                _doc.Hidden.Remove(h);
                _retailDirty = true;
                Changed();
            }
        }
        ImGui.Text($"Automatici eliminati: {_doc.HiddenRepairs.Count}");
        if (_doc.HiddenRepairs.Count > 0 && ImGui.SmallButton("Ripristina tutti"))
        {
            PushUndo();
            _doc.HiddenRepairs.Clear();
            _autoDirty = true;
            Changed();
        }
    }

    void StatusBar(ImGuiIOPtr io)
    {
        ImGui.SetNextWindowPos(new Vector2(0, io.DisplaySize.Y - StatusHeight));
        ImGui.SetNextWindowSize(new Vector2(io.DisplaySize.X, StatusHeight));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 5));
        ImGui.Begin("##status", Fixed | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar);
        string hover = "";
        if (Decode(_hoverId, out var kind, out int index))
        {
            hover = Describe(Key(kind, index));
            if (_hoverPos.W > 0.5f) hover += $"   ({_hoverPos.X:0}, {_hoverPos.Y:0}, {_hoverPos.Z:0})";
        }
        ImGui.Text($"Zona {_zone?.Name ?? "-"}   |   {hover}");
        bool longStatus = false;
        if (_status.Length > 0 && _time - _statusTime < (_status.Length > 120 ? 20 : 8))
        {
            ImGui.SameLine(Math.Max(io.DisplaySize.X * 0.55f, ImGui.GetCursorPosX() + 20));
            longStatus = ImGui.CalcTextSize(_status).X > io.DisplaySize.X - ImGui.GetCursorPosX() - 16;
            if (!longStatus) ImGui.TextColored(new Vector4(1, 0.85f, 0.4f, 1), _status);
        }
        ImGui.End();
        ImGui.PopStyleVar();
        if (longStatus)
        {
            // Too long for the bar (warnings): wrapped in a box just above it.
            float width = io.DisplaySize.X * 0.45f;
            ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X - width - 8, io.DisplaySize.Y - StatusHeight - 4),
                ImGuiCond.Always, new Vector2(0, 1));
            ImGui.SetNextWindowSize(new Vector2(width, 0));
            ImGui.Begin("##longstatus", ImGuiWindowFlags.NoDecoration
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings);
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 0.85f, 0.4f, 1));
            ImGui.TextWrapped(_status);
            ImGui.PopStyleColor();
            ImGui.End();
        }
    }

    void LeavePopup()
    {
        if (_confirmLeave)
        {
            ImGui.OpenPopup("Salvare?");
            _confirmLeave = false;
        }
        bool open = true;
        if (ImGui.BeginPopupModal("Salvare?", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Ci sono modifiche non salvate.");
            if (ImGui.Button("Salva")) { Save(); LeaveLevel(); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("Non salvare")) { LeaveLevel(); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("Annulla")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    void LeaveLevel()
    {
        CaptureCursor(false);
        SavePrefs();
        _dirty = false;
        _sel.Clear();
        _screen = Screen.Pick;
        _slots = SaveSlot.All();
    }

    // ---- Thumbnails ----

    byte[] Tile(string meshName, int poly, Vector2 lo, Vector2 hi, out int w, out int h)
    {
        w = Math.Clamp((int)MathF.Round(hi.X - lo.X) + 1, 1, 256);
        h = Math.Clamp((int)MathF.Round(hi.Y - lo.Y) + 1, 1, 128);
        var rgba = new byte[w * h * 4];
        if (L == null || !L.MeshByName.TryGetValue(meshName, out var m) || poly < 0 || poly >= m.PolyCount) return rgba;
        var mat = L.MaterialOf(m, poly);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                ushort t = L.Texel(mat, (int)lo.X + x, (int)lo.Y + y);
                int o = (y * w + x) * 4;
                rgba[o] = (byte)((t & 31) * 8);
                rgba[o + 1] = (byte)(((t >> 5) & 31) * 8);
                rgba[o + 2] = (byte)(((t >> 10) & 31) * 8);
                rgba[o + 3] = (byte)(t == 0 ? 0 : 255);
            }
        return rgba;
    }

    void BrushThumb()
    {
        var b = _brush!;
        string key = $"{b.Mesh}/{b.Poly}/{b.Min}/{b.Max}";
        if (key == _brushThumbKey) return;
        _brushThumbKey = key;
        var rgba = Tile(b.Mesh, b.Poly, b.Min, b.Max, out int w, out int h);
        _brushThumb = _gfx.Thumbnail(_brushThumb, rgba, w, h);
    }

    void SelThumb(List<int> user)
    {
        var t = _doc.Tris[user[0]];
        Vector2 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (int i in user)
            if (_doc.Tris[i].Mesh == t.Mesh && _doc.Tris[i].Poly == t.Poly)
                foreach (var v in _doc.Tris[i].V) { lo = Vector2.Min(lo, v.Uv); hi = Vector2.Max(hi, v.Uv); }
        string key = $"{t.Mesh}/{t.Poly}/{lo}/{hi}";
        if (key == _selThumbKey) return;
        _selThumbKey = key;
        var rgba = Tile(t.Mesh, t.Poly, lo, hi, out int w, out int h);
        _selThumb = _gfx.Thumbnail(_selThumb, rgba, w, h);
    }

    // ---- Overlay over the 3D view ----

    void Overlay()
    {
        var dl = ImGui.GetBackgroundDrawList();
        var min = new Vector2(_view.X, _view.Y);
        var max = min + new Vector2(_view.W, _view.H);
        dl.PushClipRect(min, max, false);
        float coreX0 = _view.X + _view.W * (1 - Camera.CoreFraction) / 2, coreX1 = _view.X + _view.W * (1 + Camera.CoreFraction) / 2;
        dl.AddLine(new(coreX0, min.Y), new(coreX0, max.Y), Col(1, 1, 1, 0.35f), 1);
        dl.AddLine(new(coreX1, min.Y), new(coreX1, max.Y), Col(1, 1, 1, 0.35f), 1);

        Span<Vector3> c = stackalloc Vector3[3];
        Span<Vector2> s = stackalloc Vector2[3];
        int drawn = 0;
        Span<Vector3> clipped = stackalloc Vector3[4];
        var screen = new Vector2[4];
        foreach (var key in _sel)
        {
            if (++drawn > 6000) break;
            if (!Corners(key, c)) continue;
            // Cut at the near plane: long skirts run past the camera.
            int n = 0;
            float near = Camera.Near + 1;
            for (int k = 0; k < 3; k++)
            {
                var a = c[k];
                var b = c[(k + 1) % 3];
                float da = _cam.Depth(a) - near, db = _cam.Depth(b) - near;
                if (da >= 0) clipped[n++] = a;
                if ((da >= 0) != (db >= 0)) clipped[n++] = a + (b - a) * (da / (da - db));
            }
            if (n < 3) continue;
            bool ok = true;
            for (int k = 0; k < n && ok; k++) ok = _cam.Project(clipped[k], _view, out screen[k]);
            if (!ok) continue;
            uint fill = KindOf(key) == Kind.User ? Col(0.3f, 1, 0.3f, 0.22f) : Col(1, 0.9f, 0.2f, 0.25f);
            dl.AddConvexPolyFilled(ref screen[0], n, fill);
            dl.AddPolyline(ref screen[0], n, Col(1, 0.9f, 0.2f, 0.95f), ImDrawFlags.Closed, 2f * _ui);
        }

        if (_tool == Tool.Select && _sel.Count > 0) DrawGizmo(dl);
        if (_tool == Tool.Sculpt && _view.Contains(ImGui.GetIO().MousePos)) SculptOverlay(dl);
        if (_tool == Tool.Bridge) BridgeOverlay(dl);
        BridgePieceOverlay(dl);
        if (_tool == Tool.Vertex)
        {
            foreach (var t in _doc.Tris)
                foreach (var v in t.V)
                    if (_cam.Project(Shown(t, v.Pos), _view, out var p)) dl.AddCircleFilled(p, 3.5f, Col(1, 0.85f, 0.1f, 0.9f));
            if (_hoverVertex is { } hv && _cam.Project(Shown(_doc.Tris[hv.Tri], _doc.Tris[hv.Tri].V[hv.K].Pos), _view, out var hp))
                dl.AddCircle(hp, 8, Col(1, 1, 1), 0, 2);
        }
        if (_tool is Tool.Quad or Tool.Triangle)
        {
            var mouse = ImGui.GetIO().MousePos;
            Vector2? prev = null;
            foreach (var p in _points)
                if (_cam.Project(p, _view, out var sp))
                {
                    dl.AddCircleFilled(sp, 5, Col(0.3f, 1, 0.4f));
                    if (prev is { } pv) dl.AddLine(pv, sp, Col(0.3f, 1, 0.4f), 2);
                    prev = sp;
                }
            var target = _pointPreview is { } pp && _cam.Project(pp, _view, out var ss) ? ss : mouse;
            if (prev is { } last && _view.Contains(mouse)) dl.AddLine(last, target, Col(0.3f, 1, 0.4f, 0.6f), 1.5f);
            // Where the next click puts its point.
            if (_view.Contains(mouse)) dl.AddCircleFilled(target, 5 * _ui, Col(1, 0.6f, 0.1f, 0.95f));
        }
        if (_snapHit is { } snap && _cam.Project(snap, _view, out var sn))
            dl.AddCircle(sn, 9, Col(0.2f, 1, 1), 0, 2.5f);
        if (_drag == DragMode.Box)
        {
            var mouse = ImGui.GetIO().MousePos;
            var a = Vector2.Min(_dragStart, mouse);
            var b = Vector2.Max(_dragStart, mouse);
            dl.AddRectFilled(a, b, Col(0.3f, 0.6f, 1, 0.15f));
            dl.AddRect(a, b, Col(0.4f, 0.7f, 1, 0.9f));
        }
        if (_tool == Tool.Eyedropper)
            dl.AddText(ImGui.GetIO().MousePos + new Vector2(14, 10), Col(1, 0.9f, 0.3f), "contagocce");
        dl.PopClipRect();
    }
}
