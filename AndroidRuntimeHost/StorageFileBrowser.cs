using System.Text.RegularExpressions;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using IOPath = System.IO.Path;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>
/// In-app file list over real filesystem paths. Used after All files access
/// is granted, so .cue/.bin dumps under /sdcard/Rom (and similar) are readable.
/// </summary>
static class StorageFileBrowser
{
    const string LastDirPref = "disc_browser_dir";

    static readonly Color Night = Color.Rgb(6, 16, 24);
    static readonly Color Card = Color.Rgb(11, 35, 27);
    static readonly Color Wumpa = Color.Rgb(255, 138, 0);
    static readonly Color Sand = Color.Rgb(244, 228, 188);
    static readonly Color Muted = Color.Rgb(177, 188, 188);

    static readonly Regex CueFileLine = new(
        "(?im)^\\s*FILE\\s+\"([^\"]+)\"\\s+(?:BINARY|MOTOROLA)\\s*$",
        RegexOptions.Compiled);

    public static void ShowDisc(Activity activity, Action<string, string> onCueAndBin)
        => Show(activity, "Select the .cue or .bin", DiscFilter, path =>
        {
            if (!TryPairDisc(path, out var cue, out var bin, out var error))
            {
                Toast.MakeText(activity, error, ToastLength.Long)?.Show();
                return false;
            }

            onCueAndBin(cue, bin);
            return true;
        });

    public static void ShowZip(Activity activity, Action<string> onZip)
        => Show(activity, "Select a mod .zip", ZipFilter, path =>
        {
            onZip(path);
            return true;
        });

    static bool DiscFilter(string name) =>
        name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

    static bool ZipFilter(string name) =>
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    static void Show(Activity activity, string heading, Func<string, bool> fileFilter, Func<string, bool> onFile)
    {
        var prefs = activity.GetPreferences(FileCreationMode.Private);
        var start = prefs.GetString(LastDirPref, null);
        if (string.IsNullOrWhiteSpace(start) || !Directory.Exists(start))
            start = AndroidStorageAccess.PrimaryStoragePath();

        var dialog = new Dialog(activity);
        dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);
        dialog.SetCanceledOnTouchOutside(true);

