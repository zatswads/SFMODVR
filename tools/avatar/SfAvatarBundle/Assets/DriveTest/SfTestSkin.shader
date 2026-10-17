// Stand-in for the game's 'Shader Graphs/Skin' in the avatar drive test (Avatar.cs picks her body meshes by a skin shader).
Shader "SfTest/Skin"
{
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 vert(float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }
            fixed4 frag() : SV_Target { return 1; }
            ENDCG
        }
    }
}
