# Disc reader checks

Requires .NET 10 and [MAME's chdman](https://docs.mamedev.org/tools/chdman.html).

```text
dotnet run --project tools/CrashBandicoot.DiscCheck -c Release -- /path/to/chdman artifacts/disc-check
```

Creates original synthetic disc data in a unique scratch subdirectory. Checks uncompressed CHD and the CD Deflate, LZMA, FLAC, and Zstd codecs; stored and virtual pregaps; sector boundaries, read sizes, concurrent seeks, malformed/truncated files, non-CD images, multi-track/parent-dependent images, and compressed versus logical size validation. Test files remain in the scratch directory for inspection.

Optionally append paths to your own matching Crash NTSC-U CUE and CHD:

```text
dotnet run --project tools/CrashBandicoot.DiscCheck -c Release -- /path/to/chdman artifacts/disc-check /path/to/game.cue /path/to/game.chd
```

This also compares every sector's 2340-byte PS1 read payload, the boot executable, and `SYSTEM.CNF`; verifies both disc validators and the CHD launch fingerprint gate; and checks random/concurrent reads in all supported sizes. The CHD must have been created from that CUE/BIN. No disc image or retail game data is included in this tool.
