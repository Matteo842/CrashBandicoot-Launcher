using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.IO;
var path = args[0];
using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();
foreach (var h in md.MethodDefinitions) {
  var m = md.GetMethodDefinition(h);
  var name = md.GetString(m.Name);
  if (name != "func_80015B58") continue;
  var body = pe.GetMethodBody(m.RelativeVirtualAddress);
  Console.WriteLine($"func_80015B58 IL len={body.GetILContent().Length}");
  var il = body.GetILContent();
  // look for ldc.i4 0x520 (1312) near other constants - rough
  int hits520=0, hits408=0;
  for (int i=0;i<il.Length-4;i++) {
    // ldc.i4 = 0x20 followed by int32 little endian
    if (il[i]==0x20) {
      int v = BitConverter.ToInt32(il, i+1);
      if (v==0x520) hits520++;
      if (v==0x408) hits408++;
    }
  }
  Console.WriteLine($"ldc.i4 0x520 hits={hits520} 0x408 hits={hits408}");
}
