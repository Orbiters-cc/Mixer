// A cloud's splats as SplatCloud keeps them and SplatRenderer uploads them (SplatLayout.cs writes the same bits): a
// 32-byte record per splat and, where the cloud says what the keyframes saw, a 12-byte one beside it.
#ifndef ORBITERS_SPLAT_DATA
#define ORBITERS_SPLAT_DATA

struct Splat
{
    float3 position;
    // r | g << 8 | b << 16 | a << 24, encoded sRGB as captured.
    uint color;
    // The unit quaternion's three smallest components at 20 bits each, the largest one's index (bits 28-29 of .y) and
    // the sky shell's flag (bit 30 of .y).
    uint2 rotation;
    // ln(standard deviation) per axis at 21 bits over [-32, 16].
    uint2 scale;
};

struct SplatSeen
{
    // Largest weight | keyframes << 16 (halves).
    uint weightViews;
    // Compass sector bits | metres beyond the depth frames' reach << 16 (half).
    uint sectorsBeyond;
    // Finest sampling rate, pixels per metre.
    float rate;
};

float4 SplatColor(Splat s)
{
    return float4(s.color & 255u, (s.color >> 8) & 255u, (s.color >> 16) & 255u, s.color >> 24) / 255.0;
}

bool SplatIsSky(Splat s)
{
    return ((s.rotation.y >> 30) & 1u) != 0u;
}

// (x, y, z, w), unit length.
float4 SplatRotation(Splat s)
{
    uint qa = s.rotation.x & 0xFFFFFu;
    uint qb = (s.rotation.x >> 20) | ((s.rotation.y & 0xFFu) << 12);
    uint qc = (s.rotation.y >> 8) & 0xFFFFFu;
    uint largest = (s.rotation.y >> 28) & 3u;
    // Stored as round(c · √2 · 524287) + 524287: zero is exact.
    float3 c = (float3(qa, qb, qc) - 524287.0) * (1.0 / (524287.0 * 1.41421356));
    float d = sqrt(max(0.0, 1.0 - dot(c, c)));
    if (largest == 0u)
        return float4(d, c.x, c.y, c.z);
    if (largest == 1u)
        return float4(c.x, d, c.y, c.z);
    if (largest == 2u)
        return float4(c.x, c.y, d, c.z);
    return float4(c.x, c.y, c.z, d);
}

// Standard deviations along the rotated axes, metres.
float3 SplatScale(Splat s)
{
    uint q0 = s.scale.x & 0x1FFFFFu;
    uint q1 = (s.scale.x >> 21) | ((s.scale.y & 0x3FFu) << 11);
    uint q2 = (s.scale.y >> 10) & 0x1FFFFFu;
    return exp(-32.0 + float3(q0, q1, q2) * (48.0 / 2097151.0));
}

// Lower triangle of R·S²·Rᵀ: (xx, xy, xz) and (yy, yz, zz).
void SplatCovariance(Splat s, out float3 cov0, out float3 cov1)
{
    float4 q = SplatRotation(s);
    float3 sc = SplatScale(s);
    float3 s2 = sc * sc;
    float x = q.x, y = q.y, z = q.z, w = q.w;
    float3 r0 = float3(1.0 - 2.0 * (y * y + z * z), 2.0 * (x * y - w * z), 2.0 * (x * z + w * y));
    float3 r1 = float3(2.0 * (x * y + w * z), 1.0 - 2.0 * (x * x + z * z), 2.0 * (y * z - w * x));
    float3 r2 = float3(2.0 * (x * z - w * y), 2.0 * (y * z + w * x), 1.0 - 2.0 * (x * x + y * y));
    float3 m0 = r0 * s2, m1 = r1 * s2, m2 = r2 * s2;
    cov0 = float3(dot(m0, r0), dot(m0, r1), dot(m0, r2));
    cov1 = float3(dot(m1, r1), dot(m1, r2), dot(m2, r2));
}

// The IEEE half in the low 16 bits of h, by bits as SplatLayout.HalfToFloat reads it (no infinity or NaN is ever
// written: the encoder saturates).
float SplatHalf(uint h)
{
    uint sign = (h & 0x8000u) << 16;
    uint e = (h >> 10) & 0x1Fu;
    uint m = h & 0x3FFu;
    if (e == 0u)
        return asfloat(sign | asuint((float)m * 5.9604645e-8));
    return asfloat(sign | ((e + 112u) << 23) | (m << 13));
}

// Largest weight, keyframes, sector bits, finest rate.
float4 SplatSeenOf(SplatSeen t)
{
    return float4(SplatHalf(t.weightViews & 0xFFFFu), SplatHalf(t.weightViews >> 16), (float)(t.sectorsBeyond & 0xFFFFu), t.rate);
}

float SplatBeyondOf(SplatSeen t)
{
    return SplatHalf(t.sectorsBeyond >> 16);
}

#endif
