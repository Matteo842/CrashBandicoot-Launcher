using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RecompOne.Runtime.Cdrom;

namespace CrashBandicoot.Launcher.Recomp;

public sealed record DiscValidation(
    bool Ok,
    string CuePath,
    string? BinPath,
    string Fingerprint,
    string Message,
    string Title = "",
    string Problem = "",
    string Fix = "",
    string Focus = ""); // "cue" | "bin" | "pair" | "game" | "other"

public static class DiscValidator
{
    public const string ExpectedGameId = "SCUS-94900";
    public const string ExpectedBoot = "SCUS_949.00";

    /// <summary>Reject tiny fakes; a real Crash NTSC-U MODE2 dump is hundreds of MB.</summary>
    const long MinBinBytes = 80L * 1024 * 1024;

    static readonly Regex FileLine = new(
        @"^FILE\s+""([^""]+)""\s+(BINARY|MOTOROLA|AIFF|WAVE|MP3)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex TrackLine = new(
        @"^TRACK\s+(\d{1,2})\s+(MODE1/2048|MODE1/2352|MODE2/2336|MODE2/2352|AUDIO)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex IndexLine = new(
        @"^INDEX\s+(\d{1,2})\s+(\d{2}):(\d{2}):(\d{2})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static DiscValidation Validate(string cuePath)
    {
        cuePath = Path.GetFullPath(cuePath);
        if (!File.Exists(cuePath))
        {
            return Fail(cuePath, null, "cue",
                "Disc file not found",
                "The selected .cue path does not exist on disk.",
                "Use Select disc and pick your Crash Bandicoot .cue again.");
        }

        if (!cuePath.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(cuePath, null, "cue",
                "Wrong file type",
                "You selected something that is not a .cue sheet.",
                "Pick the .cue file (the small text sheet), not the .bin alone.");
        }

        string binPath;
        try
        {
            var sheet = ParseCueSheet(cuePath);
            if (!sheet.Ok)
                return sheet.Failure!;

            binPath = sheet.BinPath!;
            if (!File.Exists(binPath))
            {
                return Fail(cuePath, binPath, "bin",
                    "Missing .bin image",
                    "The .cue is readable, but the disc image it points to is missing.",
                    "Put the matching .bin in the same folder as the .cue (same names as inside the cue sheet). A prepared game folder does not replace your dump.");
            }

            var binLen = new FileInfo(binPath).Length;
            if (binLen < MinBinBytes)
            {
                return Fail(cuePath, binPath, "bin",
                    ".bin looks incomplete",
                    $"The disc image is only {binLen / (1024 * 1024)} MB. A full PS1 Crash dump is hundreds of MB.",
                    "Re-dump the disc (or restore the full .bin). Do not use an empty / tiny placeholder file.");
            }
        }
        catch (Exception ex)
        {
            return Fail(cuePath, null, "cue",
                "Cannot read .cue",
                ex.Message,
                "Open the .cue in a text editor: it should list FILE \"something.bin\" BINARY, TRACK, and INDEX lines.");
        }

        string contentKey;
        try
        {
            using var fs = CueFs.Open(cuePath);

            var pvd = fs.ReadSector(16);
            if (pvd.Length < 6 ||
                pvd[1] != (byte)'C' || pvd[2] != (byte)'D' ||
                pvd[3] != (byte)'0' || pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
            {
                return Fail(cuePath, binPath, "bin",
                    "Not a disc image",
                    "The .bin does not look like a PlayStation / ISO9660 image (bad volume descriptor).",
                    "Make sure the .bin is a real PS1 dump paired with this .cue — not a renamed random file.");
            }

            string cnf;
            try
            {
                cnf = Encoding.ASCII.GetString(fs.ReadFile("SYSTEM.CNF"));
            }
            catch
            {
                var found = fs.FindFile("SYSTEM.CNF");
                if (found == null)
                {
                    return Fail(cuePath, binPath, "bin",
                        "Not a PlayStation disc",
                        "SYSTEM.CNF was not found inside the image.",
                        "This dump is not a PS1 game disc (or the image is corrupt).");
                }
                cnf = Encoding.ASCII.GetString(fs.ReadFile(found));
            }

            if (!cnf.Contains(ExpectedBoot, StringComparison.OrdinalIgnoreCase) &&
                !cnf.Contains("SCUS_949", StringComparison.OrdinalIgnoreCase))
            {
                return Fail(cuePath, binPath, "game",
                    "Wrong game / region",
                    $"This dump is not Crash Bandicoot NTSC-U ({ExpectedGameId}). BOOT must be {ExpectedBoot}.",
                    "Use an NTSC-U Crash Bandicoot dump (SCUS-94900). Other regions or games will not work.");
            }

            if (!fs.Locate(ExpectedBoot, out _, out var bootSize) &&
                !fs.Locate("SCUS_949.00", out _, out bootSize))
            {
                return Fail(cuePath, binPath, "bin",
                    "Boot file missing on disc",
                    $"The image claims Crash, but {ExpectedBoot} was not found in the filesystem.",
                    "The .bin may be corrupt or incomplete. Re-dump the disc.");
            }

            byte[] boot;
            try
            {
                boot = fs.ReadFile(ExpectedBoot);
            }
            catch
            {
                var alt = fs.FindFile(ExpectedBoot) ?? fs.FindFile("SCUS_949.00");
                if (alt == null)
                {
                    return Fail(cuePath, binPath, "bin",
                        "Boot file unreadable",
                        $"Could not read {ExpectedBoot} from the disc image.",
                        "The .bin may be corrupt. Re-dump the disc.");
                }
                boot = fs.ReadFile(alt);
            }

            var magic = Encoding.ASCII.GetString(boot, 0, Math.Min(8, boot.Length));
            if (!magic.StartsWith("PS-X EXE", StringComparison.Ordinal))
            {
                return Fail(cuePath, binPath, "bin",
                    "Invalid boot executable",
                    "The boot file is not a PS-X EXE.",
                    "The .bin contents look wrong or corrupt. Re-dump the disc.");
            }

            contentKey = ContentKey(binPath, pvd, boot, bootSize);
        }
        catch (Exception ex)
        {
            return Fail(cuePath, binPath, "bin",
                "Cannot open disc image",
                ex.Message,
                "Check that the .bin next to the .cue is complete and not locked by another program.");
        }

        var fingerprint = FingerprintDisc(cuePath, binPath, contentKey);
        return new DiscValidation(
            true, cuePath, binPath, fingerprint,
            "Disc OK — Crash Bandicoot (SCUS-94900).",
            "Disc ready",
            "Valid Crash Bandicoot NTSC-U dump.",
            "You can press Start Game.",
            "pair");
    }

    public static DiscValidation EnsureDiscPresentForLaunch(string cuePath, string expectedFingerprint)
    {
        var v = Validate(cuePath);
        if (!v.Ok) return v;

        if (!string.Equals(v.Fingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(cuePath, v.BinPath, "pair",
                "Disc changed",
                "The dump on disk no longer matches the prepared game.",
                "Select your Crash Bandicoot .cue again (with the matching .bin in the same folder).");
        }

        return v;
    }

    public static string FingerprintDisc(string cuePath, string binPath, string contentKey)
    {
        var cueInfo = new FileInfo(cuePath);
        var binInfo = new FileInfo(binPath);
        var payload =
            $"{contentKey}|{binInfo.Length}|{ExpectedGameId}|postpass-v2|{cueInfo.Length}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash)[..16];
    }

    static string ContentKey(string binPath, byte[] pvd, byte[] boot, uint bootSize)
    {
        var volId = Encoding.ASCII.GetString(pvd, 40, 32).Trim();
        var sampleLen = Math.Min(boot.Length, 4096);
        var bootHash = Convert.ToHexString(SHA256.HashData(boot.AsSpan(0, sampleLen)));
        var binLen = new FileInfo(binPath).Length;
        return $"{volId}|{bootSize}|{bootHash}|{binLen}";
    }

    static (bool Ok, string? BinPath, DiscValidation? Failure) ParseCueSheet(string cuePath)
    {
        var dir = Path.GetDirectoryName(cuePath) ?? "";
        string? binPath = null;
        var sawDataTrack = false;
        var sawIndex01 = false;
        var lineNo = 0;

        foreach (var raw in File.ReadLines(cuePath))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("REM", StringComparison.OrdinalIgnoreCase))
                continue;

            var file = FileLine.Match(line);
            if (file.Success)
            {
                if (binPath != null)
                {
                    return (false, null, Fail(cuePath, null, "cue",
                        "Unsupported .cue layout",
                        "This cue sheet references multiple image files.",
                        "Use a single-file dump: one .cue + one .bin in the same folder."));
                }

                var name = file.Groups[1].Value;
                var kind = file.Groups[2].Value.ToUpperInvariant();
                if (kind is not ("BINARY" or "MOTOROLA"))
                {
                    return (false, null, Fail(cuePath, null, "cue",
                        "Invalid FILE type in .cue",
                        $"FILE type is \"{kind}\", but a data dump must be BINARY.",
                        "Edit the .cue so the data track uses: FILE \"game.bin\" BINARY"));
                }

                if (name.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    return (false, null, Fail(cuePath, null, "cue",
                        "Invalid FILE name in .cue",
                        "The FILE path inside the cue sheet contains illegal characters.",
                        "Fix the quoted file name in the .cue, or re-dump with a normal name."));
                }

                var ext = Path.GetExtension(name);
                if (!ext.Equals(".bin", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".img", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".raw", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, null, Fail(cuePath, null, "cue",
                        ".cue does not point to a disc image",
                        $"The cue references \"{name}\", which is not a .bin/.img image.",
                        "The .cue must contain a line like: FILE \"Crash Bandicoot.bin\" BINARY"));
                }

                binPath = Path.GetFullPath(Path.Combine(dir, name));
                continue;
            }

            var track = TrackLine.Match(line);
            if (track.Success)
            {
                if (binPath == null)
                {
                    return (false, null, Fail(cuePath, null, "cue",
                        "Broken .cue sheet",
                        $"Line {lineNo}: TRACK appears before any FILE.",
                        "Use a real cue sheet from your dump tool, not an empty/renamed text file."));
                }

                if (!track.Groups[2].Value.Equals("AUDIO", StringComparison.OrdinalIgnoreCase))
                    sawDataTrack = true;
                continue;
            }

            var index = IndexLine.Match(line);
            if (index.Success)
            {
                if (binPath == null)
                {
                    return (false, null, Fail(cuePath, null, "cue",
                        "Broken .cue sheet",
                        $"Line {lineNo}: INDEX appears before any FILE.",
                        "Use a real cue sheet from your dump tool, not an empty/renamed text file."));
                }

                if (index.Groups[1].Value is "01" or "1")
                    sawIndex01 = true;
                continue;
            }

            if (line.StartsWith("FLAGS ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("PREGAP ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("POSTGAP ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("CATALOG ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("CDTEXTFILE ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("PERFORMER ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("TITLE ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("SONGWRITER ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("ISRC ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return (false, null, Fail(cuePath, null, "cue",
                "Not a valid .cue file",
                $"Line {lineNo} is not valid cue-sheet syntax. A renamed .txt is not enough.",
                "You need the real .cue from your dump, plus the matching .bin in the same folder."));
        }

        if (binPath == null)
        {
            return (false, null, Fail(cuePath, null, "cue",
                "Empty or fake .cue",
                "This file has no FILE \"….bin\" BINARY line — it is not a real cue sheet.",
                "Select the real Crash Bandicoot .cue from your dump folder (not an empty renamed .txt)."));
        }

        if (!sawDataTrack)
        {
            return (false, binPath, Fail(cuePath, binPath, "cue",
                "No data track in .cue",
                "The cue sheet has no MODE1/MODE2 data TRACK.",
                "Use the cue sheet generated with your dump — it must list a data track before audio tracks."));
        }

        if (!sawIndex01)
        {
            return (false, binPath, Fail(cuePath, binPath, "cue",
                "Incomplete .cue sheet",
                "The cue sheet is missing INDEX 01.",
                "Re-export the cue from your dumping tool, or restore the original .cue."));
        }

        var cueDir = Path.GetFullPath(Path.GetDirectoryName(cuePath) ?? "");
        var binDir = Path.GetFullPath(Path.GetDirectoryName(binPath) ?? "");
        if (!string.Equals(cueDir, binDir, StringComparison.OrdinalIgnoreCase))
        {
            return (false, binPath, Fail(cuePath, binPath, "pair",
                ".cue and .bin must stay together",
                "The .cue points to a .bin in a different folder.",
                "Move both files into the same folder, then select the .cue again."));
        }

        return (true, binPath, null);
    }

    static DiscValidation Fail(
        string cue, string? bin, string focus,
        string title, string problem, string fix)
    {
        var msg = string.IsNullOrWhiteSpace(problem) ? title : $"{title}: {problem}";
        return new DiscValidation(false, cue, bin, "", msg, title, problem, fix, focus);
    }
}
