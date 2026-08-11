using Silk.NET.OpenGL;

namespace RecompOne.Runtime.Hle;

internal static class GlShaders
{
    public const string FullscreenVs = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        out vec2 vUv;
        void main() {
            vUv = aPos * 0.5 + 0.5;
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    public const string PresentFs = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uVram;
        uniform vec2 uOrigin;
        uniform vec2 uSize;
        uniform vec2 uTexSize;
        out vec4 oColor;
        void main() {
            vec2 t = (uOrigin + vUv * uSize) / uTexSize;
            oColor = vec4(texture(uVram, t).rgb, 1.0);
        }
        """;

    public const string Present24Fs = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uVram;
        uniform vec2 uOrigin;
        uniform vec2 uSize;
        uniform int uScale;
        out vec4 oColor;

        int u5(float f) { return int(floor(f * 31.0 + 0.5)); }
        int texel16(int lin) {
            vec4 p = texelFetch(uVram, ivec2((lin & 1023) * uScale, ((lin >> 10) & 511) * uScale), 0);
            return u5(p.r) | (u5(p.g) << 5) | (u5(p.b) << 10) | (int(ceil(p.a)) << 15);
        }
        int byteAt(int b) {
            int t = texel16(b >> 1);
            return (b & 1) == 0 ? (t & 0xff) : ((t >> 8) & 0xff);
        }
        void main() {
            int px = int(floor(vUv.x * uSize.x));
            int py = int(floor(vUv.y * uSize.y));
            int ty = int(uOrigin.y) + py;
            int base = (ty * 1024 + int(uOrigin.x)) * 2 + px * 3;
            oColor = vec4(float(byteAt(base)) / 255.0, float(byteAt(base + 1)) / 255.0,
                          float(byteAt(base + 2)) / 255.0, 1.0);
        }
        """;

    public const string PrimVs = """
        #version 330 core
        layout(location = 0) in vec2  inPos;
        layout(location = 1) in uint  inColor;
        layout(location = 2) in int   inClut;
        layout(location = 3) in int   inTexpage;
        layout(location = 4) in vec2  inUV;
        layout(location = 5) in float inInvZ;

        out vec4 vColor;
        out vec2 vUV;
        out float vInvZ;
        flat out ivec2 clutBase;
        flat out ivec2 pageBase;
        flat out int   texMode;
        flat out int   vDither;
        flat out int   vSemiTrans;
        flat out int   vBlendMode;

        uniform vec2 uVertexOffset;
        uniform vec2 uPosBias;
        uniform vec2 uFbInv;

        void main() {
            vec2 p = (inPos + uVertexOffset + uPosBias) * uFbInv - 1.0;
            gl_Position = vec4(p, 0.0, 1.0);

            vColor = vec4(float(inColor & 0xFFu), float((inColor >> 8) & 0xFFu), float((inColor >> 16) & 0xFFu), 0.0) / 255.0;
            vDither = (inTexpage >> 10) & 1;
            vSemiTrans = (inTexpage >> 12) & 1;
            vBlendMode = (inTexpage >> 13) & 3;
            vInvZ = inInvZ;

            if ((inTexpage & 0x8000) != 0) {
                texMode = 4;
            } else {
                texMode = (inTexpage >> 7) & 3;
                vUV = inUV;
                pageBase = ivec2((inTexpage & 0xf) * 64, ((inTexpage >> 4) & 1) * 256);
                clutBase = ivec2((inClut & 0x3f) * 16, (inClut >> 6) & 0x1ff);
            }
        }
        """;

    public const string PrimFs = """
        #version 330 core
        in vec4 vColor;
        in vec2 vUV;
        in float vInvZ;
        flat in ivec2 clutBase;
        flat in ivec2 pageBase;
        flat in int   texMode;
        flat in int   vDither;
        flat in int   vSemiTrans;
        flat in int   vBlendMode;

        layout(location = 0, index = 0) out vec4 FragColor;
        layout(location = 0, index = 1) out vec4 BlendColor;

        uniform sampler2D uVram;
        uniform sampler2D uDest;
        uniform ivec4 uTexWindow;
        uniform vec4  uBlend;
        uniform vec4  uBlendOpaque = vec4(1.0, 1.0, 1.0, 0.0);
        uniform float uSetMask;
        uniform int   uCheckMask;
        uniform int   uScale;
        uniform int   uFilterMode;
        uniform float uFilterStrength;
        uniform int   uDedither;
        uniform vec2  uPosBias;

        const int ditherTbl[16] = int[16](
            -4,  0, -3,  1,
             2, -2,  3, -1,
            -3,  1, -4,  0,
             3, -1,  2, -2 );

        int u5(float f) { return int(floor(f * 31.0 + 0.5)); }
        vec4 fetch(ivec2 c) { return texelFetch(uVram, (c & ivec2(1023, 511)) * uScale, 0); }
        int fetch16(ivec2 c) {
            vec4 p = fetch(c);
            return u5(p.r) | (u5(p.g) << 5) | (u5(p.b) << 10) | (int(ceil(p.a)) << 15);
        }
        vec3 quant5(ivec3 c8) {
            // Dedither = true-color: keep 8-bit, no ordered dither.
            // (Old path skipped dither but still crushed to 5-bit into an RGB555 RT,
            //  which made edges shimmer when the camera moved.)
            if (uDedither != 0)
                return clamp(vec3(c8), 0.0, 255.0) / 255.0;
            if (vDither != 0) {
                ivec2 vp = ivec2(floor(gl_FragCoord.xy / float(uScale) - uPosBias));
                c8 = clamp(c8 + ditherTbl[(vp.y & 3) * 4 + (vp.x & 3)], 0, 255);
            }
            return vec3(min(c8 >> 3, 31)) / 31.0;
        }

        ivec2 wrapUv(ivec2 uv) {
            uv = (uv & uTexWindow.xy) | uTexWindow.zw;
            return uv & ivec2(0xff);
        }

        bool isTransparent(vec4 t) {
            return t.rgb == vec3(0.0) && t.a < 0.5;
        }

        vec4 sampleNearest(ivec2 uv) {
            uv = wrapUv(uv);
            if (texMode == 0) {
                int s = fetch16(ivec2(pageBase.x + (uv.x >> 2), pageBase.y + uv.y));
                int idx = (s >> ((uv.x & 3) << 2)) & 0xf;
                return fetch(ivec2(clutBase.x + idx, clutBase.y));
            }
            if (texMode == 1) {
                int s = fetch16(ivec2(pageBase.x + (uv.x >> 1), pageBase.y + uv.y));
                int idx = (s >> ((uv.x & 1) << 3)) & 0xff;
                return fetch(ivec2(clutBase.x + idx, clutBase.y));
            }
            return fetch(ivec2(pageBase.x + uv.x, pageBase.y + uv.y));
        }

        // Keep bilinear taps inside the same 16x16 UV cell. Crash packs unrelated
        // tiles in VRAM; crossing the boundary pulls green/junk and draws a grid.
        ivec2 tileTap(ivec2 base, ivec2 off) {
            ivec2 t = base + off;
            if ((base.x >> 4) != (t.x >> 4) || (base.y >> 4) != (t.y >> 4))
                return base;
            return t;
        }

        vec4 sampleBilinearF(vec2 uvf, vec2 f) {
            ivec2 i00 = ivec2(floor(uvf));
            vec4 c00 = sampleNearest(i00);
            vec4 c10 = sampleNearest(tileTap(i00, ivec2(1, 0)));
            vec4 c01 = sampleNearest(tileTap(i00, ivec2(0, 1)));
            vec4 c11 = sampleNearest(tileTap(i00, ivec2(1, 1)));

            // Skip transparent PS1 key color so sprite edges don't go muddy.
            float w00 = (1.0 - f.x) * (1.0 - f.y) * (isTransparent(c00) ? 0.0 : 1.0);
            float w10 = f.x * (1.0 - f.y) * (isTransparent(c10) ? 0.0 : 1.0);
            float w01 = (1.0 - f.x) * f.y * (isTransparent(c01) ? 0.0 : 1.0);
            float w11 = f.x * f.y * (isTransparent(c11) ? 0.0 : 1.0);
            float wsum = w00 + w10 + w01 + w11;
            if (wsum < 1e-4) return vec4(0.0);

            return (c00 * w00 + c10 * w10 + c01 * w01 + c11 * w11) / wsum;
        }

        vec4 sampleBilinear(vec2 uvf) {
            return sampleBilinearF(uvf, fract(uvf));
        }

        vec4 sampleSharpBilinear(vec2 uvf) {
            // Pull weights toward texel centers - less soap than plain bilinear.
            vec2 f = fract(uvf);
            f = clamp((f - 0.5) * 2.0 + 0.5, 0.0, 1.0);
            return sampleBilinearF(uvf, f);
        }

        vec4 sampleSoftSmooth(vec2 uvf) {
            // Soften bilinear weights (no 3x3 - that bled across atlas seams).
            vec2 f = fract(uvf);
            f = f * f * (3.0 - 2.0 * f);
            return sampleBilinearF(uvf, f);
        }

        vec4 applyFilter(vec2 uv, vec4 nearest, float strength) {
            if (uFilterMode == 0 || strength <= 0.001) return nearest;
            vec4 filtered = nearest;
            if (uFilterMode == 1)
                filtered = sampleBilinear(uv);
            else if (uFilterMode == 2)
                filtered = sampleSharpBilinear(uv);
            else if (uFilterMode == 3)
                filtered = sampleSoftSmooth(uv);
            if (isTransparent(filtered)) return nearest;
            return mix(nearest, filtered, clamp(strength, 0.0, 1.0));
        }

        vec4 blendFactors(bool stp) {
        #ifdef GLES_UNIFIED_BLEND
            if (vSemiTrans == 0 || !stp) return vec4(1.0);
            if (vBlendMode == 0) return vec4(0.5, 0.5, 0.5, 0.5);
            if (vBlendMode == 3) return vec4(0.25, 0.25, 0.25, 0.0);
            return vec4(1.0, 1.0, 1.0, 0.0);
        #else
            return stp ? uBlend : uBlendOpaque;
        #endif
        }

        void main() {
            if (uCheckMask != 0 && texelFetch(uDest, ivec2(gl_FragCoord.xy), 0).a >= 0.5) discard;

            if (texMode == 4) {
                FragColor = vec4(quant5(ivec3(vColor.rgb * 255.0 + 0.5)), uSetMask);
                BlendColor = blendFactors(true);
                return;
            }

            // Affine UVs (PS1). Perspective path disabled — unstable GTE Z matching.
            vec2 uv = vUV;

            int rawU = dFdx(uv.x) < 0.0 ? int(ceil(uv.x - 0.0001)) : int(floor(uv.x + 0.0001));
            int rawV = dFdy(uv.y) < 0.0 ? int(ceil(uv.y - 0.0001)) : int(floor(uv.y + 0.0001));
            vec4 nearest = sampleNearest(ivec2(rawU, rawV));
            if (isTransparent(nearest)) discard;

            vec4 texel = applyFilter(uv, nearest, uFilterStrength);
            ivec3 c8;
            if (uDedither != 0) {
                vec3 t8 = texel.rgb * 255.0;
                c8 = ivec3(t8 * vec3(vColor.rgb * 255.0) / 128.0 + 0.5);
            } else {
                ivec3 t8 = ivec3(texel.rgb * 31.0 + 0.5) << 3;
                c8 = (t8 * ivec3(vColor.rgb * 255.0 + 0.5)) >> 7;
            }
            FragColor = vec4(quant5(c8), max(texel.a, uSetMask));
            BlendColor = blendFactors(texel.a >= 0.5);
        }
        """;

    public static uint Build(GL gl, string vsSrc, string fsSrc, string name, bool gles = false)
    {
        uint vs = CompileStage(gl, ShaderType.VertexShader, AdaptSource(vsSrc, gles), name);
        uint fs = CompileStage(gl, ShaderType.FragmentShader, AdaptSource(fsSrc, gles), name);
        if (vs == 0 || fs == 0) return 0;

        uint prog = gl.CreateProgram();
        gl.AttachShader(prog, vs);
        gl.AttachShader(prog, fs);
        gl.LinkProgram(prog);
        gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0)
        {
            Console.WriteLine($"[GlBackend] link failed ({name}): {gl.GetProgramInfoLog(prog)}");
            gl.DeleteProgram(prog);
            prog = 0;
        }
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        return prog;
    }

