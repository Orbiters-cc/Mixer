// Gaussian splats as camera-facing ellipses (3DGS's EWA projection, the same frustum clamp and 0.3 px dilation as
// Orbiters desktop's renderer), drawn back to front in the order SplatSort.compute wrote. They blend like the trainer
// fitted them: on the colours as captured (encoded sRGB), over the scene's colour encoded the same way. Pass 0
// accumulates the splats, premultiplied, into a temporary target that shares the camera's depth buffer (the scene in
// front hides them, as before); pass 1 lays that over a copy of the camera's image and converts the result to linear
// light once. Blending in
// linear light instead showed half-transparent splats brighter (50 % white over black at 0.735, not 0.5): sparse far
// content and the edges of what was seen looked hazier than trained.
Shader "Hidden/Orbiters/Mixer/Splats"
{
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        // 0: the splats into the accumulation target (cleared to transparent black), depth-tested against the scene.
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
                // Encoded sRGB as captured, kept so: the blending happens on these values, as in training.
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
        // 1: the accumulated splats over the copy of the camera's image, back into the camera's target.
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            Texture2D<float4> _SplatAccumulation;
            Texture2D<float4> _SplatScene;

            float4 vert(uint vid : SV_VertexID) : SV_POSITION
            {
                // One triangle over the whole target.
                float2 p = float2((vid << 1) & 2, vid & 2);
                return float4(p * 2.0 - 1.0, 0.5, 1.0);
            }

            // The sRGB transfer functions, exactly (Unity's GammaToLinearSpace is a fit): what the hardware applies when it
            // writes an sRGB target, so splats over black end as the trainer's values.
            float3 ToLinear(float3 c)
            {
                c = max(c, 0.0);
                return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
            }

            float3 ToSrgb(float3 c)
            {
                c = max(c, 0.0);
                return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
            }

            float4 frag(float4 pos : SV_POSITION) : SV_Target
            {
                float4 s = _SplatAccumulation.Load(int3(pos.xy, 0));
                // Where no splat drew, the camera's own pixel stays (all its samples, with MSAA).
                if (s.a < 1.0 / 510.0)
                    discard;
                float4 scene = _SplatScene.Load(int3(pos.xy, 0));
                #ifdef UNITY_COLORSPACE_GAMMA
                float3 rgb = s.rgb + (1.0 - s.a) * scene.rgb;
                #else
                float3 rgb = ToLinear(s.rgb + (1.0 - s.a) * ToSrgb(scene.rgb));
                #endif
                return float4(rgb, s.a + (1.0 - s.a) * scene.a);
            }
            ENDCG
        }
    }
}
