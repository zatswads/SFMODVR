// SfMorph skin wrinkles (0.9.0): authored wrinkle maps faded in per region and blended into copies of her skin's normal map and
// albedo. Unlit; every pass is a fullscreen triangle drawn with DrawProcedural (no mesh), vertex ids from SV_VertexID.
//   Pass 0  blend    : base normal + the layout's crease slope (_Detail.rg) x the texel's region activation (_Act[_Region id]) x the
//                      region fade (_Detail.a) x _Strength -> whiteout -> the sheet's normal RT. With _MarksOn > 0.5 SkinMarks'
//                      lingering dent (_Marks.b, metres) is whited out into it too (same maths as SkinMarks.shader pass 2).
//   Pass 1  self-test: _TestMode 0 writes a UV gradient through the uv -> clip mapping, _TestMode 1 samples it back at a fixed uv into
//                      a 1x1 target; the CPU reads that texel to learn whether _FlipY is right.
//   Pass 2  albedo   : the sheet's copy of her albedo = base x SkinMarks' marks (_MarksAlbedoOn, same maths as SkinMarks.shader pass 1)
//                      x crease shade (_Cavity x the crease depth _Detail.b x activation x fade: her skin shader's subsurface
//                      scattering blurs fine normal detail out of the diffuse light, a darker crease survives it) [+ debug tint:
//                      _DebugMode 1 = activation, 2 = the regions in their own colours, brighter when active].
// The maps come from tools/wrinkles/author.py: _Detail RGBA = slope x, y (along +u, +v; *0.5/1.5+0.5), crease depth, region fade;
// _Region RGBA8 = region index + 1 (0 none, 255 off her islands), weight, crease direction (CPU only).
// Normal encoding of the output: R = x, G = y, B = z (all *0.5+0.5), A = 1 -> HDRP's RGorAG unpack takes x from R when A == 1; the
// base decode accepts R/G or A/G (xy = (R*A, G)*2-1).
Shader "SfMorph/WrinkleRaster"
{
    Properties
    {
        _FlipY ("Clip Y sign", Float) = -1
        _Strength ("Strength", Float) = 1
        _TestMode ("Self-test mode", Float) = 0
        _BaseNormal ("Base normal", 2D) = "bump" {}
        _BaseAlbedo ("Base albedo", 2D) = "white" {}
        _Detail ("Authored creases", 2D) = "black" {}
        _Region ("Regions", 2D) = "black" {}
        _Test ("Test", 2D) = "black" {}
        _DebugMode ("Debug mode", Float) = 0
        _Marks ("Skin marks", 2D) = "black" {}
        _MarksOn ("Skin marks on", Float) = 0
        _MarksTexel ("Marks texel size", Vector) = (0.000488, 0.000488, 0, 0)
        _MarksScale ("1 / metres per UV", Float) = 1
        _Cavity ("Crease shade", Float) = 0
        _MarksAlbedoOn ("Skin marks in the albedo", Float) = 0
        _Redness ("Marks redness", Float) = 0.5
        _Ao ("Marks pressure darkening", Float) = 0.175
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

        float _FlipY, _Strength, _TestMode, _DebugMode;
        float _MarksOn, _MarksScale;
        float4 _MarksTexel;
        float _Cavity, _MarksAlbedoOn, _Redness, _Ao;
        float _Act[32];
        Texture2D _Marks; SamplerState sampler_Marks;
        Texture2D _BaseNormal; SamplerState sampler_BaseNormal;
        Texture2D _BaseAlbedo; SamplerState sampler_BaseAlbedo;
        Texture2D _Detail; SamplerState sampler_Detail;
        Texture2D _Region;
        float4 _RegionSize;                     // width, height of the region map (set by the CPU)
        Texture2D _Test; SamplerState sampler_Test;

        static const float SlopeMax = 1.5;

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

        // region index (0-based; -1 = none) of the texel at uv: nearest texel of the region map (ids can't be filtered)
        int RegionAt(float2 uv)
        {
            int2 sz = int2(_RegionSize.xy);
            int2 p = clamp(int2(uv * _RegionSize.xy), int2(0, 0), sz - 1);
            int id = (int)(_Region.Load(int3(p, 0)).r * 255.0 + 0.5);
            return (id >= 1 && id <= 32) ? id - 1 : -1;
        }

        // the authored creases at uv, faded in: xy = slope (metres/metre), z = crease depth x activation, w = activation x fade
        float4 Creases(float2 uv)
        {
            int id = RegionAt(uv);
            if (id < 0) return float4(0, 0, 0, 0);
            float a = _Act[id];
            if (a <= 0.0) return float4(0, 0, 0, 0);
            float4 d = _Detail.SampleLevel(sampler_Detail, uv, 0);
            float k = a * d.a;
            return float4((d.rg * 2.0 - 1.0) * SlopeMax * k * _Strength, d.b * k, k);
        }

        // SkinMarks' marks RT (x redness, y pressure, z lingering dent in metres, w coverage), normalised by coverage and dilated one texel
        float4 SampleMarks(float2 uv)
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
            if (c.w < 1e-3) return float4(0, 0, 0, 0);
            return float4(c.xyz / c.w, saturate(c.w));
        }

        float MarksDent(float2 uv) { return SampleMarks(uv).z; }

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

        float3 Hue(float h)
        {
            float3 k = frac(float3(1.0, 2.0 / 3.0, 1.0 / 3.0) + h);
            return saturate(abs(k * 6.0 - 3.0) - 1.0);
        }

        // the debug view: activation (mode 1) or the regions (mode 2) as a tint over rgb
        float3 DebugTint(float3 rgb, float2 uv)
        {
            int id = RegionAt(uv);
            if (id < 0) return rgb;
            float4 d = _Detail.SampleLevel(sampler_Detail, uv, 0);
            float a = saturate(_Act[id]);
            if (_DebugMode > 1.5)
                return lerp(rgb, Hue(frac(id * 0.381966)), d.a * (0.35 + 0.5 * a));
            return lerp(rgb, float3(1.0, 0.05, 0.02), d.a * a * 0.8);
        }
        ENDHLSL

        // ---- pass 0: base normal + faded creases (+ marks) ----
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
                float3 n = float3(xy, sqrt(max(0.0, 1.0 - dot(xy, xy))));
                float4 c = Creases(i.uv);
                if (c.w > 0.0)
                {
                    float3 nd = normalize(float3(-c.x, -c.y, 1.0));
                    n = normalize(float3(n.xy + nd.xy, n.z * nd.z));                 // whiteout
                }
                if (_MarksOn > 0.5)
                {
                    float2 ms = MarksSlope(i.uv);
                    float3 nm = normalize(float3(-ms.x, -ms.y, 1.0));
                    n = normalize(float3(n.xy + nm.xy, n.z * nm.z));
                }
                return float4(n * 0.5 + 0.5, 1.0);
            }
            ENDHLSL
        }

        // ---- pass 1: _FlipY self-test ----
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

        // ---- pass 2: albedo copy = base x marks x crease shade (+ debug tint) ----
        Pass
        {
            Name "Albedo"
            HLSLPROGRAM
            #pragma vertex FullscreenVert
            #pragma fragment frag

            // SkinMarks.shader pass 1's tints (keep in sync)
            static const float3 RedTint = float3(0.95, 0.52, 0.48);
            static const float3 PaleTint = float3(1.0, 0.93, 0.9);
            // a crease is a little warmer than plain shade (blood under thin folded skin): blue and green darken a bit more than red
            static const float3 CreaseTint = float3(0.85, 1.0, 1.05);

            float4 frag(FsIn i) : SV_Target
            {
                float4 a = _BaseAlbedo.Sample(sampler_BaseAlbedo, i.uv);
                float3 c = a.rgb;
                if (_MarksAlbedoOn > 0.5)                                            // SkinMarks.shader pass 1, on this copy
                {
                    float4 m = SampleMarks(i.uv);
                    float3 tint = lerp(float3(1, 1, 1), RedTint, saturate(m.x * _Redness));
                    float shade = 1.0 - _Ao * saturate(m.y);
                    float pale = saturate(m.z / 0.004);
                    float3 paleTint = lerp(float3(1, 1, 1), PaleTint, pale) * (1.0 + 0.15 * pale);
                    c *= tint * shade * paleTint;
                }
                if (_Cavity > 0.0)
                {
                    float4 cr = Creases(i.uv);
                    c *= saturate(1.0 - _Cavity * saturate(cr.z * _Strength) * CreaseTint);
                }
                if (_DebugMode > 0.5) c = DebugTint(c, i.uv);
                return float4(c, a.a);
            }
            ENDHLSL
        }
    }
}
