// SfMorph skin marks (0.8.0): hand prints and slap redness on her skin textures. GpuContact keeps a float4 per vertex
// (x redness 0..1, y live pressure 0..1, z lingering dent in metres, indexed by the welded vertex id); this shader rasterises it into
// UV space and composes it into copies of her albedo and normal map. Unlit; all passes draw with DrawProcedural (no mesh).
//   Pass 0  marks raster : one triangle per mesh triangle at its UV position, the corners' marks interpolated. Blend max so renderers
//                          that share the sheet (and overlapping UV islands) never erase each other. RT: RGB marks, A coverage.
//   Pass 1  albedo       : fullscreen; base albedo x redness tint x pressed-flesh darkening x pale tint of the lingering dent -> the
//                          sheet's albedo RT.
//   Pass 2  normal       : fullscreen; the dent slope (central differences of the lingering dent / metres per UV) whiteout-blended into
//                          the base normal -> the sheet's normal RT (only when SkinMarks owns the normal slot; with Wrinkles owning it,
//                          WrinkleRaster pass 1 does the same blend).
//   Pass 3  self-test    : the same _FlipY self-test as WrinkleRaster (the uv -> clip mapping of every pass must agree with texture sampling).
// Normal encoding: R = x, G = y, B = z (all *0.5+0.5), A = 1 -> HDRP's RGorAG unpack takes x from R when A == 1; decode here accepts
// R/G or A/G (xy = (R*A, G)*2-1), exactly like WrinkleRaster.
Shader "SfMorph/SkinMarks"
{
    Properties
    {
        _FlipY ("Clip Y sign", Float) = -1
        _TestMode ("Self-test mode", Float) = 0
        _Redness ("Redness", Float) = 0.5
        _Ao ("Pressure darkening", Float) = 0.175
        _MarksScale ("1 / metres per UV", Float) = 1
        _BaseAlbedo ("Base albedo", 2D) = "white" {}
        _BaseNormal ("Base normal", 2D) = "bump" {}
        _MarksTex ("Marks", 2D) = "black" {}
        _Test ("Test", 2D) = "black" {}
        _MarksTexel ("Marks texel size", Vector) = (0.000488, 0.000488, 0, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off
        ZTest Always
        ZWrite Off

        HLSLINCLUDE
        #pragma target 5.0
        #pragma only_renderers d3d11

        float _FlipY, _TestMode, _Redness, _Ao, _MarksScale;
        float4 _MarksTexel;
        Texture2D _BaseAlbedo; SamplerState sampler_BaseAlbedo;
        Texture2D _BaseNormal; SamplerState sampler_BaseNormal;
        Texture2D _MarksTex; SamplerState sampler_MarksTex;
        Texture2D _Test; SamplerState sampler_Test;

        // how strongly the lingering dent shows in the normal map (the geometry already carries the live dent and its bent normals)
        static const float DentGain = 0.6;
        static const float3 RedTint = float3(0.95, 0.52, 0.48);
        static const float3 PaleTint = float3(1.0, 0.93, 0.9);

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

        // marks RT sample, normalised by coverage and dilated by one texel where the neighbourhood is partly empty (UV seams, gutters)
        // result: xyz = marks (redness, pressure, dent m), w = coverage found (0 = none)
        float4 SampleMarks(float2 uv)
        {
            float4 c = _MarksTex.SampleLevel(sampler_MarksTex, uv, 0);
            if (c.w < 0.999)
            {
                [unroll] for (int j = -1; j <= 1; j++)
                {
                    [unroll] for (int i = -1; i <= 1; i++)
                    {
                        if (i == 0 && j == 0) continue;
                        c += _MarksTex.SampleLevel(sampler_MarksTex, uv + float2(i, j) * _MarksTexel.xy, 0);
                    }
                }
            }
            if (c.w < 1e-3) return float4(0, 0, 0, 0);
            return float4(c.xyz / c.w, saturate(c.w));
        }

        float DentAt(float2 uv) { return SampleMarks(uv).z; }
        ENDHLSL

        // ---- pass 0: marks raster ----
        Pass
        {
            Name "Marks"
            Blend One One
            BlendOp Max
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            StructuredBuffer<uint> _Tris;        // 3 per triangle (vertex ids of the skin submesh)
            StructuredBuffer<float2> _Uv;        // per vertex
            StructuredBuffer<uint> _Canon;       // per vertex: welded representative
            StructuredBuffer<float4> _MarkBuf;   // per vertex, indexed by canonical id: x redness, y pressure, z dent (m)

            struct V2F
            {
                float4 pos : SV_POSITION;
                float3 m : TEXCOORD0;
            };

            V2F vert(uint vid : SV_VertexID)
            {
                V2F o;
                uint tri = vid / 3, corner = vid % 3;
                uint v = _Tris[3 * tri + corner];
                o.pos = UvToClip(_Uv[v]);
                o.m = _MarkBuf[_Canon[v]].xyz;
                return o;
            }

            float4 frag(V2F i) : SV_Target
            {
                return float4(max(i.m, 0.0), 1.0);
            }
            ENDHLSL
        }

        // ---- pass 1: albedo = base x redness tint x pressed-flesh darkening x pale tint of the lingering dent ----
        Pass
        {
            Name "Albedo"
            Blend Off
            HLSLPROGRAM
            #pragma vertex FullscreenVert
            #pragma fragment frag

            float4 frag(FsIn i) : SV_Target
            {
                float4 a = _BaseAlbedo.Sample(sampler_BaseAlbedo, i.uv);
                float4 m = SampleMarks(i.uv);
                float3 tint = lerp(float3(1, 1, 1), RedTint, saturate(m.x * _Redness));
                float shade = 1.0 - _Ao * saturate(m.y);
                // the print a hand leaves for a moment after it lifts: the blood is pressed out, the dent (4 mm = full) reads pale
                float pale = saturate(m.z / 0.004);
                float3 paleTint = lerp(float3(1, 1, 1), PaleTint, pale) * (1.0 + 0.15 * pale);
                return float4(a.rgb * tint * shade * paleTint, a.a);
            }
            ENDHLSL
        }

        // ---- pass 2: normal = whiteout(base, dent slope) ----
        Pass
        {
            Name "Normal"
            Blend Off
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
                float2 tx = float2(_MarksTexel.x, 0.0), ty = float2(0.0, _MarksTexel.y);
                // gradient of the dent depth in metres per metre (x along +u, y along +v); the surface is the height -dent
                float2 g = float2(DentAt(i.uv + tx) - DentAt(i.uv - tx), DentAt(i.uv + ty) - DentAt(i.uv - ty)) * 0.5 * _MarksScale
                    / max(_MarksTexel.xy, 1e-6);
                float2 s = -g * DentGain;                                           // slope of the height, like WrinkleRaster's _Slope
                float sl = length(s);
                if (sl > 1.0) s /= sl;
                float3 nd = normalize(float3(-s.x, -s.y, 1.0));
                float3 n = normalize(float3(nb.xy + nd.xy, nb.z * nd.z));            // whiteout
                return float4(n * 0.5 + 0.5, 1.0);
            }
            ENDHLSL
        }

        // ---- pass 3: _FlipY self-test ----
        Pass
        {
            Name "SelfTest"
            Blend Off
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
