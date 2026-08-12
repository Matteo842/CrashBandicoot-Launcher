using System.Globalization;
using System.Text;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using Java.Lang;
using RecompOne.Runtime;
using RecompOne.Runtime.Diagnostics;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;
using Color = Android.Graphics.Color;
using Exception = System.Exception;
using Math = System.Math;
using Runtime = RecompOne.Runtime.Runtime;
using StringBuilder = System.Text.StringBuilder;

namespace CrashBandicoot.AndroidRuntime;

sealed partial class DevMenuOverlay
{
    const int LiveMs = 400;
    const int HexRows = 20;
    const int HexCols = 16;
    const int MapW = 512;
    const int MapH = 256;

    static readonly string[] GprNames =
    [
        "zero","at","v0","v1","a0","a1","a2","a3",
        "t0","t1","t2","t3","t4","t5","t6","t7",
        "s0","s1","s2","s3","s4","s5","s6","s7",
        "t8","t9","k0","k1","gp","sp","fp","ra",
    ];

    readonly Handler _liveHandler = new(Looper.MainLooper!);
    readonly StringBuilder _sb = new();
    readonly Spu.VoiceDebug[] _voices = new Spu.VoiceDebug[24];
    readonly List<string> _cdEvents = [];
    readonly List<string> _consoleLines = [];
    readonly List<OverlayEvent> _overlayEvents = [];
    readonly int[] _mapPixels = new int[MapW * MapH];

    IRunnable? _livePending;
    Action? _liveTick;
    TextView? _liveText;
    ImageView? _liveImage;
    uint _memBase = 0x80000000u;
    string _consoleFilter = "";

    void StopLiveRefresh()
    {
        if (_livePending != null)
        {
            _liveHandler.RemoveCallbacks(_livePending);
            _livePending = null;
        }
        _liveTick = null;
        _liveText = null;
        _liveImage = null;
        RamLogger.TrackReads = false;
        AndroidVramCapture.Enabled = false;
    }

    void StartLiveRefresh(Action tick)
    {
        _liveTick = tick;
        void Fire()
        {
            if (!IsOpen || _liveTick == null) return;
            try { _liveTick(); }
            catch { /* guest RAM can disappear mid-frame */ }
            _livePending = new Runnable(Fire);
            _liveHandler.PostDelayed(_livePending, LiveMs);
        }

        _livePending = new Runnable(Fire);
        _liveHandler.PostDelayed(_livePending, LiveMs);
    }

    TextView LiveMono()
    {
        var text = new TextView(_activity)
        {
            TextSize = 11,
            Typeface = Typeface.Monospace,
        };
        text.SetTextColor(Sand);
        text.SetTextIsSelectable(true);
        _liveText = text;
        _body.AddView(text, Margin(top: 6));
        return text;
    }

