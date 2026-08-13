using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.OS.Storage;
using Android.Provider;
using Android.Widget;
using OSEnvironment = Android.OS.Environment;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>
/// Shared-storage access for disc dumps. SAF (OPEN_DOCUMENT / OPEN_DOCUMENT_TREE)
/// is unusable on several Android 16 / One UI trees: the picker returns a URI
/// whose size is 0 and whose stream cannot be read. Emulators use the special
/// "All files access" permission and then read <c>/sdcard/...</c> as real files.
/// </summary>
static class AndroidStorageAccess
{
    public const int RequestAllFiles = 952;
    public const int RequestLegacyRead = 953;

    public static bool HasFullAccess(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
            return OSEnvironment.IsExternalStorageManager;
        return context.CheckSelfPermission(Android.Manifest.Permission.ReadExternalStorage)
               == Permission.Granted;
    }

    public static void ExplainAndRequest(Activity activity, Action willLeaveForSettings)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var dialog = new AlertDialog.Builder(activity)
                .SetTitle("File access")
                .SetMessage(
                    "This app needs All files access to read your Crash Bandicoot .cue/.bin dump, " +
                    "the same permission other emulators use. Android's folder picker does not " +
                    "give access to those files on this phone.\n\n" +
                    "On the next screen, enable All files access for Crash Bandicoot Launcher, " +
                    "then return here and pick the .cue.")
                .SetPositiveButton("Continue", (_, _) =>
                {
                    willLeaveForSettings();
                    OpenAllFilesSettings(activity);
                })
                .SetNegativeButton("Cancel", (_, _) => { })
                .Create();
            dialog?.Show();
            return;
        }

        activity.RequestPermissions(
            [Android.Manifest.Permission.ReadExternalStorage,
             Android.Manifest.Permission.WriteExternalStorage],
            RequestLegacyRead);
    }

    public static void OpenAllFilesSettings(Activity activity)
    {
        try
        {
            var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission);
            intent.SetData(Android.Net.Uri.Parse("package:" + activity.PackageName));
            activity.StartActivityForResult(intent, RequestAllFiles);
        }
        catch
        {
            var fallback = new Intent(Settings.ActionManageAllFilesAccessPermission);
            activity.StartActivityForResult(fallback, RequestAllFiles);
        }
    }

    public static string PrimaryStoragePath()
        => OSEnvironment.ExternalStorageDirectory?.AbsolutePath
           ?? "/storage/emulated/0";

    public static List<(string Label, string Path)> StorageRoots(Context context)
    {
        var roots = new List<(string, string)>();
        var primary = PrimaryStoragePath();
        roots.Add(("Internal storage", primary));

        if (!OperatingSystem.IsAndroidVersionAtLeast(24))
            return roots;

        if (context.GetSystemService(Context.StorageService) is not StorageManager manager)
            return roots;

        foreach (var volume in manager.StorageVolumes)
        {
            string? path = null;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
                path = volume.Directory?.AbsolutePath;
            if (string.IsNullOrEmpty(path))
                continue;
            if (roots.Exists(r => string.Equals(r.Item2, path, StringComparison.Ordinal)))
                continue;
            var label = volume.GetDescription(context);
            if (string.IsNullOrWhiteSpace(label))
                label = volume.IsRemovable ? "SD card" : "Storage";
            roots.Add((label, path));
        }

        return roots;
    }

    public static string? TryFilesystemPath(Context context, Android.Net.Uri uri)
    {
        if (uri.Scheme != null &&
            uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            return uri.Path;

        var authority = uri.Authority ?? "";
        string? docId = null;
        try { docId = DocumentsContract.GetDocumentId(uri); }
        catch { /* not a document URI */ }
        if (docId == null)
        {
            try { docId = DocumentsContract.GetTreeDocumentId(uri); }
            catch { /* ignore */ }
        }

        if (authority == "com.android.externalstorage.documents" && docId != null)
        {
            var colon = docId.IndexOf(':');
            if (colon >= 0)
            {
                var volume = docId[..colon];
                var relative = docId[(colon + 1)..].Replace('/', Path.DirectorySeparatorChar);
                if (volume.Equals("primary", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(PrimaryStoragePath(), relative);
                if (volume.Length > 0)
                    return Path.Combine("/storage", volume, relative);
            }
        }

        if (authority == "com.android.providers.downloads.documents" && docId != null)
        {
            if (docId.StartsWith("raw:", StringComparison.OrdinalIgnoreCase))
                return docId[4..];
        }

        try
        {
            using var cursor = context.ContentResolver?.Query(uri, ["_data"], null, null, null);
            if (cursor != null && cursor.MoveToFirst())
            {
                var index = cursor.GetColumnIndex("_data");
                if (index >= 0)
                {
                    var path = cursor.GetString(index);
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        return path;
                }
            }
        }
        catch
        {
            // Some providers reject _data once scoped storage is on.
        }

        return File.Exists(uri.Path ?? "") ? uri.Path : null;
    }
}
