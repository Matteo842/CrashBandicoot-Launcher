using System.Text;
using System.Text.RegularExpressions;

namespace CrashBandicoot.Launcher.Recomp;

/// <summary>
/// Applies the known Crash SCUS-94900 control-flow fixes that the recompiler
/// does not yet emit (decompressor merge, Duff devices, bgez jump tables,
/// NSD realloc hash-pointer retarget, geom clip jump-table jr→return).
/// </summary>
public static class PostPassApplier
{
    public static void Apply(string mainCsPath, string patchPath)
    {
        if (!File.Exists(mainCsPath))
            throw new FileNotFoundException("Generated main.cs not found.", mainCsPath);
        if (!File.Exists(patchPath))
            throw new FileNotFoundException("Post-pass patch not found.", patchPath);

        var original = NormalizeLf(File.ReadAllText(mainCsPath));
        var patch = NormalizeLf(File.ReadAllText(patchPath));
        var patched = ApplyUnified(original, patch);
        patched = FixGeomJumpTableReturns(patched);
        File.WriteAllText(mainCsPath, patched.Replace("\n", Environment.NewLine));
    }

    /// <summary>
    /// SCUS-94900 world/model geom: recompiler emits <c>return</c> instead of
    /// <c>jr</c> into a clip flag jump-table after clobbering GP/SP as temps,
    /// and splits 80036BF4/80036D9C so the triangle loop's continue at 80036CAC
    /// becomes an unmapped <c>Dispatcher.Call</c>.
    /// </summary>
    static string FixGeomJumpTableReturns(string src)
    {
        if (src.Contains("func_80036BF4_impl", StringComparison.Ordinal) &&
            src.Contains("L80036CAC:", StringComparison.Ordinal))
            return src; // already merged

        // 80036340: fall through into inlined table body (restores SP/GP at epilogue).
        src = ReplaceOnce(src,
            """
                    c.RA = 0x80030000u;
                    c.RA = c.RA + 0x64D4u;
                    c.SP = c.At << 7;
                    c.RA = c.RA + c.SP;
                    c.FP = ~(c.GP | 0u);
                    return;
                    RecompOne.Runtime.Gte.Write(6, c.S3);
            """,
            """
                    c.RA = 0x80030000u;
                    c.RA = c.RA + 0x64D4u;
                    c.SP = c.At << 7;
                    c.RA = c.RA + c.SP;
                    c.FP = ~(c.GP | 0u);
                    // jr→return footgun: fall through into inlined clip jump-table.
                    RecompOne.Runtime.Gte.Write(6, c.S3);
            """);

        // Merge 80036BF4 + 80036D9C (loop continue at 80036CAC must be a goto).
        src = ReplaceOnce(src,
            """
                public static void func_80036BF4(CpuContext c, IMemory m)
                {
                    c.At = 0x1F800000u;
                    m.WriteU32(c.At, c.S0);
            """,
            """
                public static void func_80036BF4(CpuContext c, IMemory m) => func_80036BF4_impl(c, m, 0);
                [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
                public static void func_80036D9C(CpuContext c, IMemory m) => func_80036BF4_impl(c, m, 1);
                // 80036BF4/80036D9C are one geom routine incorrectly split; loop continues at 80036CAC.
                static void func_80036BF4_impl(CpuContext c, IMemory m, int entry)
                {
                    if (entry != 0) goto L80036D9C;
                    c.At = 0x1F800000u;
                    m.WriteU32(c.At, c.S0);
            """);

        src = ReplaceOnce(src,
            """
                    c.A3 = c.At + 0u;
                    c.A0 = c.RA + 0u;
                    c.At = m.ReadU16(c.T2);
                    c.A2 = c.A2 << 2;
                    c.V1 = 0u | 0x1FFCu;
                    c.T2 = c.T2 - 0x2u;
                    c.FP = c.At >> 12;
            """,
            """
                    c.A3 = c.At + 0u;
                    c.A0 = c.RA + 0u;
                    c.At = m.ReadU16(c.T2);
                    c.A2 = c.A2 << 2;
                    c.V1 = 0u | 0x1FFCu;
                    L80036CAC: ;
                    c.T2 = c.T2 - 0x2u;
                    c.FP = c.At >> 12;
            """);

        src = ReplaceOnce(src,
            """
                    c.RA = 0x80030000u;
                    c.RA = c.RA + 0x6D9Cu;
                    c.SP = c.SP << 7;
                    c.RA = c.RA + c.SP;
                    c.FP = ~(c.GP | 0u);
                    return;
                }
                [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
                public static void func_80036D9C(CpuContext c, IMemory m)
                {
                    RecompOne.Runtime.Gte.Write(6, c.S3);
            """,
            """
                    c.RA = 0x80030000u;
                    c.RA = c.RA + 0x6D9Cu;
                    c.SP = c.SP << 7;
                    c.RA = c.RA + c.SP;
                    c.FP = ~(c.GP | 0u);
                    goto L80036D9C;
                    L80036D9C: ;
                    RecompOne.Runtime.Gte.Write(6, c.S3);
            """);

        src = ReplaceOnce(src,
            """
                    if (c.T5 != 0u) {
                        c.At = m.ReadU16(c.T2);
                        Dispatcher.Call(c, m, 0x80036CACu);
                        return;
                    }
            """,
            """
                    if (c.T5 != 0u) {
                        c.At = m.ReadU16(c.T2);
                        goto L80036CAC;
                    }
            """);

        return src;
    }

