using UnityEngine;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class TransferFunction2DGenerator : MonoBehaviour
{
    [System.Serializable]
    public class TF2DRegion
    {
        public string name;
        public Vector2 densityRange;
        public Vector2 gradientRange;
        public Gradient colorGradient;
        public float opacity;
        public AnimationCurve falloff;
    }

    public List<TF2DRegion> regions = new List<TF2DRegion>();
    public int resolution = 256;

    private Texture2D tfTexture2D;

    public Texture2D GenerateTexture()
    {
        tfTexture2D = null;
        
        tfTexture2D = new Texture2D(resolution, resolution, TextureFormat.RGBAHalf, false);
        tfTexture2D.wrapMode = TextureWrapMode.Clamp;
        tfTexture2D.filterMode = FilterMode.Bilinear;

        Color[] pixels = new Color[resolution * resolution];

        for (int y = 0; y < resolution; y++)
        {
            float gradMag = (float)y / (resolution - 1);

            for (int x = 0; x < resolution; x++)
            {
                float density = (float)x / (resolution - 1);

                Color finalColor = Color.clear;

                foreach (var region in regions)
                {
                    float densityWeight = CalculateWeight(density, region.densityRange, region.falloff);
                    float gradientWeight = CalculateWeight(gradMag, region.gradientRange, region.falloff);
                    float weight = densityWeight * gradientWeight;

                    if (weight > 0.001f)
                    {
                        float normalizedDensity = Mathf.InverseLerp(region.densityRange.x, region.densityRange.y, density);
                        normalizedDensity = Mathf.Clamp01(normalizedDensity);

                        Color regionColor = region.colorGradient.Evaluate(normalizedDensity);
                        float alpha = region.opacity * weight;

                        // Front-to-back compositing
                        finalColor.r += (1 - finalColor.a) * alpha * regionColor.r;
                        finalColor.g += (1 - finalColor.a) * alpha * regionColor.g;
                        finalColor.b += (1 - finalColor.a) * alpha * regionColor.b;
                        finalColor.a += (1 - finalColor.a) * alpha;
                    }
                }

                pixels[y * resolution + x] = finalColor;
            }
        }


        tfTexture2D.SetPixels(pixels);
        tfTexture2D.Apply();

        GetComponent<Renderer>().sharedMaterial.SetTexture("_TransferFunctionTexture", tfTexture2D);

        return tfTexture2D;
    }

    // Fixed weight calculation - smooth falloff from center to edges
    private float CalculateWeight(float value, Vector2 range, AnimationCurve falloff)
    {
        float center = (range.x + range.y) * 0.5f;
        float halfWidth = (range.y - range.x) * 0.5f;

        if (halfWidth <= 0) return 0;

        float distFromCenter = Mathf.Abs(value - center);
        float normalizedDist = distFromCenter / halfWidth;

        // If outside range, return 0
        if (normalizedDist > 1.0f) return 0;

        // Evaluate falloff: 1 at center, 0 at edges
        // The falloff curve goes from 0->1, so we need to flip the input
        return falloff.Evaluate(1.0f - normalizedDist);
    }

    public static class MedicalTFPresets
    {
        private static AnimationCurve SmoothFalloff => new AnimationCurve(
            new Keyframe(0f, 0f, 0f, 0f),
            new Keyframe(0.5f, 0.8f, 1.5f, 1.5f),
            new Keyframe(1f, 1f, 0f, 0f)
        );

        private static AnimationCurve SharpFalloff => new AnimationCurve(
            new Keyframe(0f, 0f, 0f, 0f),
            new Keyframe(0.2f, 0.9f, 2f, 2f),
            new Keyframe(1f, 1f, 0f, 0f)
        );

        private static AnimationCurve GaussianFalloff => new AnimationCurve(
            new Keyframe(0f, 0f, 0f, 0f),
            new Keyframe(0.3f, 0.5f, 1.2f, 1.2f),
            new Keyframe(0.7f, 0.95f, 0.5f, 0.5f),
            new Keyframe(1f, 1f, 0f, 0f)
        );

        // Flat falloff - constant weight across entire range
        private static AnimationCurve FlatFalloff => new AnimationCurve(
            new Keyframe(0f, 1f),
            new Keyframe(1f, 1f)
        );

        private static Gradient CreateGradient(params (float time, Color color)[] keys)
        {
            Gradient gradient = new Gradient();

            GradientColorKey[] colorKeys = new GradientColorKey[keys.Length];
            GradientAlphaKey[] alphaKeys = new GradientAlphaKey[keys.Length];

            for (int i = 0; i < keys.Length; i++)
            {
                colorKeys[i] = new GradientColorKey(keys[i].color, keys[i].time);
                alphaKeys[i] = new GradientAlphaKey(keys[i].color.a, keys[i].time);
            }

            gradient.SetKeys(colorKeys, alphaKeys);
            return gradient;
        }

        public static List<TF2DRegion> GetBonePreset()
        {
            return new List<TF2DRegion>
        {
            // Background/Air - very transparent
            new TF2DRegion
            {
                name = "Background",
                densityRange = new Vector2(0.0f, 0.15f),
                gradientRange = new Vector2(0.0f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, new Color(0.0f, 0.0f, 0.0f)),
                    (1.0f, new Color(0.0f, 0.0f, 0.0f))
                ),
                opacity = 0.0f,
                falloff = FlatFalloff
            },
            // Soft tissue - semi-transparent
            new TF2DRegion
            {
                name = "Soft Tissue",
                densityRange = new Vector2(0.15f, 0.45f),
                gradientRange = new Vector2(0.0f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, new Color(0.8f, 0.5f, 0.4f)),
                    (1.0f, new Color(0.9f, 0.6f, 0.5f))
                ),
                opacity = 0.05f,
                falloff = FlatFalloff
            },
            // Cancellous bone
            new TF2DRegion
            {
                name = "Cancellous Bone",
                densityRange = new Vector2(0.4f, 0.65f),
                gradientRange = new Vector2(0.0f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, new Color(0.75f, 0.65f, 0.55f)),
                    (0.5f, new Color(0.85f, 0.75f, 0.65f)),
                    (1.0f, new Color(0.90f, 0.82f, 0.72f))
                ),
                opacity = 0.4f,
                falloff = SmoothFalloff
            },
            // Cortical bone
            new TF2DRegion
            {
                name = "Cortical Bone",
                densityRange = new Vector2(0.55f, 1.0f),
                gradientRange = new Vector2(0.0f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, new Color(0.88f, 0.85f, 0.78f)),
                    (0.5f, new Color(0.95f, 0.93f, 0.88f)),
                    (1.0f, new Color(1.0f, 1.0f, 0.98f))
                ),
                opacity = 0.9f,
                falloff = SmoothFalloff
            }
        };
        }

        public static List<TF2DRegion> GetSoftTissuePreset()
        {
            return new List<TF2DRegion>
        {
            new TF2DRegion
            {
                name = "Background",
                densityRange = new Vector2(0.0f, 0.2f),
                gradientRange = new Vector2(0.0f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, Color.black),
                    (1.0f, Color.black)
                ),
                opacity = 0.0f,
                falloff = FlatFalloff
            },
            new TF2DRegion
            {
                name = "Fat",
                densityRange = new Vector2(0.2f, 0.35f),
                gradientRange = new Vector2(0.0f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, new Color(0.9f, 0.8f, 0.4f)),
                    (1.0f, new Color(1.0f, 0.9f, 0.5f))
                ),
                opacity = 0.1f,
                falloff = FlatFalloff
            },
            new TF2DRegion
            {
                name = "Muscle",
                densityRange = new Vector2(0.3f, 0.5f),
                gradientRange = new Vector2(0.0f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, new Color(0.6f, 0.25f, 0.22f)),
                    (0.5f, new Color(0.75f, 0.32f, 0.28f)),
                    (1.0f, new Color(0.85f, 0.40f, 0.35f))
                ),
                opacity = 0.3f,
                falloff = SmoothFalloff
            },
            new TF2DRegion
            {
                name = "Skin Surface",
                densityRange = new Vector2(0.35f, 0.55f),
                gradientRange = new Vector2(0.3f, 1.0f),  // High gradient = surface
                colorGradient = CreateGradient(
                    (0.0f, new Color(0.92f, 0.72f, 0.62f)),
                    (0.5f, new Color(1.0f, 0.80f, 0.70f)),
                    (1.0f, new Color(1.0f, 0.85f, 0.75f))
                ),
                opacity = 0.7f,
                falloff = SmoothFalloff
            },
            new TF2DRegion
            {
                name = "Bone",
                densityRange = new Vector2(0.5f, 1.0f),
                gradientRange = new Vector2(0.0f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, new Color(0.9f, 0.88f, 0.82f)),
                    (1.0f, new Color(1.0f, 1.0f, 0.95f))
                ),
                opacity = 0.8f,
                falloff = SmoothFalloff
            }
        };
        }

        public static List<TF2DRegion> GetVesselPreset()
        {
            return new List<TF2DRegion>
        {
            new TF2DRegion
            {
                name = "Background",
                densityRange = new Vector2(0.0f, 0.3f),
                gradientRange = new Vector2(0.0f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, Color.black),
                    (1.0f, Color.black)
                ),
                opacity = 0.0f,
                falloff = FlatFalloff
            },
            new TF2DRegion
            {
                name = "Soft Tissue",
                densityRange = new Vector2(0.25f, 0.5f),
                gradientRange = new Vector2(0.0f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, new Color(0.7f, 0.5f, 0.45f)),
                    (1.0f, new Color(0.8f, 0.6f, 0.55f))
                ),
                opacity = 0.02f,
                falloff = FlatFalloff
            },
            new TF2DRegion
            {
                name = "Vessels",
                densityRange = new Vector2(0.45f, 0.85f),
                gradientRange = new Vector2(0.0f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, new Color(0.7f, 0.1f, 0.1f)),
                    (0.4f, new Color(0.9f, 0.2f, 0.15f)),
                    (0.7f, new Color(1.0f, 0.3f, 0.2f)),
                    (1.0f, new Color(1.0f, 0.5f, 0.4f))
                ),
                opacity = 0.95f,
                falloff = SharpFalloff
            },
            new TF2DRegion
            {
                name = "Bone",
                densityRange = new Vector2(0.7f, 1.0f),
                gradientRange = new Vector2(0.0f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, new Color(0.9f, 0.9f, 0.85f)),
                    (1.0f, new Color(1.0f, 1.0f, 0.95f))
                ),
                opacity = 0.3f,
                falloff = SmoothFalloff
            }
        };
        }

        public static List<TF2DRegion> GetLungPreset()
        {
            return new List<TF2DRegion>
        {
            new TF2DRegion
            {
                name = "Air",
                densityRange = new Vector2(0.0f, 0.1f),
                gradientRange = new Vector2(0.0f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, Color.black),
                    (1.0f, Color.black)
                ),
                opacity = 0.0f,
                falloff = FlatFalloff
            },
            new TF2DRegion
            {
                name = "Lung Parenchyma",
                densityRange = new Vector2(0.05f, 0.3f),
                gradientRange = new Vector2(0.0f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, new Color(0.4f, 0.5f, 0.7f)),
                    (0.5f, new Color(0.55f, 0.65f, 0.82f)),
                    (1.0f, new Color(0.7f, 0.8f, 0.92f))
                ),
                opacity = 0.08f,
                falloff = FlatFalloff
            },
            new TF2DRegion
            {
                name = "Airways Wall",
                densityRange = new Vector2(0.1f, 0.25f),
                gradientRange = new Vector2(0.4f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, new Color(0.3f, 0.45f, 0.65f)),
                    (1.0f, new Color(0.45f, 0.6f, 0.8f))
                ),
                opacity = 0.5f,
                falloff = SmoothFalloff
            },
            new TF2DRegion
            {
                name = "Soft Tissue",
                densityRange = new Vector2(0.3f, 0.55f),
                gradientRange = new Vector2(0.0f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, new Color(0.75f, 0.55f, 0.5f)),
                    (1.0f, new Color(0.88f, 0.68f, 0.62f))
                ),
                opacity = 0.15f,
                falloff = FlatFalloff
            },
            new TF2DRegion
            {
                name = "Nodules/Dense",
                densityRange = new Vector2(0.4f, 0.65f),
                gradientRange = new Vector2(0.3f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, new Color(0.95f, 0.8f, 0.2f)),
                    (0.5f, new Color(1.0f, 0.9f, 0.3f)),
                    (1.0f, new Color(1.0f, 0.95f, 0.5f))
                ),
                opacity = 0.9f,
                falloff = SharpFalloff
            },
            new TF2DRegion
            {
                name = "Bone",
                densityRange = new Vector2(0.55f, 1.0f),
                gradientRange = new Vector2(0.0f, 1.0f),
                colorGradient = CreateGradient(
                    (0.0f, new Color(0.9f, 0.88f, 0.82f)),
                    (1.0f, new Color(1.0f, 1.0f, 0.95f))
                ),
                opacity = 0.6f,
                falloff = SmoothFalloff
            }
        };
        }
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(TransferFunction2DGenerator))]
public class TransferFunction2DGeneratorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        TransferFunction2DGenerator generator = (TransferFunction2DGenerator)target;

        if (GUILayout.Button("Load Bone Preset"))
        {
            generator.regions = TransferFunction2DGenerator.MedicalTFPresets.GetBonePreset();
            generator.GenerateTexture();
        }

        if (GUILayout.Button("Load Soft Tissue Preset"))
        {
            generator.regions = TransferFunction2DGenerator.MedicalTFPresets.GetSoftTissuePreset();
            generator.GenerateTexture();
        }

        if (GUILayout.Button("Load Vessel Preset"))
        {
            generator.regions = TransferFunction2DGenerator.MedicalTFPresets.GetVesselPreset();
            generator.GenerateTexture();
        }

        if (GUILayout.Button("Load Lung Preset"))
        {
            generator.regions = TransferFunction2DGenerator.MedicalTFPresets.GetLungPreset();
            generator.GenerateTexture();
        }
    }
}

#endif