using UnityEngine;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class TransferFunction2DGenerator : MonoBehaviour
{
    // =========================================================================
    // Region definition
    // =========================================================================
    [System.Serializable]
    public class TF2DRegion
    {
        [Header("Identity")]
        public string name = "New Region";
        public bool enabled = true;

        [Header("Density (X axis)")]
        public Vector2 densityRange = new Vector2(0.3f, 0.7f);
        [Tooltip("Evaluated symmetrically: 0 = edge of range, 1 = center")]
        public AnimationCurve densityFalloff = DefaultSmoothFalloff();

        [Header("Gradient Magnitude (Y axis)")]
        public Vector2 gradientRange = new Vector2(0.0f, 1.0f);
        [Tooltip("Evaluated linearly across the range: t=0 at gradientRange.x, t=1 at gradientRange.y")]
        public AnimationCurve gradientFalloff = DefaultFlatCurve();

        [Header("Appearance")]
        public Gradient colorGradient = DefaultWhiteGradient();
        [Range(0f, 1f)]
        public float baseOpacity = 0.5f;

        [Header("Boundary Emphasis")]
        [Tooltip("Extra opacity boost interpolated from boundaryThreshold → 1.0 on the normalized gradient axis")]
        [Range(0f, 1f)]
        public float boundaryBoost = 0.0f;
        [Tooltip("Normalized gradient threshold where boundary boost begins")]
        [Range(0f, 1f)]
        public float boundaryThreshold = 0.3f;

        [Header("Blending")]
        [Range(0f, 2f)]
        public float weight = 1.0f;

        // --- defaults --------------------------------------------------------
        static AnimationCurve DefaultSmoothFalloff()
        {
            return new AnimationCurve(
                new Keyframe(0f, 0f, 0f, 0f),
                new Keyframe(0.5f, 0.8f, 1.5f, 1.5f),
                new Keyframe(1f, 1f, 0f, 0f));
        }
        static AnimationCurve DefaultFlatCurve()
        {
            return AnimationCurve.Linear(0f, 1f, 1f, 1f);
        }
        static Gradient DefaultWhiteGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f),
                        new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f),
                        new GradientAlphaKey(1f, 1f) });
            return g;
        }
    }

    // =========================================================================
    // Blend modes
    // =========================================================================
    public enum BlendMode
    {
        Additive,
        Max,
        FrontToBack
    }

    // =========================================================================
    // Generator settings
    // =========================================================================
    public List<TF2DRegion> regions = new List<TF2DRegion>();
    [Range(64, 512)]
    public int resolution = 256;
    public BlendMode blendMode = BlendMode.Additive;
    [Tooltip("Overall opacity multiplier applied after blending")]
    [Range(0.1f, 3f)]
    public float globalOpacityScale = 1.0f;

    [Header("Output")]
    public string materialTextureProperty = "_TransferFunctionTexture";

    private Texture2D tfTexture2D;

    // =========================================================================
    // Generation
    // =========================================================================
    public Texture2D GenerateTexture()
    {
        if (tfTexture2D == null
            || tfTexture2D.width != resolution
            || tfTexture2D.height != resolution)
        {
            if (tfTexture2D != null)
                DestroyImmediate(tfTexture2D);

            tfTexture2D = new Texture2D(resolution, resolution,
                                        TextureFormat.RGBAHalf, false);
            tfTexture2D.wrapMode  = TextureWrapMode.Clamp;
            tfTexture2D.filterMode = FilterMode.Bilinear;
            tfTexture2D.name = "TF2D_Generated";
        }

        Color[] pixels = new Color[resolution * resolution];
        float invRes = 1f / (resolution - 1);

        for (int y = 0; y < resolution; y++)
        {
            float gradMag = y * invRes;   // 0 = flat interior, 1 = sharpest boundary
            for (int x = 0; x < resolution; x++)
            {
                float density = x * invRes;
                pixels[y * resolution + x] = ComputePixel(density, gradMag);
            }
        }

        tfTexture2D.SetPixels(pixels);
        tfTexture2D.Apply();

        var renderer = GetComponent<Renderer>();
        if (renderer != null && renderer.sharedMaterial != null)
            renderer.sharedMaterial.SetTexture(materialTextureProperty, tfTexture2D);

        return tfTexture2D;
    }

    Color ComputePixel(float density, float gradMag)
    {
        switch (blendMode)
        {
            case BlendMode.Additive:    return ComputeAdditive(density, gradMag);
            case BlendMode.Max:         return ComputeMax(density, gradMag);
            case BlendMode.FrontToBack: return ComputeFrontToBack(density, gradMag);
            default:                    return ComputeAdditive(density, gradMag);
        }
    }

    // =========================================================================
    // Blending modes
    // =========================================================================

    // --- Additive: weighted sum, alpha-weighted color averaging -------------
    Color ComputeAdditive(float density, float gradMag)
    {
        float accR = 0f, accG = 0f, accB = 0f;
        float totalAlpha = 0f;

        foreach (var region in regions)
        {
            if (!region.enabled) continue;

            float w = ComputeRegionWeight(region, density, gradMag);
            if (w < 0.001f) continue;

            float normDensity = NormalizeToDensityRange(density, region);
            Color col = region.colorGradient.Evaluate(normDensity);

            float alpha = ComputeRegionAlpha(region, gradMag) * w * region.weight;

            // Premultiplied accumulation
            accR += col.r * alpha;
            accG += col.g * alpha;
            accB += col.b * alpha;
            totalAlpha += alpha;
        }

        // Scale by global opacity
        totalAlpha *= globalOpacityScale;

        // Recover straight color from premultiplied accumulation
        float rawAlpha = totalAlpha / Mathf.Max(globalOpacityScale, 0.001f);
        if (rawAlpha > 0.001f)
        {
            accR /= rawAlpha;
            accG /= rawAlpha;
            accB /= rawAlpha;
        }

        return new Color(
            Mathf.Clamp01(accR),
            Mathf.Clamp01(accG),
            Mathf.Clamp01(accB),
            Mathf.Clamp01(totalAlpha));
    }

    // --- Max: keep region with highest weighted alpha ----------------------
    Color ComputeMax(float density, float gradMag)
    {
        Color best = Color.clear;
        float bestAlpha = 0f;

        foreach (var region in regions)
        {
            if (!region.enabled) continue;

            float w = ComputeRegionWeight(region, density, gradMag);
            if (w < 0.001f) continue;

            float alpha = ComputeRegionAlpha(region, gradMag)
                        * w * region.weight * globalOpacityScale;

            if (alpha > bestAlpha)
            {
                float normDensity = NormalizeToDensityRange(density, region);
                Color col = region.colorGradient.Evaluate(normDensity);
                best = new Color(col.r, col.g, col.b, Mathf.Clamp01(alpha));
                bestAlpha = alpha;
            }
        }
        return best;
    }

    // --- Front-to-back compositing (order-dependent) -----------------------
    Color ComputeFrontToBack(float density, float gradMag)
    {
        Color finalColor = Color.clear;

        foreach (var region in regions)
        {
            if (!region.enabled) continue;
            if (finalColor.a >= 0.99f) break;

            float w = ComputeRegionWeight(region, density, gradMag);
            if (w < 0.001f) continue;

            float normDensity = NormalizeToDensityRange(density, region);
            Color col = region.colorGradient.Evaluate(normDensity);

            float alpha = ComputeRegionAlpha(region, gradMag)
                        * w * region.weight * globalOpacityScale;

            float remaining = 1f - finalColor.a;
            finalColor.r += remaining * alpha * col.r;
            finalColor.g += remaining * alpha * col.g;
            finalColor.b += remaining * alpha * col.b;
            finalColor.a += remaining * alpha;
        }

        return new Color(
            Mathf.Clamp01(finalColor.r),
            Mathf.Clamp01(finalColor.g),
            Mathf.Clamp01(finalColor.b),
            Mathf.Clamp01(finalColor.a));
    }

    // =========================================================================
    // Core helpers
    // =========================================================================

    float ComputeRegionWeight(TF2DRegion region, float density, float gradMag)
    {
        float dw = EvaluateDensityWeight(density, region.densityRange,
                                         region.densityFalloff);
        float gw = EvaluateGradientWeight(gradMag, region.gradientRange,
                                          region.gradientFalloff);
        return dw * gw;
    }

    float ComputeRegionAlpha(TF2DRegion region, float gradMag)
    {
        float alpha = region.baseOpacity;

        if (region.boundaryBoost > 0f)
        {
            float boundaryFactor = Mathf.InverseLerp(
                region.boundaryThreshold,
                Mathf.Min(region.boundaryThreshold + 0.3f, 1f),
                gradMag);
            alpha = Mathf.Lerp(alpha, Mathf.Min(alpha + region.boundaryBoost, 1f),
                               boundaryFactor);
        }
        return alpha;
    }

    /// <summary>
    /// Density axis: SYMMETRIC falloff around the center of the range.
    /// Good for selecting a density band (e.g. "bone = 0.5–0.8").
    /// Curve input: 0 = at the edge, 1 = at the center.
    /// </summary>
    float EvaluateDensityWeight(float value, Vector2 range, AnimationCurve falloff)
    {
        float center    = (range.x + range.y) * 0.5f;
        float halfWidth = (range.y - range.x) * 0.5f;
        if (halfWidth <= 0f) return 0f;

        float normalizedDist = Mathf.Abs(value - center) / halfWidth;
        if (normalizedDist > 1f) return 0f;

        return falloff.Evaluate(1f - normalizedDist);
    }

    /// <summary>
    /// Gradient axis: LINEAR mapping across the range.
    /// Curve input: t=0 at gradientRange.x, t=1 at gradientRange.y.
    ///
    /// This lets you define:
    ///   - Ramp-up curve (0→1): HIGH-PASS — surfaces only
    ///   - Ramp-down curve (1→0): LOW-PASS — interiors only
    ///   - Flat curve (1→1):  BAND-PASS — entire gradient range
    ///   - Bell curve: peaks at the middle of the gradient range
    /// </summary>
    float EvaluateGradientWeight(float value, Vector2 range, AnimationCurve falloff)
    {
        if (value < range.x || value > range.y) return 0f;

        float span = range.y - range.x;
        if (span <= 0f) return 0f;

        float t = (value - range.x) / span;  // 0 at range.x → 1 at range.y
        return falloff.Evaluate(t);
    }

    float NormalizeToDensityRange(float density, TF2DRegion region)
    {
        return Mathf.Clamp01(
            Mathf.InverseLerp(region.densityRange.x,
                              region.densityRange.y, density));
    }

    public Texture2D GetTexture() => tfTexture2D;

    // =========================================================================
    // Curve factory helpers (used by presets)
    // =========================================================================

    /// <summary>Ramp 0→1: use for gradient HIGH-PASS (surfaces).</summary>
    static AnimationCurve RampUpCurve()
    {
        return new AnimationCurve(
            new Keyframe(0f, 0f, 0f, 2f),
            new Keyframe(0.4f, 0.6f, 1.5f, 1.5f),
            new Keyframe(1f, 1f, 0.5f, 0f));
    }

    /// <summary>Ramp 1→0: use for gradient LOW-PASS (interiors).</summary>
    static AnimationCurve RampDownCurve()
    {
        return new AnimationCurve(
            new Keyframe(0f, 1f, 0f, -0.5f),
            new Keyframe(0.6f, 0.4f, -1.5f, -1.5f),
            new Keyframe(1f, 0f, -2f, 0f));
    }

    /// <summary>Flat at 1: entire range contributes equally.</summary>
    static AnimationCurve FlatCurve()
    {
        return AnimationCurve.Linear(0f, 1f, 1f, 1f);
    }

    /// <summary>Smooth density falloff (symmetric, center-peaked).</summary>
    static AnimationCurve SmoothDensityFalloff()
    {
        return new AnimationCurve(
            new Keyframe(0f, 0f, 0f, 0f),
            new Keyframe(0.5f, 0.8f, 1.5f, 1.5f),
            new Keyframe(1f, 1f, 0f, 0f));
    }

    /// <summary>Sharp density falloff (steep edges, wide plateau).</summary>
    static AnimationCurve SharpDensityFalloff()
    {
        return new AnimationCurve(
            new Keyframe(0f, 0f, 0f, 0f),
            new Keyframe(0.15f, 0.85f, 3f, 3f),
            new Keyframe(0.85f, 0.85f, 0f, 0f),
            new Keyframe(1f, 1f, 3f, 0f));
    }

    static AnimationCurve FlatDensityFalloff()
    {
        return AnimationCurve.Linear(0f, 1f, 1f, 1f);
    }

    static Gradient CreateGradient(params (float time, Color color)[] keys)
    {
        var gradient = new Gradient();
        var colorKeys = new GradientColorKey[keys.Length];
        var alphaKeys = new GradientAlphaKey[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            colorKeys[i] = new GradientColorKey(keys[i].color, keys[i].time);
            alphaKeys[i] = new GradientAlphaKey(keys[i].color.a, keys[i].time);
        }
        gradient.SetKeys(colorKeys, alphaKeys);
        return gradient;
    }

    // =========================================================================
    // Presets — redesigned for working gradient axis
    // =========================================================================
    //
    //  Key concept:
    //    X axis = density (normalized HU)
    //    Y axis = normalized gradient magnitude (0 = flat, 1 = sharpest edge)
    //
    //  Levoy (1988) 2D TF approach:
    //    • Interior regions → LOW gradient  → low-pass on Y (RampDown)
    //    • Surface regions  → HIGH gradient → high-pass on Y (RampUp)
    //    • Background       → any gradient  → Flat on Y
    //
    // =========================================================================
    public static class MedicalTFPresets
    {
        // =============================================================
        // BONE
        // =============================================================
        public static List<TF2DRegion> GetBonePreset()
        {
            return new List<TF2DRegion>
            {
                // Air / background — always transparent
                new TF2DRegion
                {
                    name = "Background",
                    enabled = true,
                    densityRange   = new Vector2(0.0f, 0.12f),
                    densityFalloff = FlatDensityFalloff(),
                    gradientRange  = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatCurve(),
                    colorGradient  = CreateGradient((0f, Color.black), (1f, Color.black)),
                    baseOpacity    = 0.0f,
                    boundaryBoost  = 0f,
                    weight = 1f
                },

                // Soft tissue INTERIOR — very faint, only low-gradient voxels
                new TF2DRegion
                {
                    name = "Soft Tissue Interior",
                    enabled = true,
                    densityRange   = new Vector2(0.15f, 0.45f),
                    densityFalloff = SmoothDensityFalloff(),
                    gradientRange  = new Vector2(0.0f, 0.5f),   // low gradient only
                    gradientFalloff = RampDownCurve(),            // ← LOW-PASS
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.8f, 0.5f, 0.4f)),
                        (1f, new Color(0.9f, 0.6f, 0.5f))),
                    baseOpacity    = 0.02f,
                    boundaryBoost  = 0f,
                    weight = 1f
                },

                // Soft tissue SURFACE — boundary-gated skin/organ surface
                new TF2DRegion
                {
                    name = "Soft Tissue Surface",
                    enabled = true,
                    densityRange   = new Vector2(0.15f, 0.50f),
                    densityFalloff = SmoothDensityFalloff(),
                    gradientRange  = new Vector2(0.15f, 1.0f),  // medium-to-high gradient
                    gradientFalloff = RampUpCurve(),              // ← HIGH-PASS
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.90f, 0.65f, 0.55f)),
                        (1f, new Color(0.95f, 0.72f, 0.62f))),
                    baseOpacity    = 0.12f,
                    boundaryBoost  = 0.2f,
                    boundaryThreshold = 0.4f,
                    weight = 1f
                },

                // Cancellous bone INTERIOR — low gradient, mid-high density
                new TF2DRegion
                {
                    name = "Cancellous Bone",
                    enabled = true,
                    densityRange   = new Vector2(0.40f, 0.68f),
                    densityFalloff = SmoothDensityFalloff(),
                    gradientRange  = new Vector2(0.0f, 0.45f),  // interior only
                    gradientFalloff = RampDownCurve(),            // ← LOW-PASS
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.75f, 0.65f, 0.55f)),
                        (0.5f, new Color(0.85f, 0.75f, 0.65f)),
                        (1f, new Color(0.90f, 0.82f, 0.72f))),
                    baseOpacity    = 0.35f,
                    boundaryBoost  = 0f,
                    weight = 1f
                },

                // Bone SURFACE — high gradient = boundary → opaque
                new TF2DRegion
                {
                    name = "Bone Surface",
                    enabled = true,
                    densityRange   = new Vector2(0.38f, 0.80f),
                    densityFalloff = SmoothDensityFalloff(),
                    gradientRange  = new Vector2(0.2f, 1.0f),   // surface
                    gradientFalloff = RampUpCurve(),              // ← HIGH-PASS
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.92f, 0.88f, 0.80f)),
                        (1f, new Color(1.0f, 0.97f, 0.92f))),
                    baseOpacity    = 0.85f,
                    boundaryBoost  = 0.15f,
                    boundaryThreshold = 0.5f,
                    weight = 1.2f
                },

                // Dense cortical bone — high density, any gradient
                new TF2DRegion
                {
                    name = "Cortical Bone",
                    enabled = true,
                    densityRange   = new Vector2(0.62f, 1.0f),
                    densityFalloff = SmoothDensityFalloff(),
                    gradientRange  = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatCurve(),               // all gradients
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.92f, 0.90f, 0.85f)),
                        (0.5f, new Color(0.97f, 0.95f, 0.90f)),
                        (1f, new Color(1.0f, 1.0f, 0.98f))),
                    baseOpacity    = 0.90f,
                    boundaryBoost  = 0f,
                    weight = 1f
                }
            };
        }

        // =============================================================
        // SOFT TISSUE
        // =============================================================
        public static List<TF2DRegion> GetSoftTissuePreset()
        {
            return new List<TF2DRegion>
            {
                new TF2DRegion
                {
                    name = "Background",
                    enabled = true,
                    densityRange   = new Vector2(0.0f, 0.18f),
                    densityFalloff = FlatDensityFalloff(),
                    gradientRange  = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatCurve(),
                    colorGradient  = CreateGradient((0f, Color.black), (1f, Color.black)),
                    baseOpacity = 0.0f,
                    weight = 1f
                },

                // Fat INTERIOR — faint fog
                new TF2DRegion
                {
                    name = "Fat Interior",
                    enabled = true,
                    densityRange   = new Vector2(0.18f, 0.35f),
                    densityFalloff = SmoothDensityFalloff(),
                    gradientRange  = new Vector2(0.0f, 0.4f),
                    gradientFalloff = RampDownCurve(),
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.9f, 0.8f, 0.4f)),
                        (1f, new Color(1.0f, 0.9f, 0.5f))),
                    baseOpacity = 0.03f,
                    weight = 1f
                },

                // Muscle INTERIOR — low gradient
                new TF2DRegion
                {
                    name = "Muscle Interior",
                    enabled = true,
                    densityRange   = new Vector2(0.30f, 0.52f),
                    densityFalloff = SmoothDensityFalloff(),
                    gradientRange  = new Vector2(0.0f, 0.35f),
                    gradientFalloff = RampDownCurve(),
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.60f, 0.25f, 0.22f)),
                        (0.5f, new Color(0.75f, 0.32f, 0.28f)),
                        (1f, new Color(0.85f, 0.40f, 0.35f))),
                    baseOpacity = 0.10f,
                    weight = 1f
                },

                // Skin / organ SURFACE — gradient-gated boundaries
                new TF2DRegion
                {
                    name = "Skin / Organ Surface",
                    enabled = true,
                    densityRange   = new Vector2(0.20f, 0.58f),
                    densityFalloff = SmoothDensityFalloff(),
                    gradientRange  = new Vector2(0.15f, 1.0f),
                    gradientFalloff = RampUpCurve(),
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.92f, 0.72f, 0.62f)),
                        (0.5f, new Color(1.0f, 0.80f, 0.70f)),
                        (1f, new Color(1.0f, 0.85f, 0.75f))),
                    baseOpacity = 0.55f,
                    boundaryBoost = 0.35f,
                    boundaryThreshold = 0.4f,
                    weight = 1.3f
                },

                // Organ boundary highlights — mid-density high-gradient
                new TF2DRegion
                {
                    name = "Organ Boundaries",
                    enabled = true,
                    densityRange   = new Vector2(0.35f, 0.62f),
                    densityFalloff = SmoothDensityFalloff(),
                    gradientRange  = new Vector2(0.3f, 1.0f),
                    gradientFalloff = RampUpCurve(),
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.85f, 0.55f, 0.50f)),
                        (1f, new Color(0.95f, 0.70f, 0.65f))),
                    baseOpacity = 0.45f,
                    boundaryBoost = 0.3f,
                    boundaryThreshold = 0.45f,
                    weight = 1f
                },

                // Bone — high density, all gradients
                new TF2DRegion
                {
                    name = "Bone",
                    enabled = true,
                    densityRange   = new Vector2(0.52f, 1.0f),
                    densityFalloff = SmoothDensityFalloff(),
                    gradientRange  = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatCurve(),
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.9f, 0.88f, 0.82f)),
                        (1f, new Color(1.0f, 1.0f, 0.95f))),
                    baseOpacity = 0.75f,
                    weight = 1f
                }
            };
        }

        // =============================================================
        // VESSEL
        // =============================================================
        public static List<TF2DRegion> GetVesselPreset()
        {
            return new List<TF2DRegion>
            {
                new TF2DRegion
                {
                    name = "Background",
                    enabled = true,
                    densityRange   = new Vector2(0.0f, 0.25f),
                    densityFalloff = FlatDensityFalloff(),
                    gradientRange  = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatCurve(),
                    colorGradient  = CreateGradient((0f, Color.black), (1f, Color.black)),
                    baseOpacity = 0.0f,
                    weight = 1f
                },

                // Surrounding tissue — very transparent
                new TF2DRegion
                {
                    name = "Surrounding Tissue",
                    enabled = true,
                    densityRange   = new Vector2(0.22f, 0.48f),
                    densityFalloff = SmoothDensityFalloff(),
                    gradientRange  = new Vector2(0.0f, 0.5f),
                    gradientFalloff = RampDownCurve(),
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.7f, 0.5f, 0.45f)),
                        (1f, new Color(0.8f, 0.6f, 0.55f))),
                    baseOpacity = 0.015f,
                    weight = 1f
                },

                // Vessel WALL — high gradient at vessel-tissue boundary
                new TF2DRegion
                {
                    name = "Vessel Wall",
                    enabled = true,
                    densityRange   = new Vector2(0.38f, 0.78f),
                    densityFalloff = SmoothDensityFalloff(),
                    gradientRange  = new Vector2(0.25f, 1.0f),
                    gradientFalloff = RampUpCurve(),
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.8f, 0.15f, 0.10f)),
                        (1f, new Color(1.0f, 0.35f, 0.25f))),
                    baseOpacity = 0.70f,
                    boundaryBoost = 0.3f,
                    boundaryThreshold = 0.4f,
                    weight = 1.2f
                },

                // Vessel LUMEN (contrast-filled interior) — low gradient,
                // high density
                new TF2DRegion
                {
                    name = "Vessel Lumen",
                    enabled = true,
                    densityRange   = new Vector2(0.48f, 0.88f),
                    densityFalloff = SharpDensityFalloff(),
                    gradientRange  = new Vector2(0.0f, 0.35f),
                    gradientFalloff = RampDownCurve(),            // ← interior only
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.70f, 0.10f, 0.10f)),
                        (0.4f, new Color(0.90f, 0.20f, 0.15f)),
                        (0.7f, new Color(1.0f, 0.30f, 0.20f)),
                        (1f, new Color(1.0f, 0.50f, 0.40f))),
                    baseOpacity = 0.90f,
                    weight = 1f
                },

                // Bone — subdued so vessels stand out
                new TF2DRegion
                {
                    name = "Bone",
                    enabled = true,
                    densityRange   = new Vector2(0.72f, 1.0f),
                    densityFalloff = SmoothDensityFalloff(),
                    gradientRange  = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatCurve(),
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.9f, 0.9f, 0.85f)),
                        (1f, new Color(1.0f, 1.0f, 0.95f))),
                    baseOpacity = 0.20f,
                    weight = 0.8f
                }
            };
        }

        // =============================================================
        // LUNG
        // =============================================================
        public static List<TF2DRegion> GetLungPreset()
        {
            return new List<TF2DRegion>
            {
                new TF2DRegion
                {
                    name = "Air",
                    enabled = true,
                    densityRange   = new Vector2(0.0f, 0.06f),
                    densityFalloff = FlatDensityFalloff(),
                    gradientRange  = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatCurve(),
                    colorGradient  = CreateGradient((0f, Color.black), (1f, Color.black)),
                    baseOpacity = 0.0f,
                    weight = 1f
                },

                // Lung parenchyma INTERIOR — faint volumetric fog
                new TF2DRegion
                {
                    name = "Parenchyma Interior",
                    enabled = true,
                    densityRange   = new Vector2(0.04f, 0.28f),
                    densityFalloff = SmoothDensityFalloff(),
                    gradientRange  = new Vector2(0.0f, 0.3f),
                    gradientFalloff = RampDownCurve(),
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.40f, 0.50f, 0.70f)),
                        (0.5f, new Color(0.55f, 0.65f, 0.82f)),
                        (1f, new Color(0.70f, 0.80f, 0.92f))),
                    baseOpacity = 0.03f,
                    weight = 1f
                },

                // Airway WALLS — high gradient in lung density range
                new TF2DRegion
                {
                    name = "Airway Walls",
                    enabled = true,
                    densityRange   = new Vector2(0.06f, 0.30f),
                    densityFalloff = SmoothDensityFalloff(),
                    gradientRange  = new Vector2(0.2f, 1.0f),
                    gradientFalloff = RampUpCurve(),
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.30f, 0.45f, 0.65f)),
                        (1f, new Color(0.50f, 0.65f, 0.85f))),
                    baseOpacity = 0.35f,
                    boundaryBoost = 0.45f,
                    boundaryThreshold = 0.4f,
                    weight = 1.2f
                },

                // Soft tissue — slight transparency
                new TF2DRegion
                {
                    name = "Soft Tissue",
                    enabled = true,
                    densityRange   = new Vector2(0.28f, 0.55f),
                    densityFalloff = SmoothDensityFalloff(),
                    gradientRange  = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatCurve(),
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.75f, 0.55f, 0.50f)),
                        (1f, new Color(0.88f, 0.68f, 0.62f))),
                    baseOpacity = 0.08f,
                    boundaryBoost = 0.15f,
                    boundaryThreshold = 0.35f,
                    weight = 1f
                },

                // Nodules / dense structures — sharp, high-gradient gated
                new TF2DRegion
                {
                    name = "Nodules",
                    enabled = true,
                    densityRange   = new Vector2(0.38f, 0.68f),
                    densityFalloff = SharpDensityFalloff(),
                    gradientRange  = new Vector2(0.2f, 1.0f),
                    gradientFalloff = RampUpCurve(),
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.95f, 0.80f, 0.20f)),
                        (0.5f, new Color(1.0f, 0.90f, 0.30f)),
                        (1f, new Color(1.0f, 0.95f, 0.50f))),
                    baseOpacity = 0.80f,
                    boundaryBoost = 0.20f,
                    boundaryThreshold = 0.35f,
                    weight = 1.3f
                },

                // Bone — subdued
                new TF2DRegion
                {
                    name = "Bone",
                    enabled = true,
                    densityRange   = new Vector2(0.55f, 1.0f),
                    densityFalloff = SmoothDensityFalloff(),
                    gradientRange  = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatCurve(),
                    colorGradient  = CreateGradient(
                        (0f, new Color(0.90f, 0.88f, 0.82f)),
                        (1f, new Color(1.0f, 1.0f, 0.95f))),
                    baseOpacity = 0.45f,
                    weight = 0.8f
                }
            };
        }
    }
}

