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
// far plane, looked up by direction as Orbiters - N4N0's renderer does (sky_lookup.wgsl). Pass 3 draws what pass 0 draws
// from the quads SplatProject.compute measured once per splat (SplatProjection.hlsl holds the maths for both).
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
            #include "SplatProjection.hlsl"

            StructuredBuffer<Splat> _Splats;
            // What the keyframes saw of each splat, where the cloud says (_HasSeen; else a placeholder).
            StructuredBuffer<SplatSeen> _Seen;
            uint _HasSeen;
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

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                nointerpolation float4 color : TEXCOORD1;
            };

            static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };

            // Every corner projects its splat again (the pipelines without the compute pass).
            v2f vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                v2f o;
                o.pos = float4(0, 0, 2, 1);
                o.uv = 0;
                o.color = 0;
                uint index = _Order[iid];
                if (index >= _Count)
                    return o;
                float2 screen = _ScreenParams.xy;
                SplatView v;
                v.objectToWorld = _ObjectToWorld;
                v.view = UNITY_MATRIX_V;
                v.focal = float2(UNITY_MATRIX_P[0][0] * screen.x * 0.5, UNITY_MATRIX_P[1][1] * screen.y * 0.5);
                v.limit = float2(1.3 / abs(UNITY_MATRIX_P[0][0]), 1.3 / abs(UNITY_MATRIX_P[1][1]));
                v.screen = screen;
                v.eyeInCloud = mul(_WorldToObject, float4(_WorldSpaceCameraPos, 1.0)).xyz;
                v.confidence = _Confidence;
                v.far = _Far;
                v.horizon = _Horizon.rgb;
                bool known = _HasSeen != 0u;
                SplatFootprint f;
                if (!SplatProject(_Splats[index], _Seen[known ? index : 0u], known, v, f))
                    return o;
                float4 clip = mul(UNITY_MATRIX_P, float4(f.view, 1.0));
                float2 corner = Corners[vid];
                float2 offset = corner.x * f.axis1 + corner.y * f.axis2;
                clip.xy += offset * 2.0 / screen * clip.w;
                o.pos = clip;
                o.uv = corner * f.extent;
                o.color = f.color;
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
        // 3: the splats into the accumulation target as pass 0 draws them, from the quads SplatProject.compute wrote for
        // this camera (SplatRenderer.Pipeline.Cached): each corner only reads its splat's quad.
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
            #include "SplatProjection.hlsl"

            StructuredBuffer<SplatQuad> _Quads;
            // The pixels the quads were measured in (what the compute pass was given).
            float4 _Screen;

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
                SplatQuad q = _Quads[iid];
                if (!(q.extent > 0.0))
                    return o;
                float4 clip = mul(UNITY_MATRIX_P, float4(q.view, 1.0));
                float2 corner = Corners[vid];
                float2 axis2 = float2(-q.axis1.y, q.axis1.x) * (q.radius2 / max(length(q.axis1), 1e-6));
                float2 offset = corner.x * q.axis1 + corner.y * axis2;
                // The quads were measured with y up; the GPU's projection flips y where it renders into a texture
                // (mirrored, the same ellipse comes out as pass 0 draws it there).
                if (UNITY_MATRIX_P[1][1] < 0.0)
                    offset.y = -offset.y;
                clip.xy += offset * 2.0 / _Screen.xy * clip.w;
                o.pos = clip;
                o.uv = corner * q.extent;
                o.color = float4(float3(q.rgb & 1023u, (q.rgb >> 10) & 1023u, (q.rgb >> 20) & 1023u) / 1023.0, q.alpha);
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
