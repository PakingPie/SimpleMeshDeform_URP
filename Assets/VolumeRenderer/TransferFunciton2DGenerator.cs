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
        public AnimationCurve densityFalloff = DefaultSmoothFalloff();

        [Header("Gradient Magnitude (Y axis)")]
        public Vector2 gradientRange = new Vector2(0.0f, 1.0f);
        public AnimationCurve gradientFalloff = DefaultFlatFalloff();

        [Header("Appearance")]
        public Gradient colorGradient = DefaultWhiteGradient();
        [Range(0f, 1f)]
        public float baseOpacity = 0.5f;

        [Header("Boundary Emphasis")]
        [Tooltip("Boost opacity at high gradient magnitudes (surface enhancement)")]
        [Range(0f, 1f)]
        public float boundaryBoost = 0.0f;
        [Tooltip("Gradient threshold where boundary boost begins")]
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
        static AnimationCurve DefaultFlatFalloff()
        {
            return new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(1f, 1f));
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
        Additive,       // sum contributions, clamp to 1
        Max,            // per-region, keep highest alpha
        FrontToBack     // original compositing order
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
            tfTexture2D.wrapMode = TextureWrapMode.Clamp;
            tfTexture2D.filterMode = FilterMode.Bilinear;
            tfTexture2D.name = "TF2D_Generated";
        }

        Color[] pixels = new Color[resolution * resolution];

        float invRes = 1f / (resolution - 1);

        for (int y = 0; y < resolution; y++)
        {
            float gradMag = y * invRes;

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

    // --- Additive: weighted sum of all regions, clamped --------------------
    Color ComputeAdditive(float density, float gradMag)
    {
        float3c acc = new float3c();
        float totalAlpha = 0f;
        float totalWeight = 0f;

        foreach (var region in regions)
        {
            if (!region.enabled) continue;

            float w = ComputeRegionWeight(region, density, gradMag);
            if (w < 0.001f) continue;

            float normDensity = NormalizeToDensityRange(density, region);
            Color col = region.colorGradient.Evaluate(normDensity);

            float alpha = ComputeRegionAlpha(region, gradMag) * w;

            acc.r += col.r * alpha * region.weight;
            acc.g += col.g * alpha * region.weight;
            acc.b += col.b * alpha * region.weight;
            totalAlpha += alpha * region.weight;
            totalWeight += w * region.weight;
        }

        totalAlpha *= globalOpacityScale;

        // Normalize color by total alpha to avoid oversaturation
        if (totalAlpha > 0.001f)
        {
            float invA = globalOpacityScale / Mathf.Max(totalAlpha / globalOpacityScale, 0.001f);
            // Actually: weighted average color, then apply alpha
            // The acc already has premultiplied alpha, so divide to get straight color
            acc.r /= (totalAlpha / globalOpacityScale);
            acc.g /= (totalAlpha / globalOpacityScale);
            acc.b /= (totalAlpha / globalOpacityScale);
        }

        return new Color(
            Mathf.Clamp01(acc.r),
            Mathf.Clamp01(acc.g),
            Mathf.Clamp01(acc.b),
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

            float alpha = ComputeRegionAlpha(region, gradMag) * w * region.weight;
            alpha *= globalOpacityScale;

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

    // --- Front-to-back: original compositing (order-dependent) -------------
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

            float alpha = ComputeRegionAlpha(region, gradMag) * w
                        * region.weight * globalOpacityScale;

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
    // Helpers
    // =========================================================================

    struct float3c { public float r, g, b; }

    float ComputeRegionWeight(TF2DRegion region, float density, float gradMag)
    {
        float dw = EvaluateAxisWeight(density, region.densityRange,
                                      region.densityFalloff);
        float gw = EvaluateAxisWeight(gradMag, region.gradientRange,
                                      region.gradientFalloff);
        return dw * gw;
    }

    float ComputeRegionAlpha(TF2DRegion region, float gradMag)
    {
        float alpha = region.baseOpacity;

        // Boundary emphasis: boost opacity at high gradient magnitudes
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

    float EvaluateAxisWeight(float value, Vector2 range, AnimationCurve falloff)
    {
        float center = (range.x + range.y) * 0.5f;
        float halfWidth = (range.y - range.x) * 0.5f;

        if (halfWidth <= 0f) return 0f;

        float distFromCenter = Mathf.Abs(value - center);
        float normalizedDist = distFromCenter / halfWidth;

        if (normalizedDist > 1f) return 0f;

        return falloff.Evaluate(1f - normalizedDist);
    }

    float NormalizeToDensityRange(float density, TF2DRegion region)
    {
        return Mathf.Clamp01(
            Mathf.InverseLerp(region.densityRange.x,
                              region.densityRange.y, density));
    }

    public Texture2D GetTexture() => tfTexture2D;

    // =========================================================================
    // Presets
    // =========================================================================
    public static class MedicalTFPresets
    {
        // --- falloff curves --------------------------------------------------
        static AnimationCurve SmoothFalloff => new AnimationCurve(
            new Keyframe(0f, 0f, 0f, 0f),
            new Keyframe(0.5f, 0.8f, 1.5f, 1.5f),
            new Keyframe(1f, 1f, 0f, 0f));

        static AnimationCurve SharpFalloff => new AnimationCurve(
            new Keyframe(0f, 0f, 0f, 0f),
            new Keyframe(0.2f, 0.9f, 2f, 2f),
            new Keyframe(1f, 1f, 0f, 0f));

        static AnimationCurve FlatFalloff => new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(1f, 1f));

        // Falloff that only lets high values through (for gradient gating)
        static AnimationCurve HighPassFalloff => new AnimationCurve(
            new Keyframe(0f, 0f, 0f, 0f),
            new Keyframe(0.7f, 0.5f, 2f, 2f),
            new Keyframe(1f, 1f, 0f, 0f));

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

        // =================================================================
        // Bone Preset
        //   - Uses gradient gating to sharpen bone surfaces
        //   - Interior bone is semi-transparent, surfaces are opaque
        // =================================================================
        public static List<TF2DRegion> GetBonePreset()
        {
            return new List<TF2DRegion>
            {
                new TF2DRegion
                {
                    name = "Background",
                    densityRange = new Vector2(0.0f, 0.15f),
                    densityFalloff = FlatFalloff,
                    gradientRange = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatFalloff,
                    colorGradient = CreateGradient(
                        (0f, Color.black), (1f, Color.black)),
                    baseOpacity = 0.0f,
                    boundaryBoost = 0f,
                    weight = 1f,
                    enabled = true
                },
                // Soft tissue: low opacity interior, boundary-enhanced surfaces
                new TF2DRegion
                {
                    name = "Soft Tissue",
                    densityRange = new Vector2(0.15f, 0.45f),
                    densityFalloff = SmoothFalloff,
                    gradientRange = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.8f, 0.5f, 0.4f)),
                        (1f, new Color(0.9f, 0.6f, 0.5f))),
                    baseOpacity = 0.03f,
                    boundaryBoost = 0.15f,
                    boundaryThreshold = 0.3f,
                    weight = 1f,
                    enabled = true
                },
                // Cancellous bone interior
                new TF2DRegion
                {
                    name = "Cancellous Bone",
                    densityRange = new Vector2(0.4f, 0.65f),
                    densityFalloff = SmoothFalloff,
                    gradientRange = new Vector2(0.0f, 0.5f),
                    gradientFalloff = FlatFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.75f, 0.65f, 0.55f)),
                        (0.5f, new Color(0.85f, 0.75f, 0.65f)),
                        (1f, new Color(0.90f, 0.82f, 0.72f))),
                    baseOpacity = 0.3f,
                    boundaryBoost = 0f,
                    weight = 1f,
                    enabled = true
                },
                // Bone surface: high gradient = boundary, gets strong opacity
                new TF2DRegion
                {
                    name = "Bone Surface",
                    densityRange = new Vector2(0.4f, 0.75f),
                    densityFalloff = SmoothFalloff,
                    gradientRange = new Vector2(0.25f, 1.0f),
                    gradientFalloff = HighPassFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.92f, 0.88f, 0.80f)),
                        (1f, new Color(1.0f, 0.97f, 0.92f))),
                    baseOpacity = 0.85f,
                    boundaryBoost = 0.15f,
                    boundaryThreshold = 0.4f,
                    weight = 1.2f,
                    enabled = true
                },
                // Dense cortical bone
                new TF2DRegion
                {
                    name = "Cortical Bone",
                    densityRange = new Vector2(0.6f, 1.0f),
                    densityFalloff = SmoothFalloff,
                    gradientRange = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.92f, 0.90f, 0.85f)),
                        (0.5f, new Color(0.97f, 0.95f, 0.90f)),
                        (1f, new Color(1.0f, 1.0f, 0.98f))),
                    baseOpacity = 0.9f,
                    boundaryBoost = 0f,
                    weight = 1f,
                    enabled = true
                }
            };
        }

        // =================================================================
        // Soft Tissue Preset
        //   - Skin surface highlighted via gradient gating
        //   - Interior tissue transparent, surfaces opaque
        // =================================================================
        public static List<TF2DRegion> GetSoftTissuePreset()
        {
            return new List<TF2DRegion>
            {
                new TF2DRegion
                {
                    name = "Background",
                    densityRange = new Vector2(0.0f, 0.2f),
                    densityFalloff = FlatFalloff,
                    gradientRange = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatFalloff,
                    colorGradient = CreateGradient(
                        (0f, Color.black), (1f, Color.black)),
                    baseOpacity = 0.0f,
                    weight = 1f,
                    enabled = true
                },
                // Fat: low opacity, subtle
                new TF2DRegion
                {
                    name = "Fat",
                    densityRange = new Vector2(0.2f, 0.35f),
                    densityFalloff = SmoothFalloff,
                    gradientRange = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.9f, 0.8f, 0.4f)),
                        (1f, new Color(1.0f, 0.9f, 0.5f))),
                    baseOpacity = 0.05f,
                    boundaryBoost = 0.1f,
                    boundaryThreshold = 0.3f,
                    weight = 1f,
                    enabled = true
                },
                // Muscle interior: semi-transparent
                new TF2DRegion
                {
                    name = "Muscle",
                    densityRange = new Vector2(0.3f, 0.5f),
                    densityFalloff = SmoothFalloff,
                    gradientRange = new Vector2(0.0f, 0.4f),
                    gradientFalloff = FlatFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.6f, 0.25f, 0.22f)),
                        (0.5f, new Color(0.75f, 0.32f, 0.28f)),
                        (1f, new Color(0.85f, 0.40f, 0.35f))),
                    baseOpacity = 0.15f,
                    boundaryBoost = 0f,
                    weight = 1f,
                    enabled = true
                },
                // Skin/tissue surface: gradient-gated for boundary detection
                new TF2DRegion
                {
                    name = "Skin Surface",
                    densityRange = new Vector2(0.25f, 0.55f),
                    densityFalloff = SmoothFalloff,
                    gradientRange = new Vector2(0.2f, 1.0f),
                    gradientFalloff = HighPassFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.92f, 0.72f, 0.62f)),
                        (0.5f, new Color(1.0f, 0.80f, 0.70f)),
                        (1f, new Color(1.0f, 0.85f, 0.75f))),
                    baseOpacity = 0.6f,
                    boundaryBoost = 0.35f,
                    boundaryThreshold = 0.3f,
                    weight = 1.3f,
                    enabled = true
                },
                // Organ boundaries: mid-density + high gradient
                new TF2DRegion
                {
                    name = "Organ Boundaries",
                    densityRange = new Vector2(0.35f, 0.6f),
                    densityFalloff = SmoothFalloff,
                    gradientRange = new Vector2(0.35f, 1.0f),
                    gradientFalloff = HighPassFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.85f, 0.55f, 0.50f)),
                        (1f, new Color(0.95f, 0.70f, 0.65f))),
                    baseOpacity = 0.5f,
                    boundaryBoost = 0.3f,
                    boundaryThreshold = 0.4f,
                    weight = 1f,
                    enabled = true
                },
                new TF2DRegion
                {
                    name = "Bone",
                    densityRange = new Vector2(0.5f, 1.0f),
                    densityFalloff = SmoothFalloff,
                    gradientRange = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.9f, 0.88f, 0.82f)),
                        (1f, new Color(1.0f, 1.0f, 0.95f))),
                    baseOpacity = 0.8f,
                    boundaryBoost = 0f,
                    weight = 1f,
                    enabled = true
                }
            };
        }

        // =================================================================
        // Vessel Preset
        //   - Contrast-enhanced vessels highlighted
        //   - Surrounding tissue very transparent
        // =================================================================
        public static List<TF2DRegion> GetVesselPreset()
        {
            return new List<TF2DRegion>
            {
                new TF2DRegion
                {
                    name = "Background",
                    densityRange = new Vector2(0.0f, 0.3f),
                    densityFalloff = FlatFalloff,
                    gradientRange = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatFalloff,
                    colorGradient = CreateGradient(
                        (0f, Color.black), (1f, Color.black)),
                    baseOpacity = 0.0f,
                    weight = 1f,
                    enabled = true
                },
                new TF2DRegion
                {
                    name = "Soft Tissue",
                    densityRange = new Vector2(0.25f, 0.5f),
                    densityFalloff = SmoothFalloff,
                    gradientRange = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.7f, 0.5f, 0.45f)),
                        (1f, new Color(0.8f, 0.6f, 0.55f))),
                    baseOpacity = 0.02f,
                    boundaryBoost = 0f,
                    weight = 1f,
                    enabled = true
                },
                // Vessel walls: use gradient gating to highlight boundaries
                new TF2DRegion
                {
                    name = "Vessel Walls",
                    densityRange = new Vector2(0.4f, 0.75f),
                    densityFalloff = SmoothFalloff,
                    gradientRange = new Vector2(0.3f, 1.0f),
                    gradientFalloff = HighPassFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.8f, 0.15f, 0.1f)),
                        (1f, new Color(1.0f, 0.35f, 0.25f))),
                    baseOpacity = 0.7f,
                    boundaryBoost = 0.3f,
                    boundaryThreshold = 0.35f,
                    weight = 1.2f,
                    enabled = true
                },
                // Vessel interior: contrast-filled lumen
                new TF2DRegion
                {
                    name = "Vessel Lumen",
                    densityRange = new Vector2(0.5f, 0.85f),
                    densityFalloff = SharpFalloff,
                    gradientRange = new Vector2(0.0f, 0.4f),
                    gradientFalloff = FlatFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.7f, 0.1f, 0.1f)),
                        (0.4f, new Color(0.9f, 0.2f, 0.15f)),
                        (0.7f, new Color(1.0f, 0.3f, 0.2f)),
                        (1f, new Color(1.0f, 0.5f, 0.4f))),
                    baseOpacity = 0.9f,
                    boundaryBoost = 0f,
                    weight = 1f,
                    enabled = true
                },
                new TF2DRegion
                {
                    name = "Bone",
                    densityRange = new Vector2(0.7f, 1.0f),
                    densityFalloff = SmoothFalloff,
                    gradientRange = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.9f, 0.9f, 0.85f)),
                        (1f, new Color(1.0f, 1.0f, 0.95f))),
                    baseOpacity = 0.25f,
                    boundaryBoost = 0f,
                    weight = 0.8f,
                    enabled = true
                }
            };
        }

        // =================================================================
        // Lung Preset
        //   - Airway walls via gradient gating
        //   - Parenchyma as faint fog
        //   - Nodules highlighted with sharp falloff
        // =================================================================
        public static List<TF2DRegion> GetLungPreset()
        {
            return new List<TF2DRegion>
            {
                new TF2DRegion
                {
                    name = "Air",
                    densityRange = new Vector2(0.0f, 0.08f),
                    densityFalloff = FlatFalloff,
                    gradientRange = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatFalloff,
                    colorGradient = CreateGradient(
                        (0f, Color.black), (1f, Color.black)),
                    baseOpacity = 0.0f,
                    weight = 1f,
                    enabled = true
                },
                // Lung parenchyma: faint volumetric fog
                new TF2DRegion
                {
                    name = "Lung Parenchyma",
                    densityRange = new Vector2(0.05f, 0.3f),
                    densityFalloff = SmoothFalloff,
                    gradientRange = new Vector2(0.0f, 0.35f),
                    gradientFalloff = FlatFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.4f, 0.5f, 0.7f)),
                        (0.5f, new Color(0.55f, 0.65f, 0.82f)),
                        (1f, new Color(0.7f, 0.8f, 0.92f))),
                    baseOpacity = 0.04f,
                    boundaryBoost = 0f,
                    weight = 1f,
                    enabled = true
                },
                // Airway walls: gradient-gated boundaries in lung density range
                new TF2DRegion
                {
                    name = "Airway Walls",
                    densityRange = new Vector2(0.08f, 0.28f),
                    densityFalloff = SmoothFalloff,
                    gradientRange = new Vector2(0.3f, 1.0f),
                    gradientFalloff = HighPassFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.3f, 0.45f, 0.65f)),
                        (1f, new Color(0.5f, 0.65f, 0.85f))),
                    baseOpacity = 0.4f,
                    boundaryBoost = 0.4f,
                    boundaryThreshold = 0.35f,
                    weight = 1.2f,
                    enabled = true
                },
                // Soft tissue
                new TF2DRegion
                {
                    name = "Soft Tissue",
                    densityRange = new Vector2(0.3f, 0.55f),
                    densityFalloff = SmoothFalloff,
                    gradientRange = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.75f, 0.55f, 0.5f)),
                        (1f, new Color(0.88f, 0.68f, 0.62f))),
                    baseOpacity = 0.1f,
                    boundaryBoost = 0.15f,
                    boundaryThreshold = 0.3f,
                    weight = 1f,
                    enabled = true
                },
                // Nodules/dense structures: sharp, attention-grabbing
                new TF2DRegion
                {
                    name = "Nodules",
                    densityRange = new Vector2(0.4f, 0.65f),
                    densityFalloff = SharpFalloff,
                    gradientRange = new Vector2(0.25f, 1.0f),
                    gradientFalloff = HighPassFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.95f, 0.8f, 0.2f)),
                        (0.5f, new Color(1.0f, 0.9f, 0.3f)),
                        (1f, new Color(1.0f, 0.95f, 0.5f))),
                    baseOpacity = 0.85f,
                    boundaryBoost = 0.15f,
                    boundaryThreshold = 0.3f,
                    weight = 1.3f,
                    enabled = true
                },
                new TF2DRegion
                {
                    name = "Bone",
                    densityRange = new Vector2(0.55f, 1.0f),
                    densityFalloff = SmoothFalloff,
                    gradientRange = new Vector2(0.0f, 1.0f),
                    gradientFalloff = FlatFalloff,
                    colorGradient = CreateGradient(
                        (0f, new Color(0.9f, 0.88f, 0.82f)),
                        (1f, new Color(1.0f, 1.0f, 0.95f))),
                    baseOpacity = 0.5f,
                    boundaryBoost = 0f,
                    weight = 0.8f,
                    enabled = true
                }
            };
        }
    }
}

// =============================================================================
// Custom Editor with texture preview
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
                    $"  X = Windowed Density →    Y = Gradient Magnitude ↑");

                float previewSize = EditorGUIUtility.currentViewWidth - 40;
                previewSize = Mathf.Min(previewSize, 400);

                Rect r = GUILayoutUtility.GetRect(previewSize, previewSize);
                EditorGUI.DrawPreviewTexture(r, tex);

                // axis labels
                Rect xLabel = new Rect(r.x, r.yMax + 2, r.width, 16);
                EditorGUI.LabelField(xLabel,
                    "0                              Density →                              1",
                    EditorStyles.miniLabel);

                // Save to assets
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