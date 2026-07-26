using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Bios;

public static class BiosB
{
    static readonly PadReadEvent _padEvent = new();
    struct EvCB { public uint Status, Class, Spec, Mode, Func; }
    const int MaxEvents = 64;
    static readonly EvCB[] _evCBs = new EvCB[MaxEvents];
    struct TCB { public bool Used; }
    const int MaxThreads = 4;
    static readonly TCB[] _tcbs = new TCB[MaxThreads];

    static readonly uint[] _intChain = new uint[4];

    public static uint IntrEnvInInterruptAddr = 0u;

    static uint _padBuf;
    static uint _cardChan;
    static uint _cardStatus = 1; // 01h = ready (see psx-spx _card_status)
    // Sony memcard FLAG bit3: set on "insert"/power-on, cleared by writing (CardAccept).
    // _card_info delivers EvSpNEW (0x2000) while set, EvSpIOE (0x0004) once cleared.
    static bool _cardFlagNewA = true;
    static bool _cardFlagNewB = true;

    public static void DeliverEvent(uint @class, uint spec)
    {
        for (int i = 0; i < MaxEvents; i++)
            if (_evCBs[i].Status == 2u && _evCBs[i].Class == @class && _evCBs[i].Spec == spec)
                _evCBs[i].Status = 4u;
    }
    
    public static void DeliverEventIntr(CpuContext c, IMemory m, uint @class, uint spec)
    {
        int hit = 0;
        for (int i = 0; i < MaxEvents; i++)
        {
            // Active (2) or already-ready (4): allow re-signal after CardInfo so a
            // following sector write can refresh HwCARD IOE.
            if (_evCBs[i].Status != 2u && _evCBs[i].Status != 4u) continue;
            if (_evCBs[i].Class != @class || _evCBs[i].Spec != spec) continue;
            hit++;
            // EvMdCALL (0x1000): invoke callback. EvMdMARK (0x2000): status only.
            if ((_evCBs[i].Mode & 0x1000u) != 0 && (_evCBs[i].Mode & 0x2000u) == 0 && _evCBs[i].Func != 0u)
            {
                var snap = c.Snapshot();
                try
                {
                    RecompOne.Runtime.Dispatch.Dispatcher.Call(c, m, _evCBs[i].Func);
                }
                catch (Exception ex)
                {
                    Diagnostics.SessionLog.Exception($"card event cb class=0x{@class:X8} spec=0x{spec:X4}", ex);
                }
                c.Restore(snap);
            }
            _evCBs[i].Status = 4u;
        }
        if (hit == 0 && (@class == 0xF4000001u || @class == 0xF0000011u))
            Diagnostics.SessionLog.Warn($"card event NOT DELIVERED class=0x{@class:X8} spec=0x{spec:X4} (no enabled listener)");
    }

    public static void SignalSwCard(CpuContext c, IMemory m, uint spec) =>
        DeliverEventIntr(c, m, 0xF4000001u, spec);

    public static void SignalHwCard(CpuContext c, IMemory m, uint spec) =>
        DeliverEventIntr(c, m, 0xF0000011u, spec);

    public static void SignalSwCard(CpuContext c, IMemory m, bool ok) =>
        SignalSwCard(c, m, ok ? 0x0004u : 0x8000u);

    public static void SignalHwCard(CpuContext c, IMemory m, bool ok) =>
        SignalHwCard(c, m, ok ? 0x0004u : 0x0100u);
    
    public static void CardComplete(CpuContext c, IMemory m, uint port)
    {
        var card = (port & 0x10u) != 0 ? Runtime.CardB : Runtime.CardA;
        _cardChan = port;
        _cardStatus = card.Enabled ? 1u : 0x11u;
        Diagnostics.SessionLog.Info($"card_complete/_card_load port=0x{port:X2} enabled={card.Enabled}");
        // _card_load completion is reported on SwCARD (libcard / PCSXR).
        SignalSwCard(c, m, card.Enabled);
    }

    public static void CardInfo(CpuContext c, IMemory m)
    {
        _cardChan = c.A0;
        bool slotB = (c.A0 & 0x10u) != 0;
        var card = slotB ? Runtime.CardB : Runtime.CardA;
        _cardStatus = card.Enabled ? 1u : 0x11u;
        // pcsx_rearmed card_vint INFO: disabled→TIMEOUT, FLAG&8→NEW, else IOE.
        uint spec;
        if (!card.Enabled) spec = 0x0100u;
        else if (slotB ? _cardFlagNewB : _cardFlagNewA) spec = 0x2000u;
        else spec = 0x0004u;
        Diagnostics.SessionLog.Info($"card_info port=0x{c.A0:X2} enabled={card.Enabled} spec=0x{spec:X4}");
        // Crash polls SwCARD; also raise HwCARD like the real BIOS vint handler.
        SignalSwCard(c, m, spec);
        SignalHwCard(c, m, spec);
        c.V0 = 1u;
    }

