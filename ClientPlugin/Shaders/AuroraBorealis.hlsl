// Aurora Borealis — volumetric raymarch through a spherical shell segment over planet poles.
// Technique based on Roy Theunissen's "difference clouds" aurora (see Docs/Plan.md §1),
// adapted from an axis-aligned box volume to a spherical shell around a planet.
//
// Compiled by the game's shader registry (entry point __pixel_shader, profile ps_5_0).
// Drawn as a fullscreen quad via MyScreenPass (vertex output = PostprocessVertex).

#include <Frame.hlsli>
#include <Postprocess/PostprocessBase.hlsli>

cbuffer AuroraConstants : register(b1)
{
    float4 CenterInner;     // xyz = planet center relative to camera (meters), w = shell inner radius
    float4 PoleOuter;       // xyz = magnetic pole axis (unit), w = shell outer radius
    float4 Tangent1;        // xyz = tangent basis vector 1 (unit, perpendicular to pole axis)
    float4 Tangent2;        // xyz = tangent basis vector 2 (unit, = pole x tangent1)
    float4 BandParams;      // x = sin(band lower lat), y = sin(band upper lat), z = feather (sin-space), w = unused
    float4 NoiseParams;     // x = layer1 UV tiling, y = layer2 UV tiling, z = threshold, w = contrast push
    float4 ScrollOffsets;   // xy = layer1 UV offset, zw = layer2 UV offset (precomputed from time)
    float4 ColorIntensity;  // rgb = HDR tint, w = master intensity
    float4 StepParams;      // x = step count, y = dither strength, z = night factor, w = curtain height variation
};

Texture2D<float4> PerlinTex : register(t20);   // R/G: difference-cloud layers, B: curtain height, A: vertical offset
Texture2D<float4> ColorRamp : register(t21);   // 256x1 vertical gradient LUT (rgb = color, a = height falloff)
Texture2D<float>  DepthTex  : register(t22);   // resolved scene depth for occlusion

SamplerState WrapSampler  : register(s6);
SamplerState ClampSampler : register(s7);

// Ray/sphere intersection around CenterInner.xyz; returns (tNear, tFar) or (-1, -1) on miss.
float2 RaySphere(float3 origin, float3 dir, float radius)
{
    float3 oc = origin - CenterInner.xyz;
    float b = dot(oc, dir);
    float c = dot(oc, oc) - radius * radius;
    float disc = b * b - c;
    if (disc < 0)
        return float2(-1, -1);
    float s = sqrt(disc);
    return float2(-b - s, -b + s);
}

// Difference-clouds curtain shape (verbatim math from the reference implementation).
float CurtainNoise(float2 uv1, float2 uv2)
{
    float a = PerlinTex.SampleLevel(WrapSampler, uv1, 0).r;
    float b = PerlinTex.SampleLevel(WrapSampler, uv2, 0).g;
    float noise = abs(a - b);
    noise = (noise - NoiseParams.z) * NoiseParams.w + NoiseParams.z;
    return 1 - saturate(noise);
}

// Cheap screen-space hash for march-start dithering.
float Hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

