// Mixer Git 3D compare: meshes coloured by what changed (vertex colours), lit from the camera so every side reads.
Shader "Hidden/Orbiters/UnitGitChanges"
{
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Tint;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                fixed4 color : COLOR;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = normalize(mul((float3x3)UNITY_MATRIX_IT_MV, v.normal));
                o.color = v.color * _Tint;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.normal);
                float key = saturate(abs(dot(n, normalize(float3(0.35, 0.55, 1.0)))));
                float rim = pow(1.0 - saturate(abs(n.z)), 3.0) * 0.25;
                return fixed4(i.color.rgb * (0.32 + 0.68 * key) + rim, 1.0);
            }
            ENDCG
        }
    }
}