    static void CardRead(CpuContext c, IMemory m)
    {
        var card = (c.A0 & 0x10u) != 0 ? Runtime.CardB : Runtime.CardA;
        _cardChan = c.A0;
        bool ok = card.Enabled && c.A2 != 0u;
        if (ok)
        {
            Span<byte> f = stackalloc byte[0x80];
            card.FrameRead((int)(c.A1 & 0x3FFu), f);
            for (uint i = 0; i < 0x80u; i++) m.WriteU8(c.A2 + i, f[(int)i]);
            _cardStatus = 1u;
        }
        else _cardStatus = 0x11u;
        Diagnostics.SessionLog.Info($"card_read port=0x{c.A0:X2} sector={c.A1 & 0x3FFu}");
        SignalHwCard(c, m, ok);
        c.V0 = 1u;
    }

    static void CardWrite(CpuContext c, IMemory m)
    {
        var card = (c.A0 & 0x10u) != 0 ? Runtime.CardB : Runtime.CardA;
        bool slotB = (c.A0 & 0x10u) != 0;
        _cardChan = c.A0;
        uint sector = c.A1 & 0x3FFu;
        // CardAccept: _new_card + _card_write(port, 0x3F, NULL). Null src is valid —
        // real BIOS still completes the transfer and clears FLAG bit3. Treating NULL as
        // failure delivered Hw TIMEOUT and trapped Crash in INSERT MEMORY CARD forever.
        bool ok = card.Enabled && sector <= 0x400u;
        if (ok)
        {
            if (c.A2 != 0u && sector < 0x400u)
            {
                Span<byte> f = stackalloc byte[0x80];
                for (uint i = 0; i < 0x80u; i++) f[(int)i] = m.ReadU8(c.A2 + i);
                card.FrameWrite((int)sector, f);
            }
            // null src or sector 0x400: CardAccept / no-op write — still clears FLAG
            if (slotB) _cardFlagNewB = false; else _cardFlagNewA = false;
            _cardStatus = 1u;
        }
        else _cardStatus = 0x11u;
        Diagnostics.SessionLog.Info($"card_write port=0x{c.A0:X2} sector={sector} src={(c.A2 == 0u ? "null" : $"0x{c.A2:X8}")} ok={ok}");
        SignalHwCard(c, m, ok);
        c.V0 = ok ? 1u : 0u;
    }

    public static uint GetFreeEvSlot()
    {
        for (int i = 0; i < MaxEvents; i++) if (_evCBs[i].Status == 0u) return (uint)i;
        return 0xFFFFFFFFu;
    }
    
    static ushort FirePad(IMemory m, int port, ushort buttons)
    {
        if (!Event.HasAnyListeners<PadReadEvent>()) return buttons;
        var e = _padEvent;
        e.Context = Runtime.Cpu!; e.Memory = m;
        e.Port = port; e.Buttons = buttons;
        Event.Dispatch(e);
        return e.Buttons;
    }

    static void PadRead(IMemory m)
    {
        if (_padBuf == 0) return;
        // Crash samples the pad at frame start (before VSync). Prefer a lightweight poll;
        // full window pump is done from Present. Avoid HostWindow type visibility issues.
        Host.InputManager.Poll();
        // Re-apply after Poll — otherwise ExitToMap Start/Select is wiped before the guest sees it.
        Host.ExitToMapInjector.ApplyOverlay();
        ushort s = Hardware.Controller.State;
        ushort swapped = (ushort)((s >> 8) | (s << 8));
        ushort s2 = Hardware.Controller.State2;
        ushort swapped2 = (ushort)((s2 >> 8) | (s2 << 8));
        swapped = FirePad(m, 0, swapped);
        swapped2 = FirePad(m, 1, swapped2);
        // Crash PadUpdate (retail) takes the LOW 16 bits for port 0 and HIGH for port 1
        // (c1's decomp has this backwards). Keep pad1 in the low halfword.
        m.WriteU32(_padBuf, ((uint)swapped2 << 16) | swapped);
        m.WriteU8(_padBuf + 4, Hardware.Controller.RightX);
        m.WriteU8(_padBuf + 5, Hardware.Controller.RightY);
        m.WriteU8(_padBuf + 6, Hardware.Controller.LeftX);
        m.WriteU8(_padBuf + 7, Hardware.Controller.LeftY);
    }