void __pixel_shader(PostprocessVertex input, out float4 output : SV_Target0)
{
    float nightFactor = StepParams.z;
    float intensity = ColorIntensity.w * nightFactor;
    if (intensity <= 0)
        discard;

    // World-space (camera-relative) view ray for this pixel. The uv comes from the pixel
    // position (like the game's read_gbuffer), which is pixel-centered and offset-aware.
    float2 uv = screen_to_uv(input.position.xy);
    float3 rayDir = normalize(view_to_world(compute_screen_ray(uv)));

    float innerR = CenterInner.w;
    float outerR = PoleOuter.w;

    // The ray's intersection with the shell is the outer sphere's interval minus the inner
    // sphere's. A ray that dips through the hollow under the shell and comes back out is
    // left with two disjoint segments; both have to be marched. Dropping the far one puts a
    // hard edge along the inner sphere's tangent, because a ray just missing that sphere
    // keeps its whole chord while its neighbour is cut at the entry point.
    float2 outerT = RaySphere(0, rayDir, outerR);
    if (outerT.y <= 0)
        discard;
    float tMin = max(outerT.x, 0);
    float tMax = outerT.y;

    // Scene depth occlusion: terrain and ships cut the march short.
    float hwDepth = DepthTex[uint2(input.position.xy)];
    if (IsDepthForeground(hwDepth))
    {
        float sceneDist = length(ReconstructWorldPosition(hwDepth, uv));
        tMax = min(tMax, sceneDist);
    }
    if (tMax <= tMin)
        discard;

    float seg0Start = tMin, seg0End = tMax;
    float seg1Start = 0, seg1End = 0;

    float2 innerT = RaySphere(0, rayDir, innerR);
    if (innerT.y > tMin && innerT.x < tMax)
    {
        seg0End = clamp(innerT.x, tMin, tMax);
        seg1Start = clamp(innerT.y, tMin, tMax);
        seg1End = tMax;
    }

    float len0 = max(seg0End - seg0Start, 0);
    float len1 = max(seg1End - seg1Start, 0);
    float marchLength = len0 + len1;
    if (marchLength <= 0)
        discard;

    int steps = (int)StepParams.x;
    float stepLen = marchLength / steps;

    // Jitter the march start to hide banding at low step counts.
    float jitter = Hash21(input.position.xy) * StepParams.y;

    float shellThickness = outerR - innerR;
    float heightVariation = StepParams.w;
    float3 accum = 0;

    [loop]
    for (int i = 0; i < steps; i++)
    {
        // Step along the two segments as one continuous arc length, so the step count and
        // therefore the cost stay fixed no matter how the ray meets the shell.
        float s = (i + jitter) * stepLen;
        float t = (s < len0) ? (seg0Start + s) : (seg1Start + (s - len0));

        float3 p = rayDir * t - CenterInner.xyz;   // position relative to planet center
        float r = length(p);
        float3 dir = p / r;

        // Latitude band mask (both hemispheres).
        float sinLat = abs(dot(dir, PoleOuter.xyz));
        float feather = BandParams.z;
        float bandMask = smoothstep(BandParams.x - feather, BandParams.x + feather, sinLat)
                       * (1 - smoothstep(BandParams.y - feather, BandParams.y + feather, sinLat));
        if (bandMask <= 0)
            continue;

        // Azimuthal (tangent plane) coordinates around the pole axis. A longitude/latitude
        // parameterisation would compress U as the meridians converge, smearing the noise
        // into radial spokes over the pole, and needs a seam at ±pi; this has neither.
        float2 uvBase = float2(dot(dir, Tangent1.xyz), dot(dir, Tangent2.xyz));
        float2 uv1 = uvBase * NoiseParams.x + ScrollOffsets.xy;
        float2 uv2 = uvBase * NoiseParams.y + ScrollOffsets.zw;

        float curtain = CurtainNoise(uv1, uv2);
        if (curtain <= 0)
            continue;

        // Per-column curtain height and vertical offset from the extra noise channels.
        float4 columnNoise = PerlinTex.SampleLevel(WrapSampler, uv1 * 0.37, 0);
        float columnHeight = lerp(1 - heightVariation, 1, columnNoise.b);
        float columnOffset = columnNoise.a * heightVariation * 0.5;

        // Normalized altitude within the shell, remapped by the column shape.
        float h = (r - innerR) / shellThickness;
        float hRemapped = (h - columnOffset) / max(columnHeight, 1e-3);
        if (hRemapped < 0 || hRemapped > 1)
            continue;

        float4 ramp = ColorRamp.SampleLevel(ClampSampler, float2(hRemapped, 0.5), 0);
        accum += ramp.rgb * (ramp.a * curtain * curtain * bandMask);
    }

    // Accumulated emission per unit of shell thickness. A ray crossing the shell near the
    // tangent travels many times its thickness, so the raw integral is unbounded; saturate
    // it exponentially to keep the brightest curtains inside the tint's HDR range instead
    // of flooding the sky when the camera sits under the shell.
    float3 optical = accum * (stepLen / shellThickness);
    float3 color = ColorIntensity.rgb * intensity * (1 - exp(-optical));
    output = float4(color, 1);   // additive blend; alpha is ignored
}
