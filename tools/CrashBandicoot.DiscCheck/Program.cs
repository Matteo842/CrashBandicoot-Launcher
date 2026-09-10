using System.Diagnostics;
using CrashBandicoot.Launcher.Recomp;
using RecompOne.Runtime.Cdrom;

if (args.Length != 2 && args.Length != 4)
{
    Console.Error.WriteLine("Usage: DiscCheck <chdman> <scratch-directory> [original.cue converted.chd]");
    return 2;
}

string chdman = Path.GetFullPath(args[0]);
string scratch = Path.Combine(Path.GetFullPath(args[1]), Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
Console.WriteLine($"Synthetic test files: {scratch}");

// Original test data only. No retail disc contents are needed for these checks.
string bin = Path.Combine(scratch, "sectors.bin");
byte[] sector = new byte[2352];
var random = new Random(94900);
using (var output = File.Create(bin))
    for (int i = 0; i < 37; i++)
    {
        random.NextBytes(sector);
        // Retain nonzero headers/payload but compress enough to exercise each codec,
        // rather than chdman's fallback to storing incompressible hunks verbatim.
        sector.AsSpan(256).Clear();
        output.Write(sector);
    }

string cue = WriteCue("sectors.cue", "    INDEX 01 00:00:00");
foreach (string codec in new[] { "none", "cdzl", "cdlz", "cdfl", "cdzs" })
{
    string chd = Path.Combine(scratch, codec + ".CHD");
    RunChdman("createcd", "-i", cue, "-o", chd, "-c", codec);
    if (codec != "none")
        Check(new FileInfo(chd).Length < new FileInfo(bin).Length, "Fixture uses compression: " + codec);
    Compare(cue, chd);
    var validation = DiscValidator.Validate(chd);
    Check(!validation.Ok && validation.Title == "CHD looks incomplete", "Tiny CHD is rejected by decoded size");
}

// Exercise INDEX 00 data and a virtual PREGAP separately: only the former is stored.
foreach (var (name, indexes) in new[]
{
    ("stored-pregap", "    INDEX 00 00:00:00\n    INDEX 01 00:00:03"),
    ("virtual-pregap", "    PREGAP 00:02:00\n    INDEX 01 00:00:00")
})
{
    string gapCue = WriteCue(name + ".cue", indexes);
    string gapChd = Path.Combine(scratch, name + ".chd");
    RunChdman("createcd", "-i", gapCue, "-o", gapChd, "-c", "cdzl");
    Compare(gapCue, gapChd);
}

string rawChd = Path.Combine(scratch, "not-a-cd.chd");
RunChdman("createraw", "-i", bin, "-o", rawChd, "-hs", "4096", "-us", "1", "-c", "zlib");
ExpectUnreadable(rawChd);
string multiCue = Path.Combine(scratch, "multi.cue");
File.WriteAllText(multiCue,
    "FILE \"sectors.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n  TRACK 02 AUDIO\n    INDEX 01 00:00:20\n");
string multiChd = Path.Combine(scratch, "multi.chd");
RunChdman("createcd", "-i", multiCue, "-o", multiChd, "-c", "cdzl");
ExpectUnreadable(multiChd);
string childChd = Path.Combine(scratch, "child.chd");
RunChdman("createcd", "-i", cue, "-o", childChd, "-op", Path.Combine(scratch, "cdzl.CHD"), "-c", "cdzl");
ExpectUnreadable(childChd);
string fake = Path.Combine(scratch, "fake.chd");
File.WriteAllText(fake, "This is not a disc image.");
ExpectUnreadable(fake);
Check(!DiscValidator.Validate(Path.Combine(scratch, "missing.chd")).Ok, "Missing CHD rejected");

string truncated = Path.Combine(scratch, "truncated.chd");
File.WriteAllBytes(truncated, File.ReadAllBytes(Path.Combine(scratch, "cdzl.CHD"))[..130]);
ExpectUnreadable(truncated);

// A small compressed file can legitimately contain a full-size data track.
using (var output = new FileStream(bin, FileMode.Open, FileAccess.Write))
    output.SetLength(36000L * 2352);
string sparse = Path.Combine(scratch, "large-track.chd");
RunChdman("createcd", "-i", cue, "-o", sparse, "-c", "cdzl");
using (var fs = CueFs.Open(sparse))
    Check(fs.DataTrackBytes == 36000L * 2352, "Decoded size excludes subchannels and padding");
var sparseValidation = DiscValidator.Validate(sparse);
Check(!sparseValidation.Ok && sparseValidation.Title == "Not a disc image",
    "A compressed file under 80 MB reaches content validation when decoded track is large enough");

if (args.Length == 4)
{
    string original = Path.GetFullPath(args[2]);
    string converted = Path.GetFullPath(args[3]);
    var cueValidation = DiscValidator.Validate(original);
    var chdValidation = DiscValidator.Validate(converted);
    Check(cueValidation.Ok, "Original game validates: " + cueValidation.Message);
    Check(chdValidation.Ok, "CHD game validates: " + chdValidation.Message);
    Check(chdValidation.BinPath == null && chdValidation.CuePath == converted,
        "CHD validation retains the selected file and requires no BIN");
    Check(DiscValidator.EnsureDiscPresentForLaunch(converted, chdValidation.Fingerprint).Ok,
        "CHD launch gate accepts the matching fingerprint");
    Check(!DiscValidator.EnsureDiscPresentForLaunch(converted, "wrong-fingerprint").Ok,
        "CHD launch gate rejects a changed disc");
    Compare(original, converted);
    using var a = CueFs.Open(original);
    using var b = CueFs.Open(converted);
    foreach (string file in new[] { "SYSTEM.CNF", DiscValidator.ExpectedBoot })
        Check(a.ReadFileWithoutOverlay(file).AsSpan().SequenceEqual(b.ReadFileWithoutOverlay(file)),
            "Identical ISO file: " + file);
}

Console.WriteLine("PASS: all disc checks passed.");
return 0;

string WriteCue(string name, string indexes)
{
    string path = Path.Combine(scratch, name);
    File.WriteAllText(path, $"FILE \"sectors.bin\" BINARY\n  TRACK 01 MODE2/2352\n{indexes}\n");
    return path;
}

void RunChdman(params string[] arguments)
{
    var start = new ProcessStartInfo(chdman)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new Exception("Cannot start chdman");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new Exception($"chdman failed: {stdout.Result}\n{stderr.Result}");
}

static void Compare(string cue, string chd)
{
    using var expected = CueFs.Open(cue);
    using var actual = CueFs.Open(chd);
    Check(expected.DataTrackBytes == actual.DataTrackBytes, "Track lengths match");
    int count = checked((int)(expected.DataTrackBytes / 2352));
    for (int lba = 0; lba < count; lba++)
    {
        if (!expected.ReadSectorData(lba, 2340).AsSpan().SequenceEqual(actual.ReadSectorData(lba, 2340)))
            throw new Exception($"Raw sector mismatch at {lba}: {chd}");
        if (lba > 0 && lba % 50000 == 0) Console.WriteLine($"Compared {lba:N0}/{count:N0} sectors...");
    }
    Parallel.For(0, 512, i =>
    {
        int lba = i < 3 ? new[] { -1, count, count + 1 }[i] : (i * 7919) % count;
        foreach (int size in new[] { 0, 2048, 2328, 2329, 2340, 2352 })
            if (!expected.ReadSectorData(lba, size).AsSpan().SequenceEqual(actual.ReadSectorData(lba, size)))
                throw new Exception($"Read mode mismatch at {lba}, size {size}: {chd}");
    });
    Console.WriteLine($"PASS: {Path.GetFileName(chd)} — {count:N0} sectors, read modes, bounds and concurrent seeks");
}

static void ExpectUnreadable(string path)
{
    Check(!DiscValidator.Validate(path).Ok, "Invalid CHD rejected: " + Path.GetFileName(path));
    try
    {
        using var fs = CueFs.Open(path);
        fs.ReadSector(0);
    }
    catch (InvalidDataException) { return; }
    throw new Exception("Expected CHD read failure: " + path);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
}