    public static void RefreshPad(IMemory m) => PadRead(m);
    public static void Dispatch(CpuContext c, IMemory m, uint fn)
    {
        Log.Bios($"B({fn:X2}) {BiosNames.B(fn)}");
        switch (fn)
        {
            case 0x00: c.V0 = 0u; break;
            case 0x01: break;
            case 0x02: c.V0 = 0u; break;
            case 0x03: c.V0 = 0u; break;
            case 0x04: break;
            case 0x05: break;
            case 0x06: break;
            case 0x07: DeliverEvent(c.A0, c.A1); break;
            case 0x08: c.V0 = OpenEvent(c.A0, c.A1, c.A2, c.A3); break;
            case 0x09: CloseEvent(c.A0); c.V0 = 1u; break;
            case 0x0A: c.V0 = WaitEvent(c.A0); break;
            case 0x0B: c.V0 = TestEvent(c.A0); break;
            case 0x0C: EnableEvent(c.A0); c.V0 = 1u; break;
            case 0x0D: DisableEvent(c.A0); c.V0 = 1u; break;
            case 0x0E: c.V0 = OpenTh(c.A0, c.A1, c.A2); break;
            case 0x0F: CloseTh(c.A0); c.V0 = 1u; break;
            case 0x10: break;
            case 0x11: break;
            case 0x12: break;
            case 0x13: break;
            case 0x14: break;
            case 0x15: _padBuf = c.A1; break;
            case 0x16: PadRead(m); break;
            case 0x17: break;
            case 0x18: IntrEnvInInterruptAddr = 0u; break;
            case 0x19: IntrEnvInInterruptAddr = c.A0 != 0u ? c.A0 - 0x36u : 0u; break;
            case 0x1A: break;
            case 0x1B: break;
            case 0x1C: break;
            case 0x1D: break;
            case 0x1E: break;
            case 0x1F: break;
            case 0x20: UnDeliverEvent(c.A0, c.A1); break;
            case 0x2B: break;
            case 0x2C: break;
            case 0x2D: break;
            case 0x2E: break;
            case 0x2F: c.V0 = 0u; break;
            case 0x30: c.V0 = 0u; break;
            case 0x31: c.V0 = 0u; break;
            case 0x32: BiosA.Dispatch(c, m, 0x00); break;
            case 0x33: BiosA.Dispatch(c, m, 0x01); break;
            case 0x34: BiosA.Dispatch(c, m, 0x02); break;
            case 0x35: BiosA.Dispatch(c, m, 0x03); break;
            case 0x36: BiosA.Dispatch(c, m, 0x04); break;
            case 0x37: BiosA.Dispatch(c, m, 0x05); break;
            case 0x38: BiosA.Dispatch(c, m, 0x06); break;
            case 0x39: c.V0 = c.A0 <= 2u ? 2u : 0u; break;
            case 0x3A: c.V0 = 0xFFFFFFFFu; break;
            case 0x3B: Console.Write((char)(c.A0 & 0xFF)); c.V0 = c.A0; break;
            case 0x3C: c.V0 = 0xFFFFFFFFu; break;
            case 0x3D: Console.Write((char)(c.A0 & 0xFF)); c.V0 = c.A0; break;
            case 0x3E: c.V0 = 0u; break;
            case 0x3F: Console.Write(Bios.ReadString(m, c.A0)); c.V0 = c.A0; break;
            case 0x40: c.V0 = 1u; break;
            case 0x41: c.V0 = BiosA.CardFormat(m, c.A0); break;
            case 0x42: c.V0 = BiosA.FirstFile(c, m, c.A0, c.A1); break;
            case 0x43: c.V0 = BiosA.NextFile(m, c.A0); break;
            case 0x44: c.V0 = 0u; break;
            case 0x45: c.V0 = BiosA.CardDelete(m, c.A0); break;
            case 0x46: c.V0 = 0u; break;
            case 0x47: c.V0 = GetFreeEvSlot(); break;
            case 0x48: c.V0 = 0xFFFFFFFFu; break;
            case 0x49: break;
            case 0x4A: c.V0 = 1u; break;
            case 0x4B: c.V0 = 1u; break;
            case 0x4C: c.V0 = 1u; break;
            case 0x4D: CardInfo(c, m); break;
            case 0x4E: CardWrite(c, m); break;
            case 0x4F: CardRead(c, m); break;
            case 0x50:
                // _new_card: paired with CardAccept write; allow next I/O.
                Diagnostics.SessionLog.Info("_new_card");
                _cardStatus = 1u;
                break;
            case 0x51: c.V0 = KromFont.Krom2RawAdd(c.A0); break;
            case 0x53: c.V0 = KromFont.Krom2Offset(c.A0); break;
            case 0x54: c.V0 = BiosA.LastErrno; break;
            case 0x55: c.V0 = 0u; break;
            case 0x56: c.V0 = 0u; break;
            case 0x57: c.V0 = 0u; break;
            case 0x58: c.V0 = _cardChan; break;
            case 0x59: c.V0 = BiosA.TestDevice(m, c.A0); break;
            case 0x5B: c.V0 = 0u; break;
            case 0x5C: c.V0 = _cardStatus; break; // 1=ready
            case 0x5D: c.V0 = _cardStatus = 1u; break; // wait → ready immediately
            default: break;
        }
    }

