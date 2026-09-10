using CHDSharp;
using CHDSharp.Models;

namespace RecompOne.Runtime.Cdrom;

/// <summary>Reads the raw PS1 data track directly from a CHD, without extracting a BIN.</summary>
internal sealed class ChdDisc : IDiscImage
{
    const int FrameBytes = 2448; // 2352 bytes of sector data + 96 bytes of subchannel data.
    const int SectorBytes = 2352;
    readonly ChdFile _chd;
    readonly ulong _firstFrame;
    readonly int _frames;
    readonly object _ioGate = new();

    ChdDisc(ChdFile chd, ChdTrackInfo track)
    {
        _chd = chd;
        // A virtual pregap is not stored. A pregap with data is stored before INDEX 01.
        int storedPregap = track.PreGapDataSize > 0 ? track.PreGap : 0;
        _firstFrame = checked(track.StartFrame + (ulong)storedPregap);
        _frames = checked(track.Frames - storedPregap);
        if (_frames <= 0 || checked((_firstFrame + (ulong)_frames) * FrameBytes) > chd.TotalBytes)
            throw new InvalidDataException("The CHD track is incomplete or has invalid frame offsets.");
        chd.ConfigureCache(8);
    }

    public long DataTrackBytes => (long)_frames * SectorBytes;

    public static ChdDisc Open(string path)
    {
        var error = ChdFile.Open(path, out var chd);
        if (error != ChdError.Chderrnone || chd == null)
        {
            chd?.Dispose();
            throw new InvalidDataException($"Cannot open CHD: {error.GetMessage()}. Use a complete, standalone CD CHD.");
        }

        try
        {
            var tracks = chd.Tracks;
            if (!chd.IsCd || chd.IsGdRom || chd.UnitBytes != FrameBytes ||
                tracks is not { Count: 1 } || tracks[0].TrackType != ChdTrackType.Mode2Raw ||
                tracks[0].DataSize != SectorBytes)
                throw new InvalidDataException(
                    "Unsupported CHD layout. Crash Bandicoot requires a single MODE2/2352 CD data track.");
            return new ChdDisc(chd, tracks[0]);
        }
        catch
        {
            chd.Dispose();
            throw;
        }
    }

    public byte[] ReadSector(int lba) => ReadSectorData(lba, 2048);

    public byte[] ReadSectorData(int lba, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        var buffer = new byte[size];
        if (lba < 0 || lba >= _frames || size == 0) return buffer;

        // Match CueBin's PS1 read modes, including XA/Form2 and raw header reads.
        int offset = size switch { >= 2340 => 12, >= 2329 => 16, _ => 24 };
        int count = Math.Min(size, SectorBytes - offset);
        ulong position = (_firstFrame + (ulong)lba) * FrameBytes + (ulong)offset;
        lock (_ioGate)
        {
            var error = _chd.Read(position, buffer, 0, count);
            if (error != ChdError.Chderrnone)
                throw new InvalidDataException($"Cannot read CHD sector {lba}: {error.GetMessage()}.");
        }
        return buffer;
    }

    public void Dispose()
    {
        lock (_ioGate) _chd.Dispose();
    }
}
