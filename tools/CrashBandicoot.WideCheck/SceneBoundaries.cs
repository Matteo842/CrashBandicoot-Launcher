using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using RecompOne.Runtime;
using RecompOne.Runtime.Memory;

// Diagnostic only: highlights authored mesh boundaries, including ones hidden
// behind other surfaces. A highlighted edge is not proof of a missing surface.
static class SceneBoundaries
{
    readonly record struct Edge(Vector3 A, Vector3 B);
    readonly record struct Owner(int Polygon, int Material, Vector3 A, Vector3 B, Vector3 C);

    public static void Write(IMemory memory, string path, string frameFile,
        double wideWidth, int coreWidth, int height, int clipX, int clipY)
    {
        uint U(uint address) => memory.ReadU32(address);
        int I(uint address) => (int)U(address);
        static bool Pointer(uint address) => (address & 0xFFE00000u) == 0x80000000u;
        static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        static int Signed(int value) => (value & 4096) != 0 ? value - 8192 : value;
        uint entry = U(0x80057914);
        if (!Pointer(entry)) return;
        uint zone = U(entry + 16);
        if (!Pointer(zone) || U(zone) is 0 or > 8) return;
        double margin = (wideWidth - coreWidth) / 2;
        var matrix = Enumerable.Range(0, 9).Select(i => (short)memory.ReadU16(0x800577E4u + (uint)i * 2)).ToArray();
        int projection = I(0x800578D0);
        if (projection is <= 0 or > 4096) return;
        double near = projection / 2.0;
        XNamespace svg = "http://www.w3.org/2000/svg";
        var root = new XElement(svg + "svg", new XAttribute("width", margin > 0 ? 1280 : 960), new XAttribute("height", 720),
            new XAttribute("viewBox", $"0 0 {Number(wideWidth)} {height}"), new XAttribute("preserveAspectRatio", "none"),
            new XElement(svg + "title", "Authored mesh boundaries — hover an edge for its world, polygon and material"),
            new XElement(svg + "image", new XAttribute("href", frameFile), new XAttribute("width", Number(wideWidth)),
                new XAttribute("height", height), new XAttribute("preserveAspectRatio", "none")));
        root.Add(new XElement(svg + "defs", new XElement(svg + "clipPath", new XAttribute("id", "sides"),
            new XElement(svg + "rect", new XAttribute("width", Number(margin)), new XAttribute("height", height)),
            new XElement(svg + "rect", new XAttribute("x", Number(margin + coreWidth)),
                new XAttribute("width", Number(margin)), new XAttribute("height", height)))));
        var lines = new XElement(svg + "g", new XAttribute("clip-path", "url(#sides)"));
        root.Add(lines);
        for (uint wi = 0; wi < U(zone); wi++)
        {
            uint world = zone + 4 + wi * 64, header = U(world + 16), polygons = U(world + 20), vertices = U(world + 24);
            if (!Pointer(header) || !Pointer(polygons) || !Pointer(vertices) || U(header + 28) != 0) continue;
            int polyCount = I(header + 12), vertexCount = I(header + 16);
            if (polyCount is <= 0 or > 4096 || vertexCount is <= 0 or > 4096) continue;
            var points = new Vector3[vertexCount];
            for (int vi = 0; vi < vertexCount; vi++)
            {
                uint a = U(vertices + (uint)vi * 8), b = U(vertices + (uint)vi * 8 + 4);
                points[vi] = new Vector3(Signed((int)((b >> 3) & 8191)) * 8, Signed((int)((b >> 19) & 8191)) * 8,
                    Signed((int)((a >> 24) | ((b >> 1) & 3) << 8 | ((b >> 16) & 7) << 10)) * 8);
            }
            var edges = new Dictionary<Edge, List<Owner>>();
            for (int pi = 0; pi < polyCount; pi++)
            {
                uint a = U(polygons + (uint)pi * 8), b = U(polygons + (uint)pi * 8 + 4);
                int[] ids = [(int)(b >> 20), (int)((b >> 8) & 4095), (int)(a >> 20)];
                if (ids.Any(id => id >= vertexCount)) continue;
                for (int ei = 0; ei < 3; ei++)
                {
                    var p = points[ids[ei]]; var q = points[ids[(ei + 1) % 3]];
                    bool swap = p.X > q.X || p.X == q.X && (p.Y > q.Y || p.Y == q.Y && p.Z > q.Z);
                    var key = swap ? new Edge(q, p) : new Edge(p, q);
                    if (!edges.TryGetValue(key, out var owners)) edges[key] = owners = [];
                    owners.Add(new Owner(pi, (int)((a >> 8) & 4095), p, q, points[ids[(ei + 2) % 3]]));
                }
            }
            Vector3 Camera(Vector3 v) => new(
                (float)Math.Floor((matrix[0] * (double)v.X + matrix[1] * v.Y + matrix[2] * v.Z) / 4096) + I(world + 4),
                (float)Math.Floor((matrix[3] * (double)v.X + matrix[4] * v.Y + matrix[5] * v.Z) / 4096) + I(world + 8),
                (float)Math.Floor((matrix[6] * (double)v.X + matrix[7] * v.Y + matrix[8] * v.Z) / 4096) + I(world + 12));
            Vector2 Project(Vector3 v) => new(
                (float)(projection * Math.Clamp(v.X, -32768, 32767) / v.Z + Runtime.Gpu!.DrawOffsetX - clipX + margin),
                projection * Math.Clamp(v.Y, -32768, 32767) / v.Z + Runtime.Gpu!.DrawOffsetY - clipY);
            foreach (var owners in edges.Values)
            {
                if (owners.Count != 1) continue;
                var owner = owners[0]; var a = Camera(owner.A); var b = Camera(owner.B); var c = Camera(owner.C);
                if (a.Z < near && b.Z < near) continue;
                if (a.Z < near) a = Vector3.Lerp(a, b, (float)((near - a.Z) / (b.Z - a.Z)));
                if (b.Z < near) b = Vector3.Lerp(b, a, (float)((near - b.Z) / (a.Z - b.Z)));
                var pa = Project(a); var pb = Project(b);
                if (c.Z >= near)
                {
                    var pc = Project(c);
                    if ((pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X) <= 0) continue;
                }
                if (Math.Max(pa.X, pb.X) < 0 || Math.Min(pa.X, pb.X) > wideWidth
                    || Math.Max(pa.Y, pb.Y) < 0 || Math.Min(pa.Y, pb.Y) > height
                    || Math.Min(pa.X, pb.X) >= margin && Math.Max(pa.X, pb.X) <= margin + coreWidth) continue;
                lines.Add(new XElement(svg + "line", new XAttribute("x1", Number(pa.X)), new XAttribute("y1", Number(pa.Y)),
                    new XAttribute("x2", Number(pb.X)), new XAttribute("y2", Number(pb.Y)),
                    new XAttribute("stroke", "#ff36da"), new XAttribute("stroke-width", 1.5),
                    new XAttribute("vector-effect", "non-scaling-stroke"),
                    new XElement(svg + "title", $"World {wi}, origin ({I(header)}, {I(header + 4)}, {I(header + 8)}); polygon {owner.Polygon}; material {owner.Material}; {owner.A} → {owner.B}")));
            }
        }
        new XDocument(root).Save(path);
    }
}
