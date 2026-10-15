// SfMorph procedural skin wrinkles (0.7.4): UV-space rasterisation of the fold heightfield's slope and its blend into the
// skin's normal map. Unlit; all passes draw with DrawProcedural (no mesh), vertex ids from SV_VertexID.
//   Pass 0  slope raster : one triangle per mesh triangle at its UV position; the fragment shader sums, over the triangle's three
//                          corners (barycentric weights = a partition of unity), the corner's fold profile gradient, written as
//                          a tangent-space slope (x along +u, y along +v, metres/metre). RT: RG slope, B coverage, A activation.
//   Pass 1  blend        : fullscreen; base normal + slope (bilinear, dilated by coverage) -> whiteout blend -> the sheet's normal RT.
//                          0.8.0: with _MarksOn > 0.5 the lingering dent of SkinMarks' marks RT (_Marks.b, metres) is whited out into
//                          the normal too (same maths as SkinMarks.shader pass 2); off by default, the wrinkles then look as before.
//   Pass 2  debug tint   : activation (or fold orientation) as a colour over the base albedo.
//   Pass 3  self-test    : _TestMode 0 writes a UV gradient through the same uv -> clip mapping as pass 0/1, _TestMode 1 samples it
//                          back at a fixed uv into a 1x1 target; the CPU reads that texel to learn whether _FlipY is right.
// Normal encoding: R = x, G = y, B = z (all *0.5+0.5), A = 1 -> HDRP's RGorAG unpack takes x from R when A == 1; decode here
// accepts R/G or A/G (xy = (R*A, G)*2-1).
Shader "SfMorph/WrinkleRaster"
{
    Properties
    {
        _FlipY ("Clip Y sign", Float) = -1
        _Strength ("Strength", Float) = 1
        _Amp ("Fold depth (fraction of spacing)", Float) = 0.15
        _TestMode ("Self-test mode", Float) = 0
        _BaseNormal ("Base normal", 2D) = "bump" {}
        _BaseAlbedo ("Base albedo", 2D) = "white" {}
        _Slope ("Slope", 2D) = "black" {}
        _Test ("Test", 2D) = "black" {}
        _SlopeTexel ("Slope texel size", Vector) = (0.000488, 0.000488, 0, 0)
        _DebugMode ("Debug mode", Float) = 1
        _Marks ("Skin marks (0.8.0)", 2D) = "black" {}
        _MarksOn ("Skin marks on", Float) = 0
        _MarksTexel ("Marks texel size", Vector) = (0.000488, 0.000488, 0, 0)
        _MarksScale ("1 / metres per UV", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off
        ZTest Always
        ZWrite Off
        Blend Off

        HLSLINCLUDE
        #pragma target 5.0
        #pragma only_renderers d3d11

        float _FlipY, _Strength, _Amp, _TestMode, _DebugMode;
        float4 _SlopeTexel;
        float _MarksOn, _MarksScale;
        float4 _MarksTexel;
        Texture2D _Marks; SamplerState sampler_Marks;
        Texture2D _BaseNormal; SamplerState sampler_BaseNormal;
        Texture2D _BaseAlbedo; SamplerState sampler_BaseAlbedo;
        Texture2D _Slope; SamplerState sampler_Slope;
        Texture2D _Test; SamplerState sampler_Test;

        static const float PI = 3.14159265;

        // the one uv -> clip mapping of every pass (so a wrong _FlipY is wrong consistently, and the self-test can find it)
        float4 UvToClip(float2 uv)
        {
            float4 p = float4(uv * 2.0 - 1.0, 0.0, 1.0);
            p.y *= _FlipY;
            return p;
        }

        struct FsIn
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        FsIn FullscreenVert(uint vid : SV_VertexID)
        {
            FsIn o;
            o.uv = float2((vid << 1) & 2, vid & 2);
            o.pos = UvToClip(o.uv);
            return o;
        }

        // slope RT sample, normalised by coverage and dilated by one texel where the neighbourhood is partly empty
        // result: xy = slope, z = coverage found (0 = none), w = activation
        float4 SampleSlope(float2 uv)
        {
            float4 c = _Slope.SampleLevel(sampler_Slope, uv, 0);
            if (c.z < 0.999)
            {
                [unroll] for (int j = -1; j <= 1; j++)
                {
                    [unroll] for (int i = -1; i <= 1; i++)
                    {
                        if (i == 0 && j == 0) continue;
                        c += _Slope.SampleLevel(sampler_Slope, uv + float2(i, j) * _SlopeTexel.xy, 0);
                    }
                }
            }
            if (c.z < 1e-3) return float4(0, 0, 0, 0);
            return float4(c.xy / c.z, saturate(c.z), c.w / c.z);
        }

        // SkinMarks' marks RT (x redness, y pressure, z lingering dent in metres, w coverage), normalised by coverage and dilated like SampleSlope
        float MarksDent(float2 uv)
        {
            float4 c = _Marks.SampleLevel(sampler_Marks, uv, 0);
            if (c.w < 0.999)
            {
                [unroll] for (int j = -1; j <= 1; j++)
                {
                    [unroll] for (int i = -1; i <= 1; i++)
                    {
                        if (i == 0 && j == 0) continue;
                        c += _Marks.SampleLevel(sampler_Marks, uv + float2(i, j) * _MarksTexel.xy, 0);
                    }
                }
            }
            return c.w < 1e-3 ? 0.0 : c.z / c.w;
        }

        // slope (x along +u, y along +v, metres/metre) of the height -dent; the same scale as SkinMarks.shader pass 2 (DentGain 0.6)
        float2 MarksSlope(float2 uv)
        {
            float2 tx = float2(_MarksTexel.x, 0.0), ty = float2(0.0, _MarksTexel.y);
            float2 g = float2(MarksDent(uv + tx) - MarksDent(uv - tx), MarksDent(uv + ty) - MarksDent(uv - ty)) * 0.5 * _MarksScale
                / max(_MarksTexel.xy, 1e-6);
            float2 s = -g * 0.6;
            float sl = length(s);
            if (sl > 1.0) s /= sl;
            return s;
        }
        ENDHLSL

        // ---- pass 0: slope raster ----
        Pass
        {
            Name "Slope"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            StructuredBuffer<uint> _Tris;        // 3 per triangle
            StructuredBuffer<float2> _Uv;
            StructuredBuffer<float> _RestTri;    // 20 per triangle: DmInv(0..3) area(4) t(5..7) b(8..10) J(11..16) pad
            StructuredBuffer<float> _RestV;      // 12 per vertex: restPos(0..2) origin(3..5) refDir(6..8) mask(9) spacing(10)
            StructuredBuffer<uint> _Canon;
            StructuredBuffer<float> _State;      // 12 per vertex: act(0) axis(1..3) | cand(4..7) | fadeOut act(8) axis(9..11)

            struct V2F
            {
                float4 pos : SV_POSITION;
                nointerpolation uint3 ids : TEXCOORD0;
                nointerpolation float3 qT : TEXCOORD1;   // unit +u / +v directions of the triangle in rest space (zero = degenerate)
                nointerpolation float3 qB : TEXCOORD2;
                float3 bary : TEXCOORD3;
                float3 rest : TEXCOORD4;
            };

            V2F vert(uint vid : SV_VertexID)
            {
                V2F o;
                uint tri = vid / 3, corner = vid % 3;
                uint3 ids = uint3(_Tris[3 * tri], _Tris[3 * tri + 1], _Tris[3 * tri + 2]);
                uint v = corner == 0 ? ids.x : corner == 1 ? ids.y : ids.z;
                o.pos = UvToClip(_Uv[v]);
                o.ids = ids;
                o.bary = float3(corner == 0 ? 1 : 0, corner == 1 ? 1 : 0, corner == 2 ? 1 : 0);
                o.rest = float3(_RestV[v * 12], _RestV[v * 12 + 1], _RestV[v * 12 + 2]);
                // J rows = gradients of u and v in the triangle's rest plane; P = J^T (J J^T)^-1 holds the plane's d(rest)/d(u,v)
                uint b = tri * 20 + 11;
                float3 r0 = float3(_RestTri[b], _RestTri[b + 1], _RestTri[b + 2]);
                float3 r1 = float3(_RestTri[b + 3], _RestTri[b + 4], _RestTri[b + 5]);
                float g00 = dot(r0, r0), g01 = dot(r0, r1), g11 = dot(r1, r1);
                float det = g00 * g11 - g01 * g01;
                o.qT = float3(0, 0, 0);
                o.qB = float3(0, 0, 0);
                if (det > 1e-20)
                {
                    float i00 = g11 / det, i01 = -g01 / det, i11 = g00 / det;
                    float3 pu = r0 * i00 + r1 * i01;
                    float3 pv = r0 * i01 + r1 * i11;
                    float lu = dot(pu, pu), lv = dot(pv, pv);
                    if (lu > 1e-20 && lv > 1e-20) { o.qT = pu * rsqrt(lu); o.qB = pv * rsqrt(lv); }
                }
                return o;
            }

            // fold profile (0.8.1): compressed skin rolls into broad rounded ridges between narrow creases,
            // h(p) = -0.3 (cos(2 pi p) + 0.25 cos(4 pi p)): creases at whole p, ridges between. The 0.7.4 profile was a thin isolated
            // crease on flat skin, which read as stretch marks. Returns h'(p) (peak ~2.4, the old one ~2.8).
            float ProfileSlope(float p)
            {
                return 0.3 * PI * (2.0 * sin(2.0 * PI * p) + sin(4.0 * PI * p));
            }

            float4 frag(V2F i) : SV_Target
            {
                float2 slope = float2(0, 0);
                float actSum = 0.0;
                // phases and their screen-space rates first (derivatives must stay out of branches)
                float pp[3], pf[3], fw[3], fwf[3];
                float3 ax[3], axf[3];
                float act[3], fo[3], sp[3];
                [unroll] for (int k = 0; k < 3; k++)
                {
                    uint idk = k == 0 ? i.ids.x : k == 1 ? i.ids.y : i.ids.z;
                    uint c = _Canon[idk];
                    uint rb = c * 12, sb = c * 12;
                    act[k] = _State[sb];
                    ax[k] = float3(_State[sb + 1], _State[sb + 2], _State[sb + 3]);
                    fo[k] = _State[sb + 8];
                    axf[k] = float3(_State[sb + 9], _State[sb + 10], _State[sb + 11]);
                    float3 org = float3(_RestV[rb + 3], _RestV[rb + 4], _RestV[rb + 5]);
                    sp[k] = max(_RestV[rb + 10], 1e-5);
                    float3 d = i.rest - org;
                    pp[k] = dot(d, ax[k]) / sp[k];
                    pf[k] = dot(d, axf[k]) / sp[k];
                    fw[k] = fwidth(pp[k]);
                    fwf[k] = fwidth(pf[k]);
                }
                [unroll] for (int m = 0; m < 3; m++)
                {
                    float w = m == 0 ? i.bary.x : m == 1 ? i.bary.y : i.bary.z;
                    if (act[m] > 0.0)
                    {
                        float fade = 1.0 - smoothstep(0.25, 0.5, fw[m]);          // band-limit: fold frequency vs the raster's resolution
                        float k2 = act[m] * _Strength * _Amp * fade * ProfileSlope(pp[m]);
                        slope += w * k2 * float2(dot(i.qT, ax[m]), dot(i.qB, ax[m]));
                        actSum += w * act[m] * fade;
                    }
                    if (fo[m] > 0.0)
                    {
                        float fade = 1.0 - smoothstep(0.25, 0.5, fwf[m]);
                        float k2 = fo[m] * _Strength * _Amp * fade * ProfileSlope(pf[m]);
                        slope += w * k2 * float2(dot(i.qT, axf[m]), dot(i.qB, axf[m]));
                        actSum += w * fo[m] * fade;
                    }
                }
                return float4(slope, 1.0, saturate(actSum));
            }
            ENDHLSL
        }

        // ---- pass 1: blend slope into the base normal ----
        Pass
        {
            Name "Blend"
            HLSLPROGRAM
            #pragma vertex FullscreenVert
            #pragma fragment frag

            float4 frag(FsIn i) : SV_Target
            {
                float4 bn = _BaseNormal.Sample(sampler_BaseNormal, i.uv);
                float2 xy = float2(bn.r * bn.a, bn.g) * 2.0 - 1.0;                  // RG or AG packed
                float l2 = dot(xy, xy);
                if (l2 > 1.0) xy *= rsqrt(l2);
                float3 nb = float3(xy, sqrt(max(0.0, 1.0 - dot(xy, xy))));
                float4 s = SampleSlope(i.uv);
                float3 nd = normalize(float3(-s.x, -s.y, 1.0));
                float3 n = normalize(float3(nb.xy + nd.xy, nb.z * nd.z));            // whiteout
                if (_MarksOn > 0.5)                                                  // 0.8.0: SkinMarks' lingering dent, second whiteout
                {
                    float2 ms = MarksSlope(i.uv);
                    float3 nm = normalize(float3(-ms.x, -ms.y, 1.0));
                    n = normalize(float3(n.xy + nm.xy, n.z * nm.z));
                }
                return float4(n * 0.5 + 0.5, 1.0);
            }
            ENDHLSL
        }

        // ---- pass 2: debug tint over the base albedo ----
        Pass
        {
            Name "Debug"
            HLSLPROGRAM
            #pragma vertex FullscreenVert
            #pragma fragment frag

            float3 Hue(float h)
            {
                float3 k = frac(float3(1.0, 2.0 / 3.0, 1.0 / 3.0) + h);
                return saturate(abs(k * 6.0 - 3.0) - 1.0);
            }

            float4 frag(FsIn i) : SV_Target
            {
                float4 a = _BaseAlbedo.Sample(sampler_BaseAlbedo, i.uv);
                float4 s = SampleSlope(i.uv);
                float amt = saturate(s.w * 1.5);
                float3 tint = float3(1.0, 0.05, 0.02);
                if (_DebugMode > 1.5)
                {
                    float ang = atan2(s.y, s.x);                                     // fold orientation (slope runs across the folds)
                    if (ang < 0.0) ang += PI;
                    if (ang >= PI) ang -= PI;
                    tint = Hue(ang / PI);
                }
                return float4(lerp(a.rgb, tint, amt * 0.8), a.a);
            }
            ENDHLSL
        }

        // ---- pass 3: _FlipY self-test ----
        Pass
        {
            Name "SelfTest"
            HLSLPROGRAM
            #pragma vertex FullscreenVert
            #pragma fragment frag

            float4 frag(FsIn i) : SV_Target
            {
                if (_TestMode < 0.5) return float4(i.uv.x, i.uv.y, 0.0, 1.0);        // mode 0: a UV gradient through the mapping under test
                return _Test.SampleLevel(sampler_Test, float2(0.5, 0.25), 0);        // mode 1: read it back where v = 0.25
            }
            ENDHLSL
        }
    }
}
