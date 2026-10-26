using System.Numerics;

namespace WideEditor;

readonly record struct Rect(float X, float Y, float W, float H)
{
    public bool Contains(Vector2 p) => p.X >= X && p.Y >= Y && p.X < X + W && p.Y < Y + H;
}

/// <summary>The game's camera: pitch/yaw like a camera path point, the 16:9 view of the
/// native widescreen renderer (vertical half-angle tan 2/3, 4:3 core in the middle).</summary>
sealed class Camera
{
    public Vector3 Pos;
    public float Pitch, Yaw;
    public const float TanY = 2f / 3f, TanX = TanY * 16f / 9f, Near = 24, Far = 200000;
    public const float CoreFraction = 0.75f;

    public Vector3 Forward => new(-MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), -MathF.Cos(Pitch) * MathF.Cos(Yaw));
    public Vector3 Right => new(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw));
    public Vector3 Up => Vector3.Cross(Right, Forward);

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Pos, Pos + Forward, Up);

    public static Matrix4x4 Proj
    {
        get
        {
            float f = 1 / TanY, a = 16f / 9f;
            return new Matrix4x4(
                f / a, 0, 0, 0,
                0, f, 0, 0,
                0, 0, (Far + Near) / (Near - Far), -1,
                0, 0, 2 * Far * Near / (Near - Far), 0);
        }
    }

    public void SetPathPoint(Vector3 pos, int pitch, int yaw)
    {
        Pos = pos;
        Pitch = pitch * MathF.Tau / 4096;
        Yaw = yaw * MathF.Tau / 4096;
    }

    /// <summary>From the GTE matrix the game logs (rows scaled by 4096, screen Y by 0.625).</summary>
    public void SetMatrix(Vector3 pos, short[] m)
    {
        Pos = pos;
        var f = Vector3.Normalize(new Vector3(m[6], m[7], m[8]));
        Pitch = MathF.Asin(Math.Clamp(f.Y, -1, 1));
        Yaw = MathF.Atan2(-f.X, -f.Z);
    }

    public void ClampPitch() => Pitch = Math.Clamp(Pitch, -1.55f, 1.55f);

    public bool Project(Vector3 p, Rect r, out Vector2 s)
    {
        var d = p - Pos;
        float z = Vector3.Dot(d, Forward);
        s = default;
        if (z < Near) return false;
        float x = Vector3.Dot(d, Right) / (z * TanX), y = Vector3.Dot(d, Up) / (z * TanY);
        s = new(r.X + (x * 0.5f + 0.5f) * r.W, r.Y + (0.5f - y * 0.5f) * r.H);
        return true;
    }

    public Vector3 Ray(Vector2 s, Rect r)
    {
        float x = (s.X - r.X) / r.W * 2 - 1, y = 1 - (s.Y - r.Y) / r.H * 2;
        return Vector3.Normalize(Forward + Right * (x * TanX) + Up * (y * TanY));
    }

    /// <summary>World point under the screen point at a given distance along the view axis.</summary>
    public Vector3 AtDepth(Vector2 s, Rect r, float depth)
    {
        var ray = Ray(s, r);
        return Pos + ray * (depth / Math.Max(Vector3.Dot(ray, Forward), 1e-4f));
    }

    public float Depth(Vector3 p) => Vector3.Dot(p - Pos, Forward);

    /// <summary>World units per screen pixel at a point.</summary>
    public float PixelSize(Vector3 p, Rect r) => Math.Max(Depth(p), Near) * TanY * 2 / r.H;
}