    static uint OpenEvent(uint @class, uint spec, uint mode, uint func)
    {
        for (int i = 0; i < MaxEvents; i++)
        {
            if (_evCBs[i].Status == 0u)
            {
                _evCBs[i] = new EvCB { Status = 1u, Class = @class, Spec = spec, Mode = mode, Func = func };
                if (@class == 0xF4000001u || @class == 0xF0000011u)
                    Diagnostics.SessionLog.Info($"OpenEvent card slot={i} class=0x{@class:X8} spec=0x{spec:X4} mode=0x{mode:X}");
                return 0xF0000000u | (uint)i;
            }
        }
        return 0xFFFFFFFFu;
    }

    static int EvSlot(uint ev)
    {
        int i = (int)(ev & 0xFFu);
        return i < MaxEvents ? i : -1;
    }

    static void CloseEvent(uint ev)
    {
        int s = EvSlot(ev);
        if (s >= 0) _evCBs[s] = default;
    }

    static uint WaitEvent(uint ev)
    {
        // Real BIOS blocks until ready. HLE: succeed only if already delivered.
        // Returning 1 unconditionally made Crash treat EvSpNEW as always fired →
        // infinite CardAccept (_new_card + write sector 63) → INSERT MEMORY CARD.
        int s = EvSlot(ev);
        if (s < 0 || _evCBs[s].Status == 0u || _evCBs[s].Status == 1u)
            return 0u;
        if (_evCBs[s].Status == 4u)
        {
            _evCBs[s].Status = 2u;
            return 1u;
        }
        return 0u;
    }

    static uint TestEvent(uint ev)
    {
        int s = EvSlot(ev);
        if (s >= 0 && _evCBs[s].Status == 4u)
        {
            _evCBs[s].Status = 2u;
            return 1u;
        }
        return 0u;
    }

    static void EnableEvent(uint ev)
    {
        int s = EvSlot(ev);
        if (s >= 0)
        {
            _evCBs[s].Status = 2u;
            if (_evCBs[s].Class == 0xF4000001u || _evCBs[s].Class == 0xF0000011u)
                Diagnostics.SessionLog.Info($"EnableEvent card handle=0x{ev:X8} class=0x{_evCBs[s].Class:X8} spec=0x{_evCBs[s].Spec:X4}");
        }
    }

    static void DisableEvent(uint ev)
    {
        int s = EvSlot(ev);
        if (s >= 0 && _evCBs[s].Status != 0u) _evCBs[s].Status = 1u;
    }

    static void UnDeliverEvent(uint @class, uint spec)
    {
        for (int i = 0; i < MaxEvents; i++)
            if (_evCBs[i].Status == 4u && _evCBs[i].Class == @class && _evCBs[i].Spec == spec)
                _evCBs[i].Status = 2u;
    }
    static uint OpenTh(uint pc, uint spFp, uint gp)
    {
        for (int i = 0; i < MaxThreads; i++)
            if (!_tcbs[i].Used) { _tcbs[i] = new TCB { Used = true }; return 0xFF000000u | (uint)i; }
        return 0xFFFFFFFFu;
    }
    static void CloseTh(uint handle)
    {
        int i = (int)(handle & 0xFFu);
        if (i < MaxThreads) _tcbs[i] = default;
    }

    public static void SysEnqIntRP(CpuContext c, IMemory m)
    {
        uint priority = c.A0 & 3u;
        uint struc = c.A1;
        c.V0 = _intChain[priority];
        m.WriteU32(struc, _intChain[priority]);
        _intChain[priority] = struc;
    }
    public static void SysDeqIntRP(CpuContext c, IMemory m)
    {
        uint priority = c.A0 & 3u;
        uint struc = c.A1;
        if (_intChain[priority] == struc)
        {
            _intChain[priority] = m.ReadU32(struc);
            c.V0 = 1u;
            return;
        }
        uint cur = _intChain[priority];
        while (cur != 0u)
        {
            uint next = m.ReadU32(cur);
            if (next == struc) { m.WriteU32(cur, m.ReadU32(struc)); c.V0 = 1u; return; }
            cur = next;
        }
        c.V0 = 0u;
    }
}