        var root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(activity, 18), Dp(activity, 14), Dp(activity, 18), Dp(activity, 14));
        root.SetBackgroundColor(Card);

        var title = new TextView(activity)
        {
            Text = heading,
            TextSize = 18,
        };
        title.SetTextColor(Wumpa);
        root.AddView(title);

        var pathLabel = new TextView(activity) { TextSize = 11 };
        pathLabel.SetTextColor(Muted);
        pathLabel.SetPadding(0, Dp(activity, 6), 0, Dp(activity, 8));
        root.AddView(pathLabel);

        var list = new ListView(activity);
        list.CacheColorHint = Color.Transparent;
        list.Divider = new ColorDrawable(Color.Argb(40, 255, 255, 255));
        list.DividerHeight = 1;
        root.AddView(list, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var cancel = new Button(activity) { Text = "Cancel", TextSize = 13 };
        cancel.SetTextColor(Sand);
        cancel.Background = new ColorDrawable(Color.Transparent);
        cancel.Click += (_, _) => dialog.Dismiss();
        root.AddView(cancel, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.End,
        });

        dialog.SetContentView(root, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        dialog.Window?.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        dialog.Window?.SetBackgroundDrawable(new ColorDrawable(Night));

        var current = start;
        List<Entry> entries = [];

        void Reload()
        {
            prefs.Edit()!.PutString(LastDirPref, current)!.Apply();
            pathLabel.Text = current;
            entries = BuildEntries(activity, current, fileFilter);
            list.Adapter = new FileListAdapter(activity, entries);
        }

        list.ItemClick += (_, e) =>
        {
            if (e.Position < 0 || e.Position >= entries.Count) return;
            var picked = entries[e.Position];
            if (picked.IsDir)
            {
                current = picked.Path;
                Reload();
                return;
            }

            if (onFile(picked.Path))
                dialog.Dismiss();
        };

        Reload();
        dialog.Show();
    }

    static List<Entry> BuildEntries(Activity activity, string current, Func<string, bool> fileFilter)
    {
        var entries = new List<Entry>();
        var roots = AndroidStorageAccess.StorageRoots(activity);
        if (current.Length == 0)
        {
            foreach (var root in roots)
                entries.Add(new Entry($"[dir]  {root.Label}", root.Path, true));
            return entries;
        }

        var atRoot = roots.Exists(r =>
            string.Equals(r.Path.TrimEnd('/'), current.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        if (atRoot)
        {
            if (roots.Count > 1)
                entries.Add(new Entry("← Storage locations", "", true));
        }
        else
        {
            var parent = Directory.GetParent(current)?.FullName;
            if (!string.IsNullOrEmpty(parent))
                entries.Add(new Entry("← ..", parent, true));
        }

        foreach (var dir in ListDirectories(current))
        {
            var name = IOPath.GetFileName(dir.TrimEnd(IOPath.DirectorySeparatorChar, '/'));
            if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;
            entries.Add(new Entry($"[dir]  {name}", dir, true));
        }

        foreach (var file in ListFiles(current))
        {
            var name = IOPath.GetFileName(file);
            if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;
            if (!fileFilter(name)) continue;
            var size = TrySizeLabel(file);
            entries.Add(new Entry($"   {name}{size}", file, false));
        }

        return entries;
    }

    static string TrySizeLabel(string path)
    {
        try
        {
            var len = new FileInfo(path).Length;
            if (len <= 0) return "  (0 MB)";
            if (len < 1024 * 1024) return $"  ({len / 1024} KB)";
            return $"  ({len / (1024d * 1024d):0} MB)";
        }
        catch
        {
            return "";
        }
    }

    static IEnumerable<string> ListDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return JavaList(path, dirs: true);
        }
    }

    static IEnumerable<string> ListFiles(string path)
    {
        try
        {
            return Directory.GetFiles(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return JavaList(path, dirs: false);
        }
    }

    static IEnumerable<string> JavaList(string path, bool dirs)
    {
        try
        {
            var listed = new Java.IO.File(path).ListFiles();
            if (listed == null) return [];
            return listed
                .Where(f => dirs ? f.IsDirectory : f.IsFile)
                .Select(f => f.AbsolutePath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return [];
        }
    }

    public static bool TryPairDisc(string selectedPath, out string cuePath, out string binPath, out string error)
    {
        cuePath = "";
        binPath = "";
        error = "";
        var dir = IOPath.GetDirectoryName(selectedPath) ?? "";
        var name = IOPath.GetFileName(selectedPath);

        if (name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
        {
            cuePath = selectedPath;
            string cueText;
            try { cueText = File.ReadAllText(selectedPath); }
            catch (Exception ex)
            {
                error = "Cannot read the .cue: " + ex.Message;
                return false;
            }

            var binName = BinNameFromCue(cueText);
            if (binName == null)
            {
                error = "The .cue has no FILE ... BINARY line.";
                return false;
            }

            binPath = FindSibling(dir, binName) ?? "";
            if (binPath.Length == 0 || !File.Exists(binPath))
            {
                error = $"Put {binName} in the same folder as the .cue.";
                return false;
            }

            return true;
        }

        if (name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            binPath = selectedPath;
            var guessed = IOPath.ChangeExtension(selectedPath, ".cue");
            if (File.Exists(guessed))
            {
                cuePath = guessed;
                return true;
            }

            foreach (var cue in ListFiles(dir).Where(p =>
                         p.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var referenced = BinNameFromCue(File.ReadAllText(cue));
                    if (referenced != null &&
                        referenced.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        cuePath = cue;
                        return true;
                    }
                }
                catch
                {
                    // skip unreadable sheets
                }
            }

            error = "Pick the matching .cue in the same folder.";
            return false;
        }

        error = "Pick the .cue (or the .bin next to it).";
        return false;
    }

    static string? FindSibling(string dir, string fileName)
    {
        var exact = IOPath.Combine(dir, fileName);
        if (File.Exists(exact)) return exact;
        try
        {
            return Directory.GetFiles(dir)
                .FirstOrDefault(p => IOPath.GetFileName(p).Equals(fileName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    static string? BinNameFromCue(string cueText)
    {
        var match = CueFileLine.Match(cueText);
        return match.Success
            ? IOPath.GetFileName(match.Groups[1].Value.Replace('\\', '/'))
            : null;
    }

    static int Dp(Activity activity, int value)
        => (int)(value * activity.Resources!.DisplayMetrics!.Density + 0.5f);

    sealed class FileListAdapter : BaseAdapter<Entry>
    {
        readonly Activity _activity;
        readonly IList<Entry> _items;

        public FileListAdapter(Activity activity, IList<Entry> items)
        {
            _activity = activity;
            _items = items;
        }

        public override int Count => _items.Count;
        public override Entry this[int position] => _items[position];
        public override long GetItemId(int position) => position;

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var text = convertView as TextView ?? new TextView(_activity);
            var item = _items[position];
            text.Text = item.Label;
            text.TextSize = 15;
            text.SetTextColor(item.IsDir ? Wumpa : Sand);
            text.SetPadding(Dp(_activity, 8), Dp(_activity, 10), Dp(_activity, 8), Dp(_activity, 10));
            return text;
        }
    }

    sealed record Entry(string Label, string Path, bool IsDir);
}
