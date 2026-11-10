// One splat's footprint on screen: 3DGS's EWA projection with the frustum clamp and compensated 0.1 px² filter of
// Orbiters - N4N0's renderer, its viewer's guards (fade over the first 20 cm, splats holding the eye left out, at most
// 1024 px and the screen's shorter side), and the poorly seen and far field looks. Written once for both ways the
// renderer draws: Splats.shader's pass 0 calls it for each corner (SplatRenderer.Pipeline.SortWhenMoved, EveryFrame),
// SplatProject.compute once per splat (Cached).
#ifndef ORBITERS_SPLAT_PROJECTION
#define ORBITERS_SPLAT_PROJECTION

#include "SplatData.hlsl"

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

// What a footprint depends on besides the splat.
struct SplatView
{
    float4x4 objectToWorld;
    // World to view space (the camera looks along -Z).
    float4x4 view;
    // Pixels per unit of view-space tangent, x and y (y negative where the GPU's projection flips it).
    float2 focal;
    // 1.3 / |P00|, 1.3 / |P11|: where the projection's Jacobian is clamped.
    float2 limit;
    float2 screen;
    // The camera in the cloud's own space, where the sectors were measured.
    float3 eyeInCloud;
    // How poorly seen splats show (SplatRenderer.poorlySeen): strength (0 to 1), mode (0 fade, 1 blur, 2 haze).
    float4 confidence;
    // The far field (SplatRenderer.farField): mode (0 shown, 1 fog, 2 crop), distance (metres).
    float4 far;
    // The sky's colour at the horizon, encoded sRGB: what fog and haze turn to.
    float3 horizon;
};

struct SplatFootprint
{
    // The centre in view space.
    float3 view;
    // The ellipse's axes out to `extent` σ, in pixels (y the way focal.y's sign says).
    float2 axis1;
    float2 axis2;
    // How many σ the axes reach: what the fragment's uv spans.
    float extent;
    // Encoded sRGB as captured (the blending happens on these values, as in training), opacity after every fade.
    float4 color;
};

// False where the splat draws nothing: nearer than 5 cm, holding the eye, or faded out.
bool SplatProject(Splat s, SplatSeen t, bool hasSeen, SplatView v, out SplatFootprint o)
{
    o.view = 0;
    o.axis1 = 0;
    o.axis2 = 0;
    o.extent = 0;
    o.color = 0;
    float4 color = SplatColor(s);
    float3 cov0, cov1;
    SplatCovariance(s, cov0, cov1);
    // What the keyframes saw of it: largest weight (negative where unknown), keyframes, sector bits, finest sampling
    // rate (pixels per metre); whether it is on the sky shell; metres beyond the depth frames' reach.
    float4 seen = float4(-1.0, 0.0, 0.0, 0.0);
    float beyond = 0.0;
    bool sky = false;
    if (hasSeen)
    {
        seen = SplatSeenOf(t);
        beyond = SplatBeyondOf(t);
        sky = SplatIsSky(s);
    }
    float3 world = mul(v.objectToWorld, float4(s.position, 1.0)).xyz;
    float3 view = mul(v.view, float4(world, 1.0)).xyz;
    float z = -view.z;
    if (z < 0.05)
        return false;

    // Covariance: object → view (Z forward), then the projection's Jacobian in pixels.
    float3x3 sigma = float3x3(cov0.x, cov0.y, cov0.z, cov0.y, cov1.x, cov1.y, cov0.z, cov1.y, cov1.z);
    float3x3 w = mul((float3x3)v.view, (float3x3)v.objectToWorld);
    w[2] = -w[2];
    float3x3 sc = mul(w, mul(sigma, transpose(w)));
    float2 screen = v.screen;
    float fx = v.focal.x;
    float fy = v.focal.y;
    float tx = clamp(view.x / z, -v.limit.x, v.limit.x) * z;
    float ty = clamp(view.y / z, -v.limit.y, v.limit.y) * z;
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
        return false;
    color.a *= smoothstep(0.05, 0.2, length(view));
    if (v.far.x > 0.5 && seen.x >= 0.0 && !sky)
    {
        float past = beyond / v.far.y;
        if (v.far.x < 1.5)
            color.rgb = lerp(color.rgb, v.horizon, 1.0 - exp(-past));
        else
            color.a *= 1.0 - smoothstep(0.0, 1.0, past);
    }
    if (v.confidence.x > 0.0 && seen.x >= 0.0 && !sky)
    {
        // The camera in the cloud's own space (where the sectors were measured), from the splat.
        float3 towardEye = v.eyeInCloud - s.position;
        float k = v.confidence.x * (1.0 - Trust(seen, towardEye, max(fx, fy)));
        if (v.confidence.y < 0.5)
            color.a *= 1.0 - k;
        else if (v.confidence.y < 1.5)
        {
            // Wider by a Gaussian of up to 16 px², its weight kept as the 2D filter keeps it.
            float before = det;
            a += k * 16.0;
            c += k * 16.0;
            det = a * c - b * b;
            color.a *= sqrt(before / det);
        }
        else
            color.rgb = lerp(color.rgb, v.horizon, k);
    }
    extent = min(3.33, sqrt(2.0 * log(max(color.a * 255.0, 1.0))));
    if (extent <= 0.0)
        return false;
    float mid = 0.5 * (a + c);
    float r = sqrt(max(0.1, mid * mid - det));
    float cap = min(1024.0, min(screen.x, screen.y)) / extent;
    float l1 = min(mid + r, cap * cap);
    float l2 = min(max(mid - r, 0.1), cap * cap);
    float2 e1 = abs(b) > 1e-6 ? normalize(float2(b, mid + r - a)) : (a >= c ? float2(1, 0) : float2(0, 1));
    float2 e2 = float2(-e1.y, e1.x);
    o.view = view;
    o.axis1 = sqrt(l1) * extent * e1;
    o.axis2 = sqrt(l2) * extent * e2;
    o.extent = extent;
    o.color = color;
    return true;
}

