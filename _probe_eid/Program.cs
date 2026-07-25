using RecompOne.Runtime.Cdrom;
using var fs = CueFs.Open(@"D:\GitHub\RecompOne\Crash Bandicoot.cue");
foreach (var id in new[]{3,5,26}) {
  var name=$"S{id:D7}.NSD";
  fs.Locate(name, out int lba, out uint size);
  var nsd = fs.ReadSectors(lba, (int)size);
  uint hc = BitConverter.ToUInt32(nsd, 0x404);
  int lh = 0x520 + (int)hc * 8;
  Console.WriteLine($"\n=== {name} lh@0x{lh:X} ===");
  for (int i=0;i<6;i++) {
    uint w = BitConverter.ToUInt32(nsd, lh + i*4);
    Console.WriteLine($"  +{i*4:X2}: {w:X8}");
  }
  Console.WriteLine("  execeidmap[0..7] @+0x14:");
  for (int i=0;i<8;i++) Console.Write($" {BitConverter.ToUInt32(nsd, lh+0x14+i*4):X8}");
  Console.WriteLine();
  int img = lh + 0x118;
  Console.WriteLine($"  image@{img:X} first words: {BitConverter.ToUInt32(nsd,img):X8} {BitConverter.ToUInt32(nsd,img+4):X8}");
  Console.WriteLine($"  trim_end=0x{hc*8+0x730:X} img_start=0x{img:X} delta={img-(int)(hc*8+0x730)}");
}