// =============================================================================
// Custom Editor with texture preview and debug visualization
// =============================================================================
#if UNITY_EDITOR
[CustomEditor(typeof(TransferFunction2DGenerator))]
public class TransferFunction2DGeneratorEditor : Editor
{
    private bool showPreview = true;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        TransferFunction2DGenerator gen = (TransferFunction2DGenerator)target;

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Presets", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Bone"))
        {
            Undo.RecordObject(gen, "Load Bone Preset");
            gen.regions = TransferFunction2DGenerator.MedicalTFPresets.GetBonePreset();
            gen.GenerateTexture();
            EditorUtility.SetDirty(gen);
        }
        if (GUILayout.Button("Soft Tissue"))
        {
            Undo.RecordObject(gen, "Load Soft Tissue Preset");
            gen.regions = TransferFunction2DGenerator.MedicalTFPresets.GetSoftTissuePreset();
            gen.GenerateTexture();
            EditorUtility.SetDirty(gen);
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Vessel"))
        {
            Undo.RecordObject(gen, "Load Vessel Preset");
            gen.regions = TransferFunction2DGenerator.MedicalTFPresets.GetVesselPreset();
            gen.GenerateTexture();
            EditorUtility.SetDirty(gen);
        }
        if (GUILayout.Button("Lung"))
        {
            Undo.RecordObject(gen, "Load Lung Preset");
            gen.regions = TransferFunction2DGenerator.MedicalTFPresets.GetLungPreset();
            gen.GenerateTexture();
            EditorUtility.SetDirty(gen);
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(5);
        if (GUILayout.Button("Generate / Refresh Texture", GUILayout.Height(30)))
        {
            gen.GenerateTexture();
        }

        // --- texture preview -------------------------------------------------
        Texture2D tex = gen.GetTexture();
        if (tex != null)
        {
            EditorGUILayout.Space(10);
            showPreview = EditorGUILayout.Foldout(showPreview,
                "Texture Preview", true, EditorStyles.foldoutHeader);

            if (showPreview)
            {
                EditorGUILayout.LabelField(
                    "  X = Density →    Y = Gradient Magnitude ↑");

                float previewSize = EditorGUIUtility.currentViewWidth - 40;
                previewSize = Mathf.Min(previewSize, 400);

                Rect r = GUILayoutUtility.GetRect(previewSize, previewSize);
                EditorGUI.DrawPreviewTexture(r, tex);

                Rect xLabel = new Rect(r.x, r.yMax + 2, r.width, 16);
                EditorGUI.LabelField(xLabel,
                    "0                    Density →                    1",
                    EditorStyles.miniLabel);

                EditorGUILayout.Space(5);
                if (GUILayout.Button("Save Texture as Asset"))
                {
                    string path = EditorUtility.SaveFilePanelInProject(
                        "Save TF Texture", "TF2D", "asset",
                        "Save the transfer function texture");
                    if (!string.IsNullOrEmpty(path))
                    {
                        Texture2D copy = new Texture2D(tex.width, tex.height,
                            tex.format, false);
                        copy.SetPixels(tex.GetPixels());
                        copy.Apply();
                        AssetDatabase.CreateAsset(copy, path);
                        AssetDatabase.SaveAssets();
                        Debug.Log($"Saved TF texture to {path}");
                    }
                }
            }
        }
    }
}
#endif