    ImageView LiveImage(int heightDp)
    {
        var image = new ImageView(_activity);
        image.SetAdjustViewBounds(true);
        image.SetScaleType(ImageView.ScaleType.FitCenter);
        image.SetBackgroundColor(Color.Rgb(8, 8, 8));
        _liveImage = image;
        _body.AddView(image, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(heightDp))
        {
            TopMargin = Dp(8),
        });
        return image;
    }

    EditText HexField(string value, int maxLen)
    {
        var field = new EditText(_activity)
        {
            Text = value,
            InputType = InputTypes.TextFlagCapCharacters | InputTypes.TextFlagNoSuggestions,
        };
        field.SetSingleLine(true);
        field.SetTextColor(Sand);
        field.SetHintTextColor(Muted);
        field.SetFilters([new InputFilterLengthFilter(maxLen)]);
        field.SetBackgroundColor(Color.Argb(80, 12, 12, 12));
        field.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
        return field;
    }

    void BuildCpu()
    {
        Hint("Live GPR + COP0. Orange would be “changed this frame” on desktop.");
        LiveMono();
        UpdateCpu();
        StartLiveRefresh(UpdateCpu);
    }

    void UpdateCpu()
    {
        if (_liveText == null) return;
        var cpu = Runtime.Cpu;
        if (cpu == null)
        {
            _liveText.Text = "No CPU context.";
            return;
        }

        _sb.Clear();
        _sb.AppendLine("GPR");
        for (var i = 0; i < 32; i++)
            _sb.Append(GprNames[i].PadRight(5)).Append(' ').Append(cpu[i].ToString("X8")).AppendLine();
        _sb.Append("hi   ").Append(cpu.HI.ToString("X8")).AppendLine();
        _sb.Append("lo   ").Append(cpu.LO.ToString("X8")).AppendLine();
        _sb.AppendLine();
        _sb.AppendLine("COP0");
        _sb.Append("SR      ").Append(cpu.SR.ToString("X8")).AppendLine();
        _sb.Append("Cause   ").Append(cpu.Cause.ToString("X8")).AppendLine();
        _sb.Append("EPC     ").Append(cpu.EPC.ToString("X8")).AppendLine();
        _sb.Append("BadVAddr ").Append(cpu.BadVAddr.ToString("X8")).AppendLine();
        _sb.Append("PRId    ").Append(cpu.PRId.ToString("X8"));
        _liveText.Text = _sb.ToString();
    }

    void BuildMemory()
    {
        RamLogger.TrackReads = true;
        Hint("Go to a KUSEG/KSEG0 address, then poke a byte.");
        var addr = HexField($"{_memBase:X8}", 8);
        addr.Hint = "80000000";
        _body.AddView(addr, Margin(top: 6));
        FullButton("Go", () =>
        {
            if (uint.TryParse(addr.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
            {
                _memBase = parsed & 0xFFFFFFE0u;
                addr.Text = $"{_memBase:X8}";
                UpdateMemory();
            }
        });

        var pokeRow = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        var pokeAddr = HexField("80000000", 8);
        pokeAddr.Hint = "addr";
        var pokeVal = HexField("00", 2);
        pokeVal.Hint = "byte";
        pokeRow.AddView(pokeAddr, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.4f));
        pokeRow.AddView(pokeVal, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.6f)
        {
            LeftMargin = Dp(8),
        });
        _body.AddView(pokeRow, Margin(top: 8));
        FullButton("Poke byte", () =>
        {
            if (!uint.TryParse(pokeAddr.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var at))
            {
                Toast("Bad poke address.");
                return;
            }
            if (!byte.TryParse(pokeVal.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                Toast("Bad poke byte.");
                return;
            }
            try
            {
                Runtime.Mem?.WriteU8(at, value);
                UpdateMemory();
            }
            catch (Exception)
            {
                Toast("Poke failed.");
            }
        });

        LiveMono();
        UpdateMemory();
        StartLiveRefresh(UpdateMemory);
    }

    void UpdateMemory()
    {
        if (_liveText == null) return;
        var mem = Runtime.Mem;
        if (mem == null)
        {
            _liveText.Text = "No memory.";
            return;
        }

        _sb.Clear();
        var start = _memBase;
        for (var row = 0; row < HexRows; row++)
        {
            var rowAddr = start + (uint)(row * HexCols);
            _sb.Append(rowAddr.ToString("X8")).Append("  ");
            for (var col = 0; col < HexCols; col++)
            {
                try
                {
                    _sb.Append(mem.ReadU8(rowAddr + (uint)col).ToString("X2"));
                }
                catch
                {
                    _sb.Append("??");
                }
                _sb.Append(col == 7 ? "  " : " ");
            }
            _sb.Append(" |");
            for (var col = 0; col < HexCols; col++)
            {
                try
                {
                    var b = mem.ReadU8(rowAddr + (uint)col);
                    _sb.Append(b is >= 32 and < 127 ? (char)b : '.');
                }
                catch
                {
                    _sb.Append('.');
                }
            }
            _sb.AppendLine("|");
        }
        _liveText.Text = _sb.ToString();
    }

    void BuildRamMap()
    {
        Runtime.RamLog.EnsureAllocated();
        RamLogger.TrackReads = true;
        Hint("Write heat = red, read heat = blue. 2 MB KUSEG, downsampled.");
        Toggle("Greyscale RAM", Runtime.RamLog.ShowGreyscale, value =>
        {
            Runtime.RamLog.ShowGreyscale = value;
            UpdateRamMap();
        });
        LiveImage(180);
        UpdateRamMap();
        StartLiveRefresh(UpdateRamMap);
    }

    void UpdateRamMap()
    {
        if (_liveImage == null) return;
        var log = Runtime.RamLog;
        log.EnsureAllocated();
        ReadOnlySpan<byte> ram = Runtime.Mem is PSMemory ps ? ps.Ram : ReadOnlySpan<byte>.Empty;
        var pixels = _mapPixels;
        const int srcW = RamLogger.Width;
        const int srcH = RamLogger.Height;
        for (var y = 0; y < MapH; y++)
        {
            var srcY = y * srcH / MapH;
            for (var x = 0; x < MapW; x++)
            {
                var idx = srcY * srcW + (x * srcW / MapW);
                byte b = idx < ram.Length ? ram[idx] : (byte)0;
                float shade = log.ShowGreyscale ? 1f - b / 255f : 1f;
                float r = 0.25f * shade, g = 0.15f * shade, bl = 0.15f * shade;
                var wHeat = log.HeatAt(idx);
                var rHeat = log.ReadHeatAt(idx);
                r = Math.Clamp(r + wHeat * 0.75f, 0f, 1f);
                g = Math.Clamp(g, 0f, 1f);
                bl = Math.Clamp(bl + rHeat * 0.75f, 0f, 1f);
                pixels[y * MapW + x] = unchecked((int)(
                    0xFF000000u
                    | ((uint)(r * 255f) << 16)
                    | ((uint)(g * 255f) << 8)
                    | (uint)(bl * 255f)));
            }
        }

        var bmp = Bitmap.CreateBitmap(MapW, MapH, Bitmap.Config.Argb8888!);
        bmp.SetPixels(pixels, 0, MapW, 0, 0, MapW, MapH);
        SetLiveBitmap(bmp);
    }

    void UpdateRootFps()
    {
        if (_liveText == null) return;
        _process.Refresh();
        _liveText.Text = $"{AndroidPlatformHost.LastFps:0.0} fps   ·   {FormatBytes(_process.WorkingSet64)}";
    }

    void BuildVram()
    {
        AndroidVramCapture.Enabled = true;
        Hint("GL VRAM (1024×512), captured on the render thread. Same source as desktop.");
        LiveMono();
        LiveImage(220);
        UpdateVram();
        StartLiveRefresh(UpdateVram);
    }

    void UpdateVram()
    {
        if (_liveImage == null) return;
        if (!AndroidVramCapture.CopyDownsampleArgb(_mapPixels, MapW, MapH))
        {
            if (_liveText != null)
            {
                _liveText.Text = GpuHle.Backend is { Ready: true }
                    ? "Waiting for first GL readback…"
                    : "GL backend not ready.";
            }
            return;
        }

        if (_liveText != null)
            _liveText.Text = "GL VRAM 1024×512 (downsampled)";

        var bmp = Bitmap.CreateBitmap(MapW, MapH, Bitmap.Config.Argb8888!);
        bmp.SetPixels(_mapPixels, 0, MapW, 0, 0, MapW, MapH);
        SetLiveBitmap(bmp);
    }

    void SetLiveBitmap(Bitmap bmp)
    {
        if (_liveImage == null)
        {
            bmp.Recycle();
            return;
        }

        var old = (_liveImage.Drawable as Android.Graphics.Drawables.BitmapDrawable)?.Bitmap;
        _liveImage.SetImageBitmap(bmp);
        if (old != null && old != bmp && !old.IsRecycled)
            old.Recycle();
    }

    void BuildSpu()
    {
        Hint("24 voices + XA. N/P/R/E = noise / pitch-mod / reverb / end.");
        LiveMono();
        UpdateSpu();
        StartLiveRefresh(UpdateSpu);
    }

    void UpdateSpu()
    {
        if (_liveText == null) return;
        var spu = Runtime.Spu;
        if (spu == null)
        {
            _liveText.Text = "No SPU.";
            return;
        }

        spu.CaptureDebug(_voices, out var st);
        _sb.Clear();
        _sb.Append("Main ").Append(st.MainVolL.ToString("X4")).Append(' ').Append(st.MainVolR.ToString("X4"));
        _sb.Append("  CD ").Append(st.CdVolL.ToString("X4")).Append(' ').Append(st.CdVolR.ToString("X4"));
        _sb.Append("  Ext ").Append(st.ExtVolL.ToString("X4")).Append(' ').AppendLine(st.ExtVolR.ToString("X4"));
        _sb.Append("Reverb ").Append(st.ReverbVolL.ToString("X4")).Append(' ').Append(st.ReverbVolR.ToString("X4"));
        _sb.Append(" @ ").Append(st.ReverbStartAddr.ToString("X5"));
        _sb.Append("  SPUCNT ").AppendLine(st.Spucnt.ToString("X4"));
        _sb.Append("Enable ").Append((st.Spucnt & 0x8000) != 0 ? "on" : "off");
        _sb.Append("  Unmute ").Append((st.Spucnt & 0x4000) != 0 ? "on" : "off");
        _sb.Append("  Reverb ").Append((st.Spucnt & 0x0080) != 0 ? "on" : "off");
        _sb.Append("  CD audio ").AppendLine((st.Spucnt & 0x0001) != 0 ? "on" : "off");
        _sb.Append("Transfer ").AppendLine(st.TransferAddr.ToString("X5"));

        var buffered = XaAudio.BufferedSamples;
        var rate = XaAudio.SourceRate;
        var ms = rate > 0 ? buffered * 1000f / rate : 0f;
        _sb.Append("XA ").Append(XaAudio.Playing ? "playing" : "stopped");
        _sb.Append("  ").Append(rate).Append(" Hz  buffered ").Append(buffered);
        _sb.Append(" (").Append(ms.ToString("0")).AppendLine(" ms)");
        _sb.AppendLine();
        _sb.AppendLine("V  Phase     ENVX  VL   VR   Pitch Start Repeat Cur   Flags");
        for (var i = 0; i < 24; i++)
        {
            var v = _voices[i];
            var on = v.Phase != Spu.AdsrPhase.Off;
            _sb.Append(i.ToString("D2")).Append(' ');
            _sb.Append((on ? v.Phase.ToString() : "Off").PadRight(9));
            _sb.Append(v.AdsrVol.ToString("X4")).Append(' ');
            _sb.Append(v.VolL.ToString("X4")).Append(' ');
            _sb.Append(v.VolR.ToString("X4")).Append(' ');
            _sb.Append(v.Pitch.ToString("X4")).Append(' ');
            _sb.Append(((uint)v.StartAddr << 3).ToString("X5")).Append(' ');
            _sb.Append(((uint)v.RepeatAddr << 3).ToString("X5")).Append(' ');
            _sb.Append(v.CurAddr.ToString("X5")).Append(' ');
            if (v.Noise) _sb.Append('N');
            if (v.Pmod) _sb.Append('P');
            if (v.Reverb) _sb.Append('R');
            if (v.EndX) _sb.Append('E');
            _sb.AppendLine();
        }
        _liveText.Text = _sb.ToString();
    }

    void BuildCd()
    {
        Hint("Seek / read state plus the recent CD event log.");
        FullButton("Clear events", () =>
        {
            Runtime.Cd?.ClearDebugEvents();
            UpdateCd();
        });
        LiveMono();
        UpdateCd();
        StartLiveRefresh(UpdateCd);
    }

    void UpdateCd()
    {
        if (_liveText == null) return;
        var cd = Runtime.Cd;
        if (cd == null)
        {
            _liveText.Text = "No CD controller.";
            return;
        }

        cd.CaptureDebug(out var d, _cdEvents);
        _sb.Clear();
        _sb.Append("Seek LBA ").Append(d.SeekLba).Append(" (").Append(Msf(d.SeekLba)).AppendLine(")");
        _sb.Append("Last read ").Append(d.LastReadLba).Append(" (").Append(Msf(d.LastReadLba)).AppendLine(")");
        _sb.Append("Sectors read ").AppendLine(d.SectorsRead.ToString());
        _sb.Append("Reading ").Append(d.Reading ? "yes" : "no");
        _sb.Append("  Stream ").Append(d.StreamPending ? "yes" : "no");
        _sb.Append("  Data ready ").AppendLine(d.DataReady ? "yes" : "no");
        _sb.Append("FIFO ").Append(d.DataFifoPos).Append('/').Append(d.DataBufLength);
        _sb.Append("  IRQ ").Append(d.IrqFlags.ToString("X2"));
        _sb.Append(" last ").Append(d.LastIrq);
        _sb.Append(" pending ").AppendLine(d.PendingIrqCount.ToString());
        _sb.Append("Param/Resp ").Append(d.ParamCount).Append('/').Append(d.ResponseCount);
        _sb.Append("  Index ").AppendLine(d.Index.ToString());
        _sb.AppendLine();
        _sb.AppendLine("Events");
        var start = Math.Max(0, _cdEvents.Count - 40);
        for (var i = start; i < _cdEvents.Count; i++)
            _sb.AppendLine(_cdEvents[i]);
        _liveText.Text = _sb.ToString();
    }

    static string Msf(int lba)
    {
        var abs = Math.Max(0, lba + 150);
        return $"{abs / (60 * 75):D2}:{abs / 75 % 60:D2}:{abs % 75:D2}";
    }

    void BuildOverlays()
    {
        Hint("EXE overlays loaded by the recompiler dispatcher.");
        FullButton("Clear log", () =>
        {
            Runtime.OverlayLog.Clear();
            UpdateOverlays();
        });
        LiveMono();
        UpdateOverlays();
        StartLiveRefresh(UpdateOverlays);
    }

    void UpdateOverlays()
    {
        if (_liveText == null) return;
        _sb.Clear();
        _sb.Append("Active: ");
        var names = Dispatcher.ActiveNames;
        if (names.Length == 0) _sb.AppendLine("none");
        else _sb.AppendLine(string.Join(" · ", names));
        _sb.AppendLine();

        _overlayEvents.Clear();
        Runtime.OverlayLog.Read(_overlayEvents);
        var start = Math.Max(0, _overlayEvents.Count - 50);
        for (var i = start; i < _overlayEvents.Count; i++)
        {
            var ev = _overlayEvents[i];
            _sb.Append(FormatOverlayTime(ev.TimestampMs)).Append(' ');
            _sb.Append(ev.Kind switch
            {
                OverlayEventKind.Loaded => "loaded",
                OverlayEventKind.Unloaded => "unloaded",
                OverlayEventKind.Overwritten => "overwritten",
                OverlayEventKind.VramCollision => "vram collision",
                _ => "?",
            });
            _sb.Append(' ').Append(ev.OverlayName);
            if (ev.DisplacedBy != null)
                _sb.Append("  ").Append(ev.Kind == OverlayEventKind.VramCollision ? "with " : "by ")
                    .Append(ev.DisplacedBy);
            _sb.AppendLine();
        }
        _liveText.Text = _sb.ToString();
    }

    static string FormatOverlayTime(long ms)
    {
        var s = ms / 1000;
        var m = s / 60;
        var h = m / 60;
        return h > 0
            ? $"{h}:{m % 60:D2}:{s % 60:D2}.{ms % 1000 / 10:D2}"
            : $"{m:D2}:{s % 60:D2}.{ms % 1000 / 10:D2}";
    }

    void BuildConsole()
    {
        Hint("Mirrors Console.Out. Turn categories on to see BIOS/SPU/GPU/… spam.");
        Toggle("BIOS", Log.BiosOn, v => Log.BiosOn = v);
        Toggle("SPU", Log.SpuOn, v => Log.SpuOn = v);
        Toggle("GPU", Log.GpuOn, v => Log.GpuOn = v);
        Toggle("DMA", Log.DmaOn, v => Log.DmaOn = v);
        Toggle("CD", Log.CdOn, v => Log.CdOn = v);
        Toggle("SDK", Log.SdkOn, v => Log.SdkOn = v);
        Toggle("MDEC", Log.MdecOn, v => Log.MdecOn = v);
        var filter = HexField(_consoleFilter, 64);
        filter.InputType = InputTypes.ClassText;
        filter.Hint = "filter";
        filter.TextChanged += (_, _) =>
        {
            _consoleFilter = filter.Text ?? "";
            UpdateConsole();
        };
        _body.AddView(filter, Margin(top: 6));
        FullButton("Clear", () =>
        {
            ConsoleMirror.Clear();
            UpdateConsole();
        });
        LiveMono();
        UpdateConsole();
        StartLiveRefresh(UpdateConsole);
    }

    void UpdateConsole()
    {
        if (_liveText == null) return;
        ConsoleMirror.SnapshotInto(_consoleLines);
        _sb.Clear();
        var filter = _consoleFilter;
        var shown = 0;
        for (var i = _consoleLines.Count - 1; i >= 0 && shown < 80; i--)
        {
            var line = _consoleLines[i];
            if (filter.Length > 0 && line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            shown++;
        }

        var keep = shown;
        for (var i = _consoleLines.Count - 1; i >= 0 && keep > 0; i--)
        {
            var line = _consoleLines[i];
            if (filter.Length > 0 && line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            keep--;
            if (keep == 0)
            {
                for (var j = i; j < _consoleLines.Count; j++)
                {
                    var l = _consoleLines[j];
                    if (filter.Length == 0 || l.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        _sb.AppendLine(l);
                }
                break;
            }
        }

        _liveText.Text = _sb.Length == 0 ? "(empty)" : _sb.ToString();
    }

    static List<(string Name, long Bytes, string Note)> KnownPools(out long accounted)
    {
        var rows = new List<(string, long, string)>();
        long total = 0;

        void Add(string name, long bytes, string note)
        {
            rows.Add((name, bytes, note));
            if (bytes > 0) total += bytes;
        }

        var ramLogOn = Runtime.RamLog.IsAllocated;
        var ramMapCells = RamLogger.Width * RamLogger.Height;
        Add("RamLogger timestamps", ramLogOn ? ramMapCells * sizeof(uint) * 2L : 0,
            ramLogOn ? "allocated (RAM Map used)" : "lazy — not allocated yet");

        var psRam = Runtime.Mode == RunMode.Devkit
            ? MemoryMap.DevkitRamSize
            : MemoryMap.RetailRamSize;
        Add("PS main RAM", psRam, Runtime.Mode == RunMode.Devkit ? "Devkit 8MB" : "Retail 2MB");
        Add("VRAM shadow (CPU)", Gpu.VramWidth * Gpu.VramHeight * sizeof(ushort), "1024×512 ×2");
        Add("SPU RAM", Spu.RamSize, "512 KB");
        Add("GL vertex pool", 0x40000L * 32, "MaxVerts × ~32 B (+ matching VBO)");

        var scale = GlVram.Scale;
        Add($"GL VRAM main ({scale}x)", (long)GlVram.Width * GlVram.Height * 2, "GPU texture estimate");
        Add("GL VRAM stage (1x)", (long)VramShadow.Width * VramShadow.Height * 2, "GPU texture estimate");
        accounted = total;
        return rows;
    }
}
