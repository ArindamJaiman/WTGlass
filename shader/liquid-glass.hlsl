// Liquid Glass for Windows Terminal
// ---------------------------------
// An Apple "Liquid Glass" style pixel shader. Windows' acrylic already frosts
// the desktop behind the window; this shader turns every terminal pane into a
// slab of glass sitting on top of it.
//
// Text is never blurred. The interior passes through pixel for pixel, and
// only a band around the perimeter bends light, like a gravitational lens:
//
//   * Einstein ring  - content near the edge is pulled toward a ring where it
//                      stretches, and a flipped, squeezed copy wraps the edge
//   * frame dragging - the band swirls along the edge, so lines of text curve
//                      as they approach it
//   * dispersion     - an RGB split where the light bends hardest
//   * horizon        - the outermost strip darkens like an event horizon
//   * specular rim   - a thin light-facing highlight plus a soft bloom
//   * sheen, depth   - soft diagonal light across the body, inner shadow
//
// Use it with:  "experimental.pixelShaderPath": "<path to this file>",
//               "useAcrylic": true, "opacity": 50, "padding": "0"
//
// All sizes are in DIPs (1/96 inch) and scale with DPI. Edit a value, save,
// then save settings.json (or reopen the tab) to see the change.

Texture2D shaderTexture : register(t0);
SamplerState samplerState : register(s0);

cbuffer PixelShaderSettings : register(b0)
{
    float  Time;       // seconds since the shader was loaded
    float  Scale;      // DPI scale (1.0 = 96 DPI)
    float2 Resolution; // pane size in pixels
    float4 Background; // default background color, premultiplied
};

// ---- shape ------------------------------------------------------------------
#define CORNER_RADIUS        9.0   // matches Windows 11's rounded window corners

// ---- black-hole lensing (perimeter only) ------------------------------------
#define LENS_WIDTH          28.0   // width of the band that bends light; the
                                   // interior is never touched
#define RING_POSITION        0.28  // where the Einstein ring sits, as a fraction
                                   // of the band (0 = right at the edge)
#define MIRROR_DEPTH         0.85  // how far back the flipped copy at the edge
                                   // reaches, as a fraction of the band
#define SWIRL                0.35  // frame dragging along the edge (0 = none)
#define DISPERSION           1.2   // RGB split where light bends hardest
#define PHOTON_RING          0.12  // faint glow along the Einstein ring
#define HORIZON_SHADOW       0.30  // darkening toward the very edge

// ---- light ------------------------------------------------------------------
#define LIGHT_X             -0.55  // where the light comes from (screen space,
#define LIGHT_Y             -1.0   // y points down): top-left by default
#define SPECULAR             0.60  // thin rim highlight
#define BLOOM                0.20  // soft glow spreading in from the rim
#define FRESNEL              0.07  // edges get brighter, like real glass
#define INNER_SHADOW         0.16  // depth on the edges facing away from the light
#define SHEEN                0.05  // diagonal light across the glass body
#define ANIMATE              0     // 1 = light slowly drifts (repaints every
                                   //     frame, so it costs battery)

float4 Tap(float2 px)
{
    // The sampler's border color is transparent, so stay inside the texture.
    px = clamp(px, 0.5, Resolution - 0.5);
    return shaderTexture.SampleLevel(samplerState, px / Resolution, 0);
}

// Splits light into red, green and blue that bend by slightly different amounts.
float4 TapDispersed(float2 px, float2 spread)
{
    float4 g = Tap(px);
    if (dot(spread, spread) < 0.0025)
        return g;
    float4 r = Tap(px + spread);
    float4 b = Tap(px - spread);
    // Colors are premultiplied, so each channel stays <= the widest alpha.
    return float4(r.r, g.g, b.b, max(g.a, max(r.a, b.a)));
}

