Shader "Hidden/MMDPreview/SpoutOutput"
{
    Properties { _MainTex ("Source", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            struct Varyings { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings vert(appdata_img v)
            {
                Varyings o;
                o.position = UnityObjectToClipPos(v.vertex);
                // Spout consumes Direct3D texture rows from top to bottom.
                o.uv = float2(v.texcoord.x, 1 - v.texcoord.y);
                return o;
            }
            float4 frag(Varyings i) : SV_Target { return float4(tex2D(_MainTex, i.uv).rgb, 1); }
            ENDCG
        }
    }
}