    static string ReplaceOnce(string src, string oldText, string newText)
    {
        oldText = NormalizeLf(oldText);
        newText = NormalizeLf(newText);
        var idx = src.IndexOf(oldText, StringComparison.Ordinal);
        if (idx < 0)
            throw new InvalidDataException(
                "Geom jump-table post-pass pattern not found. Recompiler output may have changed.\n" +
                "Missing:\n" + oldText.Split('\n').FirstOrDefault());
        return src.Substring(0, idx) + newText + src.Substring(idx + oldText.Length);
    }

    static string NormalizeLf(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    static string ApplyUnified(string original, string patch)
    {
        var src = original.Split('\n').Select(l => l + "\n").ToList();
        // If original ended without newline, last split item is wrong — handle:
        if (!original.EndsWith('\n') && src.Count > 0)
            src[^1] = src[^1].TrimEnd('\n');

        var lines = patch.Split('\n');
        var output = new List<string>();
        var i = 0;
        var idx = 0;

        while (idx < lines.Length)
        {
            var line = lines[idx];
            if (line.StartsWith("---") || line.StartsWith("+++") || line.StartsWith("diff"))
            {
                idx++;
                continue;
            }

            var m = Regex.Match(line, @"@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@");
            if (!m.Success)
            {
                idx++;
                continue;
            }

            var oldStart = int.Parse(m.Groups[1].Value) - 1;
            while (i < oldStart && i < src.Count)
            {
                output.Add(src[i]);
                i++;
            }

            idx++;
            while (idx < lines.Length &&
                   !lines[idx].StartsWith("@@") &&
                   !lines[idx].StartsWith("diff"))
            {
                var pl = lines[idx];
                if (pl.StartsWith('\\'))
                {
                    idx++;
                    continue;
                }

                if (pl.Length == 0)
                {
                    idx++;
                    continue;
                }

                var tag = pl[0];
                var content = pl.Length > 1 ? pl[1..] : "";
                content += "\n";

                switch (tag)
                {
                    case ' ':
                        EnsureMatch(src, i, content);
                        output.Add(src[i]);
                        i++;
                        break;
                    case '-':
                        EnsureMatch(src, i, content);
                        i++;
                        break;
                    case '+':
                        output.Add(content);
                        break;
                    default:
                        throw new InvalidDataException($"Bad patch tag '{tag}' at patch line {idx + 1}");
                }

                idx++;
            }
        }

        while (i < src.Count)
        {
            output.Add(src[i]);
            i++;
        }

        var sb = new StringBuilder(original.Length + 4096);
        foreach (var l in output) sb.Append(l);
        var result = sb.ToString();
        if (original.EndsWith('\n') && !result.EndsWith('\n'))
            result += "\n";
        if (!original.EndsWith('\n') && result.EndsWith('\n'))
            result = result.TrimEnd('\n');
        return result;
    }

    static void EnsureMatch(List<string> src, int i, string content)
    {
        if (i >= src.Count)
            throw new InvalidDataException($"Patch ran past end of file at line {i + 1}");
        var a = src[i].TrimEnd('\n', '\r');
        var b = content.TrimEnd('\n', '\r');
        if (!string.Equals(a, b, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Post-pass context mismatch at line {i + 1}. Recompiler output may have changed — regenerate the patch.\nExpected: {b}\nGot: {a}");
    }
}