// Signed distance to a rounded box (negative inside), plus the outward normal.
float SdRoundBox(float2 p, float2 halfSize, float radius, out float2 normal)
{
    float2 q = abs(p) - halfSize + radius;
    float2 side = float2(p.x < 0.0 ? -1.0 : 1.0, p.y < 0.0 ? -1.0 : 1.0);
    if (q.x > 0.0 && q.y > 0.0)
        normal = normalize(q) * side;
    else
        normal = q.x > q.y ? float2(side.x, 0.0) : float2(0.0, side.y);
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

// The lens. For a pixel at depth u into the band (0 = edge, 1 = interior),
// returns the depth its content comes from, both as fractions of the band.
//  * ring..1  primary image: the band's content, all of it, squeezed in and
//             stretched without limit at the ring (a caustic). It meets the
//             interior at matching slope, so there is no seam.
//  * 0..ring  secondary image: a flipped, squeezed copy of the content, the
//             light that wraps around the hole.
float LensedDepth(float u)
{
    float r0 = RING_POSITION;
    if (u >= 1.0)
        return u;
    if (u >= r0)
    {
        float t = (u - r0) / (1.0 - r0);
        return (2.0 + r0) * t * t - (1.0 + r0) * t * t * t;
    }
    float s = (r0 - u) / r0;
    return MIRROR_DEPTH * s * s;
}

// Premultiplied "over".
float4 Over(float4 top, float4 bottom)
{
    return top + bottom * (1.0 - top.a);
}

float4 main(float4 pos : SV_POSITION, float2 tex : TEXCOORD) : SV_TARGET
{
    float2 px = pos.xy;
    float2 halfSize = Resolution * 0.5;
    float maxRadius = min(halfSize.x, halfSize.y);

    // Lensing. The lens shape's corner radius is at least the band width, so
    // the band's inner edge is a smooth rounded rectangle and the bend has no
    // creases at the corners.
    float lensWidth = LENS_WIDTH * Scale;
    float2 n;
    float lensRadius = min(max(lensWidth, CORNER_RADIUS * Scale), maxRadius);
    float u = min(-SdRoundBox(px - halfSize, halfSize, lensRadius, n) / lensWidth, 1.0);
    float rim = saturate(1.0 - u); // 0 in the interior, 1 at the edge

    float2 tangent = float2(-n.y, n.x);
    float2 src = px + n * ((u - LensedDepth(u)) * lensWidth)
                    + tangent * (SWIRL * lensWidth * rim * rim);
    float2 spread = n * (DISPERSION * Scale * rim * rim);
    float4 color = TapDispersed(src, spread);

    float horizon = HORIZON_SHADOW * pow(saturate(1.0 - u / RING_POSITION), 1.5);
    float ringOffset = (u - RING_POSITION) * lensWidth / Scale;
    float photon = PHOTON_RING * exp(-0.5 * ringOffset * ringOffset);

    // Light, following the actual window corners.
    float2 rimNormal;
    float depth = -SdRoundBox(px - halfSize, halfSize, min(CORNER_RADIUS * Scale, maxRadius), rimNormal);
    float2 light = normalize(float2(LIGHT_X, LIGHT_Y));
    float2 uv = px / Resolution;
#if ANIMATE
    float wobble = 0.35 * sin(Time * 0.3);
    light = float2(light.x * cos(wobble) - light.y * sin(wobble),
                   light.x * sin(wobble) + light.y * cos(wobble));
    uv += 0.06 * float2(sin(Time * 0.21), cos(Time * 0.17));
#endif
    float facing = dot(rimNormal, light);
    float lit = pow(saturate(facing), 2.0);
    // Light bouncing inside the slab also catches the opposite edge.
    float bounce = 0.45 * pow(saturate(-facing), 3.0);
    float catchLight = lit + bounce + 0.18;

    float inside = smoothstep(-0.75, 0.25, depth);
    float rimWidth = 1.25 * Scale;
    float rimLine = inside * (1.0 - smoothstep(rimWidth * 0.5, rimWidth + 0.75, depth));
    float bloom = inside * exp(-max(depth, 0.0) / (7.0 * Scale));
    float fresnel = inside * pow(saturate(1.0 - depth / (28.0 * Scale)), 3.0);
    float shade = INNER_SHADOW * pow(saturate(-facing), 1.5)
                * smoothstep(0.0, 2.0 * Scale, depth)
                * exp(-max(depth - 2.0 * Scale, 0.0) / (6.0 * Scale));
    float sheen = saturate(1.0 - (uv.x * 0.5 + uv.y) * 1.4);

    float glow = saturate(SPECULAR * rimLine * catchLight
                        + BLOOM * bloom * catchLight
                        + FRESNEL * fresnel
                        + photon * inside
                        + SHEEN * sheen * sheen);

    color = Over(float4(0.0, 0.0, 0.0, horizon * inside), color);
    color = Over(float4(0.0, 0.0, 0.0, shade), color);
    color = Over(float4(float3(0.96, 0.98, 1.0) * glow, glow), color);
    return color;
}
