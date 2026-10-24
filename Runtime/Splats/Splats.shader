// Gaussian splats as camera-facing ellipses (3DGS's EWA projection, the same frustum clamp and compensated 0.1 px²
// filter as Orbiters - N4N0's renderer), drawn back to front in the order SplatSort.compute wrote, with its viewer's
// guards: splats fade in over the first 20 cm from the eye instead of vanishing at the near plane, those whose visible
// extent holds the eye are left out (they would fill the view), and none reaches further than 1024 px on screen (nor
// than the screen's shorter side). They blend like the trainer fitted them: on the colours as captured (encoded sRGB),
// over the scene's colour encoded the same way. Pass 0 accumulates the splats, premultiplied, into a temporary target
// that shares the camera's depth buffer (the scene in front hides them, as before); pass 1 lays that over a copy of the
// camera's image and converts the result to linear light once. Blending in linear light instead showed half-transparent
// splats brighter (50 % white over black at 0.735, not 0.5): sparse far content and the edges of what was seen looked
// hazier than trained. Pass 2, before them, draws the cloud's sky map wherever the camera drew nothing nearer than the
// far plane, looked up by direction as Orbiters - N4N0's renderer does (sky_lookup.wgsl).
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
                // What the keyframes saw of it: largest weight (negative where unknown), keyframes, sector bits, finest
                // sampling rate (pixels per metre); flags bit 0: the sky shell.
                float4 seen;
                uint flags;
                // Metres beyond the reach of the capture's depth frames.
                float beyond;
            };

            StructuredBuffer<Splat> _Splats;
            StructuredBuffer<uint> _Order;
            uint _Count;
            float4x4 _ObjectToWorld;
            float4x4 _WorldToObject;
            // How poorly seen splats show (SplatRenderer.poorlySeen): strength (0 to 1), mode (0 fade, 1 blur, 2 haze).
            float4 _Confidence;
            // The far field (SplatRenderer.farField): mode (0 shown, 1 fog, 2 crop), distance (metres).
            float4 _Far;
            // The sky's colour at the horizon, encoded sRGB: what fog and haze turn to.
            float4 _Horizon;

            // How far a splat can be trusted, as Orbiters - N4N0's viewer judges it (common.wgsl `trust`), seen from the
            // camera `towardEye` away (the cloud's own space, from the splat): RadSplat's thresholds on its largest weight,
            // half as much seen in one keyframe, 0.2 less for each compass sector between the camera's and the nearest one
            // a keyframe looked from (at most three), and none once the camera samples it four times as finely as the
            // nearest keyframe did.
            float Trust(float4 s, float3 towardEye, float focal)
            {
                float strength = smoothstep(0.001, 0.01, s.x);
                float views = lerp(0.5, 1.0, saturate((s.y - 1.0) / 4.0));
                uint eye = min((uint)(frac(atan2(towardEye.x, towardEye.z) / 6.283185307 + 1.0) * 16.0), 15u);
                uint bits = (uint)s.z;
                uint nearest = 3u;
                for (uint k = 0u; k < 16u; k++)
                {
                    if ((bits & (1u << k)) != 0u)
                    {
                        uint d = max(eye, k) - min(eye, k);
                        nearest = min(nearest, min(d, 16u - d));
                    }
                }
                float side = 1.0 - 0.2 * nearest;
                float finer = focal / max(length(towardEye), 1e-6) / max(s.w, 1e-6);
                return strength * views * side * (1.0 - smoothstep(1.0, 4.0, finer));
            }

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
                float3 world = mul(_ObjectToWorld, float4(s.position, 1.0)).xyz;
                float3 view = mul(UNITY_MATRIX_V, float4(world, 1.0)).xyz;
                float z = -view.z;
                if (z < 0.05)
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
                // The eye inside what the splat draws: its Mahalanobis distance (in view space, where the eye is the
                // origin) within the reach at this opacity.
                float3 d = -float3(view.x, view.y, z);
                float3 adj0 = float3(sc[1][1] * sc[2][2] - sc[1][2] * sc[1][2], sc[0][2] * sc[1][2] - sc[0][1] * sc[2][2], sc[0][1] * sc[1][2] - sc[0][2] * sc[1][1]);
                float adj11 = sc[0][0] * sc[2][2] - sc[0][2] * sc[0][2];
                float adj12 = sc[0][1] * sc[0][2] - sc[0][0] * sc[1][2];
                float adj22 = sc[0][0] * sc[1][1] - sc[0][1] * sc[0][1];
                float det3 = sc[0][0] * adj0.x + sc[0][1] * adj0.y + sc[0][2] * adj0.z;
                float inside = adj0.x * d.x * d.x + adj11 * d.y * d.y + adj22 * d.z * d.z + 2.0 * (adj0.y * d.x * d.y + adj0.z * d.x * d.z + adj12 * d.y * d.z);
                float3x3 cv = mul(j, mul(sc, transpose(j)));
                // Mip-Splatting's 2D filter: a Gaussian of 0.1 px² widens the splat and its opacity scales by
                // √(det A / det(A + filter)), so a splat smaller than a pixel fades as it shrinks instead of spreading at
                // full opacity (the trainer fused its 3D filter into the scales and opacities already).
                float detA = cv[0][0] * cv[1][1] - cv[0][1] * cv[0][1];
                float a = cv[0][0] + 0.1;
                float b = cv[0][1];
                float c = cv[1][1] + 0.1;
                float det = a * c - b * b;
                color.a *= sqrt(max(detA, 1e-8) / det);
                // Out to α = 1/255 at this opacity, never past 3.33 σ.
                float extent = min(3.33, sqrt(2.0 * log(max(color.a * 255.0, 1.0))));
                if (det3 > 0.0 && inside < extent * extent * det3)
                    return o;
                color.a *= smoothstep(0.05, 0.2, length(view));
                if (_Far.x > 0.5 && s.seen.x >= 0.0 && (s.flags & 1u) == 0u)
                {
                    float past = s.beyond / _Far.y;
                    if (_Far.x < 1.5)
                        color.rgb = lerp(color.rgb, _Horizon.rgb, 1.0 - exp(-past));
                    else
                        color.a *= 1.0 - smoothstep(0.0, 1.0, past);
                }
                if (_Confidence.x > 0.0 && s.seen.x >= 0.0 && (s.flags & 1u) == 0u)
                {
                    // The camera in the cloud's own space (where the sectors were measured), from the splat.
                    float3 towardEye = mul(_WorldToObject, float4(_WorldSpaceCameraPos, 1.0)).xyz - s.position;
                    float k = _Confidence.x * (1.0 - Trust(s.seen, towardEye, max(fx, fy)));
                    if (_Confidence.y < 0.5)
                        color.a *= 1.0 - k;
                    else if (_Confidence.y < 1.5)
                    {
                        // Wider by a Gaussian of up to 16 px², its weight kept as the 2D filter keeps it.
                        float before = det;
                        a += k * 16.0;
                        c += k * 16.0;
                        det = a * c - b * b;
                        color.a *= sqrt(before / det);
                    }
                    else
                        color.rgb = lerp(color.rgb, _Horizon.rgb, k);
                }
                extent = min(3.33, sqrt(2.0 * log(max(color.a * 255.0, 1.0))));
                if (extent <= 0.0)
                    return o;
                float mid = 0.5 * (a + c);
                float r = sqrt(max(0.1, mid * mid - det));
                float cap = min(1024.0, min(screen.x, screen.y)) / extent;
                float l1 = min(mid + r, cap * cap);
                float l2 = min(max(mid - r, 0.1), cap * cap);
                float2 e1 = abs(b) > 1e-6 ? normalize(float2(b, mid + r - a)) : (a >= c ? float2(1, 0) : float2(0, 1));
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
        // 2: the sky map, into the camera's target where its depth is still the far plane (before the splats).
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Off
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            // The far plane in clip space (built-in's includes have no UNITY_RAW_FAR_CLIP_VALUE).
            #if defined(UNITY_REVERSED_Z)
            #define SKY_FAR 0.0
            #else
            #define SKY_FAR 1.0
            #endif

            // Encoded sRGB kept as linear data: filtered as Orbiters - N4N0 filters its texels, turned into light here.
            Texture2D<float4> _SkyMap;
            SamplerState sampler_SkyMap;
            float4x4 _WorldToObject;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 ray : TEXCOORD0;
            };

            v2f vert(uint vid : SV_VertexID)
            {
                // One triangle over the whole target, on the far plane.
                float2 p = float2((vid << 1) & 2, vid & 2) * 2.0 - 1.0;
                v2f o;
                o.pos = float4(p, SKY_FAR, 1.0);
                // The view ray through this corner under the projection the GPU uses (which flips Y when it renders into a
                // texture), out of view space into the world, then into the cloud's own space, where the map was trained.
                float3 view = float3((p.x + UNITY_MATRIX_P[0][2]) / UNITY_MATRIX_P[0][0], (p.y + UNITY_MATRIX_P[1][2]) / UNITY_MATRIX_P[1][1], -1.0);
                float3 world = mul((float3x3)UNITY_MATRIX_I_V, view);
                o.ray = mul((float3x3)_WorldToObject, world);
                return o;
            }

            float3 ToLinear(float3 c)
            {
                c = max(c, 0.0);
                return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 d = normalize(i.ray);
                // Longitude atan2(x, z) across from −180°, elevation from +90° at the map's top row (a texture's last).
                float2 uv = float2(atan2(d.x, d.z) / 6.283185307 + 0.5, asin(clamp(d.y, -1.0, 1.0)) / 3.141592654 + 0.5);
                float3 sky = _SkyMap.SampleLevel(sampler_SkyMap, uv, 0).rgb;
                #ifdef UNITY_COLORSPACE_GAMMA
                return float4(sky, 1.0);
                #else
                return float4(ToLinear(sky), 1.0);
                #endif
            }
            ENDCG
        }
    }
}
