// Ground truth for Mixer's capture simulator: copies the camera's depth texture as eye depth in metres (0 where
// nothing was drawn) into an RFloat target of the same size. It reads the depth texel at the same memory position as
// the pixel it writes (no UV maths), so its result checks the capture overlay's own depth lookups independently.
Shader "Hidden/Orbiters/Mixer/CaptureGroundTruth"
{
    SubShader
    {
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            Texture2D<float> _CameraDepthTexture;

            struct v2f { float4 pos : SV_POSITION; };

            v2f vert(uint id : SV_VertexID)
            {
                v2f o;
                float2 corner = float2((id << 1) & 2, id & 2);
                o.pos = float4(corner * 2.0 - 1.0, 0.5, 1.0); // one triangle over the whole target
                return o;
            }

            float frag(v2f i) : SV_Target
            {
                float raw = _CameraDepthTexture.Load(int3(i.pos.xy, 0));
            #if UNITY_REVERSED_Z
                if (raw <= 0.0) return 0.0;
            #else
                if (raw >= 1.0) return 0.0;
            #endif
                return LinearEyeDepth(raw);
            }
            ENDCG
        }
    }
    Fallback Off
}