// What SplatProject.compute writes for each splat, in back-to-front order, and Splats.shader's pass 3 draws: its
// footprint in 36 bytes, extent 0 where it draws nothing.
struct SplatQuad
{
    float3 view;
    float2 axis1;
    // The second axis's length (it is the first turned a quarter turn anticlockwise).
    float radius2;
    float extent;
    float alpha;
    // r | g << 10 | b << 20, 10 bits each (within 1/2046, an eighth of an 8-bit step).
    uint rgb;
};

// Whether any pixel of the footprint can be on screen: its centre under `projection` (the camera's own, OpenGL-style:
// visible z within ±w; every corner shares the centre's z and w, so the GPU clips the whole quad exactly then), and the
// circle through its corners against the screen's rectangle.
bool SplatOnScreen(SplatFootprint f, float4x4 projection, float2 screen)
{
    float4 clip = mul(projection, float4(f.view, 1.0));
    if (!(clip.w > 0.0) || clip.z < -clip.w || clip.z > clip.w)
        return false;
    float2 centre = (clip.xy / clip.w * 0.5 + 0.5) * screen;
    float reach = sqrt(dot(f.axis1, f.axis1) + dot(f.axis2, f.axis2));
    return centre.x + reach >= 0.0 && centre.x - reach <= screen.x && centre.y + reach >= 0.0 && centre.y - reach <= screen.y;
}

SplatQuad SplatQuadOf(SplatFootprint f)
{
    SplatQuad q;
    q.view = f.view;
    q.axis1 = f.axis1;
    q.radius2 = length(f.axis2);
    q.extent = f.extent;
    q.alpha = f.color.a;
    uint3 c = (uint3)(saturate(f.color.rgb) * 1023.0 + 0.5);
    q.rgb = c.x | (c.y << 10) | (c.z << 20);
    return q;
}

SplatQuad SplatNoQuad()
{
    SplatQuad q;
    q.view = 0;
    q.axis1 = 0;
    q.radius2 = 0;
    q.extent = 0;
    q.alpha = 0;
    q.rgb = 0u;
    return q;
}

#endif