    static string Ascii(string s)
    {
        var a = s.ToCharArray();
        for (int i = 0; i < a.Length; i++) if (a[i] > 0x7F) a[i] = ' ';
        return new string(a);
    }

    static string AdaptSource(string source, bool gles)
    {
        if (!gles) return source;

        var header = "#version 320 es";
        if (source.Contains("index = 1", StringComparison.Ordinal))
            header += "\n#extension GL_EXT_blend_func_extended : require";
        header += "\n#define GLES_UNIFIED_BLEND 1\nprecision highp float;\nprecision highp int;";
        return source
            .Replace("#version 330 core", header, StringComparison.Ordinal)
            .Replace("uniform vec4  uBlendOpaque = vec4(1.0, 1.0, 1.0, 0.0);",
                "uniform vec4  uBlendOpaque;", StringComparison.Ordinal);
    }

    static uint CompileStage(GL gl, ShaderType type, string src, string name)
    {
        uint sh = gl.CreateShader(type);
        gl.ShaderSource(sh, Ascii(src));
        gl.CompileShader(sh);
        gl.GetShader(sh, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
        {
            Console.WriteLine($"[GlBackend] compile failed ({name} {type}) {gl.GetShaderInfoLog(sh)}");
            gl.DeleteShader(sh);
            return 0;
        }
        return sh;
    }
}
