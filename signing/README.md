# Android upload keystore

Release APKs are signed with `android-release.keystore` in this folder.

Those files are **gitignored on purpose**:

- `android-release.keystore`
- `keystore.properties`

`python publish_release.py --platform android` creates them on first run.

Back up both files somewhere safe (password manager + offline copy). If they are
lost, Android will refuse updates over the existing app id — users would have to
uninstall first, and Play Console would treat it as a different app.
