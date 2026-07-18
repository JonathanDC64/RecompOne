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
        layout(location = 5) in float inW;

        out vec4 vColor;
        out vec2 vUV;
        noperspective out vec4 vColorA; // affine twins: fragment picks by uniform
        noperspective out vec2 vUVA;
        flat out ivec2 clutBase;
        flat out ivec2 pageBase;
        flat out int   texMode;
        flat out int   vPersp; // 1 = perspective 3D poly (W!=1); 0 = 2D rect/sprite/affine

        uniform vec2 uVertexOffset;
        uniform vec2 uPosBias;
        uniform vec2 uFbInv;

        void main() {
            vec2 p = (inPos + uVertexOffset + uPosBias) * uFbInv - 1.0;
            // PGXP: scaling clip coords by W makes the hardware interpolate the
            // smooth varyings perspective-correctly; W=1 = PS1-style affine. The
            // noperspective twins always interpolate affine regardless of W.
            gl_Position = vec4(p * inW, 0.0, inW);
            vPersp = (inW != 1.0) ? 1 : 0; // rects/sprites are affine (W=1) -> excluded from bilinear

            vColor = vec4(float(inColor & 0xFFu), float((inColor >> 8) & 0xFFu), float((inColor >> 16) & 0xFFu), 0.0) / 255.0;
            vColorA = vColor;

            if ((inTexpage & 0x8000) != 0) {
                texMode = 4;
            } else {
                texMode = (inTexpage >> 7) & 3;
                vUV = inUV;
                vUVA = inUV;
                pageBase = ivec2((inTexpage & 0xf) * 64, ((inTexpage >> 4) & 1) * 256);
                clutBase = ivec2((inClut & 0x3f) * 16, (inClut >> 6) & 0x1ff);
            }
        }
        """;

    public const string PrimFs = """
        #version 330 core
        in vec4 vColor;
        in vec2 vUV;
        noperspective in vec4 vColorA;
        noperspective in vec2 vUVA;
        flat in ivec2 clutBase;
        flat in ivec2 pageBase;
        flat in int   texMode;
        flat in int   vPersp;

        uniform int uPctTex; // 1 = perspective-correct texture coords (PGXP)
        uniform int uPctCol; // 1 = perspective-correct vertex colors

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
        uniform int   uFilter; // 0 = nearest, 1 = bilinear (manual, CLUT-aware)

        int u5(float f) { return int(floor(f * 31.0 + 0.5)); }
        vec4 fetch(ivec2 c) { return texelFetch(uVram, (c & ivec2(1023, 511)) * uScale, 0); }
        int fetch16(ivec2 c) {
            vec4 p = fetch(c);
            return u5(p.r) | (u5(p.g) << 5) | (u5(p.b) << 10) | (int(ceil(p.a)) << 15);
        }
        vec4 modulate(vec4 tex, vec4 col) { vec4 r = (tex * col) / (128.0 / 255.0); r.a = 1.0; return r; }

        // Decode ONE texel at integer coords (texture-window wrap + CLUT lookup).
        vec4 sampleTexel(ivec2 raw) {
            ivec2 uv = ((raw & uTexWindow.xy) | uTexWindow.zw) & ivec2(0xff);
            if (texMode == 0) {
                int s = fetch16(ivec2(pageBase.x + (uv.x >> 2), pageBase.y + uv.y));
                int idx = (s >> ((uv.x & 3) << 2)) & 0xf;
                return fetch(ivec2(clutBase.x + idx, clutBase.y));
            } else if (texMode == 1) {
                int s = fetch16(ivec2(pageBase.x + (uv.x >> 1), pageBase.y + uv.y));
                int idx = (s >> ((uv.x & 1) << 3)) & 0xff;
                return fetch(ivec2(clutBase.x + idx, clutBase.y));
            }
            return fetch(ivec2(pageBase.x + uv.x, pageBase.y + uv.y));
        }
        // PS1 transparency: a texel whose 16-bit value is all-zero is "not drawn".
        bool isOpaque(vec4 t) { return !(t.rgb == vec3(0.0) && t.a < 0.5); }

        void main() {
            if (uCheckMask != 0 && texelFetch(uDest, ivec2(gl_FragCoord.xy), 0).a >= 0.5) discard;

            vec4 col = uPctCol != 0 ? vColor : vColorA;
            vec2 uvi = uPctTex != 0 ? vUV : vUVA;

            if (texMode == 4) {
                FragColor = vec4(col.rgb, uSetMask);
                BlendColor = uBlend;
                return;
            }

            vec4 texel;
            if (uFilter == 0 || vPersp == 0) {
                // Nearest: subpixel-correct texel pick (matches PS1 rasterizer).
                // Also the path for 2D rects/sprites/UI, which stay crisp.
                int rawU = dFdx(uvi.x) < 0.0 ? int(ceil(uvi.x - 0.0001)) : int(floor(uvi.x + 0.0001));
                int rawV = dFdy(uvi.y) < 0.0 ? int(ceil(uvi.y - 0.0001)) : int(floor(uvi.y + 0.0001));
                texel = sampleTexel(ivec2(rawU, rawV));
                if (!isOpaque(texel)) discard;
            } else {
                // Manual CLUT-aware bilinear: decode the 4 neighbours to RGBA and
                // blend, weighting out transparent texels so opaque edges don't
                // bleed toward black (hardware filtering can't run on CLUT indices).
                vec2 f = uvi - vec2(0.5);
                ivec2 b = ivec2(floor(f));
                vec2 fr = f - vec2(b);
                // TODO(uv_limits): clamp b+offset taps to the primitive's UV
                // bounding box (DuckStation's fix) to stop bleed into adjacent
                // atlas textures / across tile edges. Needs a per-vertex UV-bounds
                // attribute plumbed from GpuRaster (per whole primitive, incl. all
                // 4 quad verts). Until then, opaque atlas neighbours can bleed.
                vec4 t00 = sampleTexel(b + ivec2(0, 0));
                vec4 t10 = sampleTexel(b + ivec2(1, 0));
                vec4 t01 = sampleTexel(b + ivec2(0, 1));
                vec4 t11 = sampleTexel(b + ivec2(1, 1));
                float w00 = (1.0 - fr.x) * (1.0 - fr.y) * (isOpaque(t00) ? 1.0 : 0.0);
                float w10 = fr.x * (1.0 - fr.y) * (isOpaque(t10) ? 1.0 : 0.0);
                float w01 = (1.0 - fr.x) * fr.y * (isOpaque(t01) ? 1.0 : 0.0);
                float w11 = fr.x * fr.y * (isOpaque(t11) ? 1.0 : 0.0);
                float ws = w00 + w10 + w01 + w11;
                if (ws < 0.001) discard; // all four neighbours transparent
                vec3 rgb = (t00.rgb * w00 + t10.rgb * w10 + t01.rgb * w01 + t11.rgb * w11) / ws;
                float a = (t00.a * w00 + t10.a * w10 + t01.a * w01 + t11.a * w11) / ws;
                texel = vec4(rgb, a);
            }
            FragColor = vec4(modulate(texel, col).rgb, max(texel.a, uSetMask));
            BlendColor = texel.a >= 0.5 ? uBlend : uBlendOpaque;
        }
        """;

    public static uint Build(GL gl, string vsSrc, string fsSrc, string name)
    {
        uint vs = CompileStage(gl, ShaderType.VertexShader, vsSrc, name);
        uint fs = CompileStage(gl, ShaderType.FragmentShader, fsSrc, name);
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
