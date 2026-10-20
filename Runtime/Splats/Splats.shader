// Gaussian splats as camera-facing ellipses (3DGS's EWA projection, the same frustum clamp and 0.3 px dilation as
// Orbiters desktop's renderer), drawn back to front in the order SplatSort.compute wrote, premultiplied alpha.
Shader "Hidden/Orbiters/Mixer/Splats"
{
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Off
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            struct Splat
            {
                float3 position;
                uint color;
                float3 cov0;
                float3 cov1;
            };

            StructuredBuffer<Splat> _Splats;
            StructuredBuffer<uint> _Order;
            uint _Count;
            float4x4 _ObjectToWorld;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                nointerpolation float4 color : TEXCOORD1;
            };

            static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };

            v2f vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                v2f o;
                o.pos = float4(0, 0, 2, 1);
                o.uv = 0;
                o.color = 0;
                uint index = _Order[iid];
                if (index >= _Count)
                    return o;
                Splat s = _Splats[index];
                float4 color = float4((s.color & 255u), (s.color >> 8) & 255u, (s.color >> 16) & 255u, s.color >> 24) / 255.0;
                // Out to α = 1/255 at this opacity, never past 3.33 σ.
                float extent = min(3.33, sqrt(2.0 * log(max(color.a * 255.0, 1.0))));
                float3 world = mul(_ObjectToWorld, float4(s.position, 1.0)).xyz;
                float3 view = mul(UNITY_MATRIX_V, float4(world, 1.0)).xyz;
                float z = -view.z;
                if (z < 0.05 || extent <= 0.0)
                    return o;
                float4 clip = mul(UNITY_MATRIX_P, float4(view, 1.0));

                // Covariance: object → view (Z forward), then the projection's Jacobian in pixels.
                float3x3 sigma = float3x3(s.cov0.x, s.cov0.y, s.cov0.z, s.cov0.y, s.cov1.x, s.cov1.y, s.cov0.z, s.cov1.y, s.cov1.z);
                float3x3 w = mul((float3x3)UNITY_MATRIX_V, (float3x3)_ObjectToWorld);
                w[2] = -w[2];
                float3x3 sc = mul(w, mul(sigma, transpose(w)));
                float2 screen = _ScreenParams.xy;
                float fx = UNITY_MATRIX_P[0][0] * screen.x * 0.5;
                float fy = UNITY_MATRIX_P[1][1] * screen.y * 0.5;
                float limx = 1.3 / abs(UNITY_MATRIX_P[0][0]);
                float limy = 1.3 / abs(UNITY_MATRIX_P[1][1]);
                float tx = clamp(view.x / z, -limx, limx) * z;
                float ty = clamp(view.y / z, -limy, limy) * z;
                float3x3 j = float3x3(fx / z, 0, -fx * tx / (z * z), 0, fy / z, -fy * ty / (z * z), 0, 0, 0);
                float3x3 c2 = mul(j, mul(sc, transpose(j)));
                float a = c2[0][0] + 0.3;
                float b = c2[0][1];
                float c = c2[1][1] + 0.3;
                float mid = 0.5 * (a + c);
                float d = sqrt(max(0.1, mid * mid - (a * c - b * b)));
                float l1 = mid + d;
                float l2 = max(mid - d, 0.1);
                float2 e1 = abs(b) > 1e-6 ? normalize(float2(b, l1 - a)) : (a >= c ? float2(1, 0) : float2(0, 1));
                float2 e2 = float2(-e1.y, e1.x);

                float2 corner = Corners[vid];
                float2 offset = (corner.x * sqrt(l1) * e1 + corner.y * sqrt(l2) * e2) * extent;
                clip.xy += offset * 2.0 / screen * clip.w;
                o.pos = clip;
                o.uv = corner * extent;
                // Colours were captured as encoded sRGB.
                #ifndef UNITY_COLORSPACE_GAMMA
                color.rgb = GammaToLinearSpace(color.rgb);
                #endif
                o.color = color;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float alpha = min(0.99, i.color.a * exp(-0.5 * dot(i.uv, i.uv)));
                if (alpha < 1.0 / 255.0)
                    discard;
                return float4(i.color.rgb * alpha, alpha);
            }
            ENDCG
        }
    }
}
