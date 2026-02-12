Shader "Unlit/ImprovedVolumeRender"
{
    Properties
    {
        [Header(Volume Data)]
        _VolumeDataTexture ("Data Texture", 3D) = "" {}
        [KeywordEnum(DVR, SUR, MIP)] _MODE("Rendering Mode", Float) = 0
        [Toggle(GRADIENT_TEXTURE)]_EnableGradientTexture("Enable Gradient Texture", Float) = 0
        _GradientTexture ("Gradient Texture", 3D) = "" {}
        [KeywordEnum(TEX1D, TEX2D)] _TF("Transfer Function Type", Float) = 0
        _TransferFunctionTexture("Transfer Function Texture", 2D) = "white" {}
        _NoiseTexture("Noise Texture", 2D) = "white" {}
        _MaxGradientMagnitude("Max Gradient Magnitude", Range(0.1, 5.0)) = 1.0

        [Header(Cross Sections)]
        [Toggle(CROSS_SECTION_ON)]_EnableCrossSection("Enable Cross Section", Float) = 0

        [Header(Windowing)]
        _WindowCenter("Window Center (HU)", Float) = 300
        _WindowWidth("Window Width (HU)", Float) = 2000
        _OriginWindowCenter("Original Data Center (HU)", Float) = 300
        _OriginWindowWidth("Original Data Width (HU)", Float) = 2000

        [Header(Ray Marching)]
        _MaxRayMarchingSteps("Max Steps", Int) = 512
        _SampleRateMultiplier("Sample Rate Multiplier", Float) = 1.0
        _MinVal("Min val", Range(0.0, 1.0)) = 0.0
        _MaxVal("Max val", Range(0.0, 1.0)) = 1.0
        _MinGradient("Surface Gradient Threshold (SUR mode)", Range(0.0, 1.0)) = 0.0
        _DepthAlphaThreshold("Depth Write Alpha Threshold", Range(0.0, 1.0)) = 0.15
        _ClipAlphaThreshold("Clip Alpha Threshold", Range(0.0, 1.0)) = 0.01
        _GradientDelta("Gradient Sample Delta", Range(0.001, 0.02)) = 0.005

        [Header(Gradient Quality)]
        [Toggle(SMOOTH_GRADIENT)]_EnableSmoothGradient("Enable Smooth Gradient (multi-scale)", Float) = 1
        _GradientSmoothScale("Smooth Scale Multiplier", Range(1.5, 4.0)) = 2.0
        _GradientSmoothBlend("Fine/Coarse Blend", Range(0.0, 1.0)) = 0.4

        [Header(Volumetric Lighting Model)]
        _ExtinctionScale("Extinction Scale", Range(0.1, 200.0)) = 30.0
        _Anisotropy("Forward Scattering g", Range(0.0, 0.95)) = 0.3
        _BackScatterAniso("Back Scatter g2", Range(-0.95, 0.0)) = -0.2
        _BackScatterBlend("Back Scatter Blend", Range(0.0, 1.0)) = 0.15
        _PhaseIntensityClamp("Phase Max Intensity", Range(1.0, 8.0)) = 3.0
        _LightIntensity("Light Intensity", Range(0.0, 5.0)) = 1.5
        _AmbientIntensity("Ambient Intensity", Range(0.0, 1.0)) = 0.2
        _AmbientSkyColor("Ambient Sky Color", Color) = (0.6, 0.7, 0.9, 1.0)
        _AmbientGroundColor("Ambient Ground Color", Color) = (0.3, 0.25, 0.2, 1.0)

        [Header(Multi Scattering Approximation)]
        _MultiScatterStrength("Multi-Scatter Strength", Range(0.0, 1.0)) = 0.35
        _MultiScatterPhaseAtten("Phase Attenuation per Bounce", Range(0.1, 0.8)) = 0.5

        [Header(Subsurface Scattering)]
        _SubsurfaceStrength("Subsurface Strength", Range(0.0, 2.0)) = 0.6
        _SubsurfaceDistortion("Normal Distortion", Range(0.0, 1.0)) = 0.3
        _SubsurfacePower("Falloff Power", Range(1.0, 16.0)) = 4.0
        _SubsurfaceColor("Subsurface Tint", Color) = (1.0, 0.35, 0.2, 1.0)

        [Header(Surface Highlights at Boundaries)]
        _SurfaceBlendStart("Surface Blend Start", Range(0.0, 1.0)) = 0.15
        _SurfaceBlendEnd("Surface Blend End", Range(0.0, 1.0)) = 0.5
        _DiffuseWrap("Diffuse Wrap", Range(0.0, 1.0)) = 0.5
        _SpecularStrength("Specular Strength", Range(0.0, 2.0)) = 0.5
        _Shininess("Shininess", Range(1.0, 256.0)) = 64.0
        _FresnelStrength("Fresnel Strength", Range(0.0, 2.0)) = 0.3
        _FresnelPower("Fresnel Power", Range(1.0, 10.0)) = 3.0

        [Header(Volume Shadows)]
        [Toggle(SHADOW_ON)]_EnableShadows("Enable Shadows", Float) = 0
        _ShadowSteps("Shadow Steps", Range(4, 32)) = 8
        _ShadowMaxDist("Shadow Max Distance", Range(0.05, 1.0)) = 0.3
        _ShadowIntensity("Shadow Intensity", Range(0.0, 2.0)) = 1.0
        _ShadowColorAbsorption("Shadow Color Absorption", Range(0.0, 1.0)) = 0.5

        [Header(Ambient Occlusion)]
        [Toggle(AO_ON)]_EnableAO("Enable Ambient Occlusion", Float) = 0
        _AOStrength("AO Strength", Range(0.0, 3.0)) = 1.5
        _AORadius("AO Radius", Range(0.01, 0.2)) = 0.05
        _AOSteps("AO Distance Steps", Range(1, 4)) = 2

        [Header(Tone Mapping)]
        [Toggle(TONEMAP_ON)]_EnableToneMapping("Enable Tone Mapping", Float) = 0
        _Exposure("Exposure", Range(0.1, 3.0)) = 1.0
        _Contrast("Contrast", Range(0.5, 2.0)) = 1.1
        _Saturation("Saturation", Range(0.0, 2.0)) = 1.0

        [Header(Animation)]
        [Toggle(ENABLE_ANIMATION)]_EnableAnimation("Enable Animation", Float) = 0
        _AnimationAmplitude("Amplitude", Range(0.0, 0.1)) = 0.02
        _AnimationSpeed("Speed", Float) = 2.0

        [Header(Debug)]
        [KeywordEnum(NONE, RAY_DIR, RAY_ORIGIN, INTERSECTION, DENSITY, WINDOWED, TF_COLOR, NORMALS, GRAD_MAG, EXTINCTION, SHADOW, AO, PHASE, MULTI_SCATTER, SUBSURFACE)] _DEBUG("Debug Mode", Float) = 0
    }

    SubShader
    {
        Pass
        {
            Name "Universal Forward"
            Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
            LOD 100
            Cull Front
            ZTest LEqual
            ZWrite On
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Input.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _MaxGradientMagnitude;
                float _WindowCenter;
                float _WindowWidth;
                float _OriginWindowCenter;
                float _OriginWindowWidth;
                float _MaxRayMarchingSteps;
                float _SampleRateMultiplier;
                float _MinVal;
                float _MaxVal;
                float _MinGradient;
                float _DepthAlphaThreshold;
                float _ClipAlphaThreshold;
                float _GradientDelta;

                // Gradient quality
                float _GradientSmoothScale;
                float _GradientSmoothBlend;

                float _ExtinctionScale;
                float _Anisotropy;
                float _BackScatterAniso;
                float _BackScatterBlend;
                float _PhaseIntensityClamp;
                float _LightIntensity;
                float _AmbientIntensity;
                float4 _AmbientSkyColor;
                float4 _AmbientGroundColor;

                // Multi-scattering
                float _MultiScatterStrength;
                float _MultiScatterPhaseAtten;

                // Subsurface
                float _SubsurfaceStrength;
                float _SubsurfaceDistortion;
                float _SubsurfacePower;
                float4 _SubsurfaceColor;

                float _SurfaceBlendStart;
                float _SurfaceBlendEnd;
                float _DiffuseWrap;
                float _SpecularStrength;
                float _Shininess;
                float _FresnelStrength;
                float _FresnelPower;

                float _ShadowSteps;
                float _ShadowMaxDist;
                float _ShadowIntensity;
                float _ShadowColorAbsorption;

                float _AOStrength;
                float _AORadius;
                float _AOSteps;

                float _Exposure;
                float _Contrast;
                float _Saturation;

                float _AnimationAmplitude;
                float _AnimationSpeed;
            CBUFFER_END

            sampler3D _VolumeDataTexture;
            sampler3D _GradientTexture;
            sampler2D _TransferFunctionTexture;
            sampler2D _NoiseTexture;

            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5

            #pragma shader_feature _MODE_DVR _MODE_SUR _MODE_MIP
            #pragma shader_feature _ GRADIENT_TEXTURE
            #pragma shader_feature _ SMOOTH_GRADIENT
            #pragma shader_feature _ SHADOW_ON
            #pragma shader_feature _TF_TEX1D _TF_TEX2D
            #pragma shader_feature _ CROSS_SECTION_ON
            #pragma multi_compile DEPTH_WRITE_ON DEPTH_WRITE_OFF
            #pragma multi_compile _ USE_MAIN_LIGHT
            #pragma shader_feature _ ENABLE_ANIMATION
            #pragma shader_feature _ AO_ON
            #pragma shader_feature _ TONEMAP_ON

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE

            #pragma shader_feature _DEBUG_NONE _DEBUG_RAY_DIR _DEBUG_RAY_ORIGIN _DEBUG_INTERSECTION _DEBUG_DENSITY _DEBUG_WINDOWED _DEBUG_TF_COLOR _DEBUG_NORMALS _DEBUG_GRAD_MAG _DEBUG_EXTINCTION _DEBUG_SHADOW _DEBUG_AO _DEBUG_PHASE _DEBUG_MULTI_SCATTER _DEBUG_SUBSURFACE

            // -----------------------------------------------------------------
            struct Attributes
            {
                UNITY_VERTEX_INPUT_INSTANCE_ID
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv     : TEXCOORD0;
            };

            struct Varyings
            {
                UNITY_VERTEX_OUTPUT_STEREO
                float4 vertex      : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 vertexLocal : TEXCOORD1;
                float3 normal      : NORMAL;
            };

            struct FragmentOutput
            {
                half4 color : SV_Target;
                #if DEPTH_WRITE_ON
                    float depth : SV_Depth;
                #endif
            };

            // -----------------------------------------------------------------
            #define TRANSMITTANCE_THRESHOLD (1.0 / 255.0)
            #define PI 3.14159265359
            #define INV_4PI 0.07957747154

            // =================================================================
            // Section 1 : Core Sampling  [IMPROVED: multi-scale gradient]
            // =================================================================

            float DensityToHU(float density)
            {
                return density * _OriginWindowWidth
                + (_OriginWindowCenter - _OriginWindowWidth * 0.5);
            }

            float ApplyWindowing(float hu)
            {
                float lo = _WindowCenter - _WindowWidth * 0.5;
                return saturate((hu - lo) / _WindowWidth);
            }

            float SampleDensity(float3 pos)
            {
                return tex3Dlod(_VolumeDataTexture, float4(pos, 0)).r;
            }

            float SampleWindowedDensity(float3 pos)
            {
                return ApplyWindowing(DensityToHU(SampleDensity(pos)));
            }

            // [IMPROVED] Central differences at a single scale
            float3 CentralDiffGradient(float3 pos, float delta)
            {
                float3 g;
                g.x = SampleDensity(pos + float3(delta,0,0))
                - SampleDensity(pos - float3(delta,0,0));
                g.y = SampleDensity(pos + float3(0,delta,0))
                - SampleDensity(pos - float3(0,delta,0));
                g.z = SampleDensity(pos + float3(0,0,delta))
                - SampleDensity(pos - float3(0,0,delta));
                return g / (2.0 * delta);
            }

            // [NEW] Multi-scale gradient: blend fine + coarse central diffs.
            //  Coarse scale acts as low-pass filter → smoother normals on
            //  bone surfaces.  Fine scale preserves small-feature detail.
            //  Total cost: 12 tex reads instead of 6.
            float3 SampleGradient(float3 pos)
            {
                #if defined(GRADIENT_TEXTURE)
                    return tex3Dlod(_GradientTexture, float4(pos, 0)).rgb
                    * 2.0 - 1.0;
                #elif defined(SMOOTH_GRADIENT)
                    float3 gFine   = CentralDiffGradient(pos, _GradientDelta);
                    float3 gCoarse = CentralDiffGradient(pos,
                    _GradientDelta * _GradientSmoothScale);
                    return lerp(gFine, gCoarse, _GradientSmoothBlend);
                #else
                    return CentralDiffGradient(pos, _GradientDelta);
                #endif
            }

            // =================================================================
            // Section 2 : Ray Utilities  (unchanged)
            // =================================================================

            float3 GetLightDirection(float3 viewDir)
            {
                #if defined(USE_MAIN_LIGHT)
                    return normalize(mul((float3x3)unity_WorldToObject,
                    _MainLightPosition.xyz));
                #else
                    return viewDir;
                #endif
            }

            float3 GetLightColor()
            {
                #if defined(USE_MAIN_LIGHT)
                    return _MainLightColor.rgb;
                #else
                    return float3(1, 1, 1);
                #endif
            }

            float3 GetViewRayDir(float3 vertexLocal)
            {
                float3 cam = mul(unity_WorldToObject,
                float4(_WorldSpaceCameraPos, 1)).xyz;
                if (unity_OrthoParams.w == 0)
                return normalize(vertexLocal - cam);
                else
                {
                    float3 fwd = -UNITY_MATRIX_V[2].xyz;
                    return normalize(mul((float3x3)unity_WorldToObject, fwd));
                }
            }

            float2 IntersectAABB(float3 o, float3 d, float3 bmin, float3 bmax)
            {
                float3 inv = 1.0 / d;
                float3 t0 = (bmin - o) * inv;
                float3 t1 = (bmax - o) * inv;
                float3 tlo = min(t0, t1);
                float3 thi = max(t0, t1);
                return float2(max(max(tlo.x, tlo.y), tlo.z),
                min(min(thi.x, thi.y), thi.z));
            }

            float LocalToDepth(float3 localPos)
            {
                float4 c = TransformObjectToHClip(float4(localPos, 1));
                #if defined(SHADER_API_GLCORE) || defined(SHADER_API_OPENGL) \
                    || defined(SHADER_API_GLES)   || defined(SHADER_API_GLES3)
                    return (c.z / c.w) * 0.5 + 0.5;
                #else
                    return c.z / c.w;
                #endif
            }

            // =================================================================
            // Section 3 : Cross Section  (unchanged)
            // =================================================================
            #if defined(CROSS_SECTION_ON)
                #define CS_PLANE     1
                #define CS_BOX_INCL  2
                #define CS_BOX_EXCL  3
                #define CS_SPH_INCL  4
                #define CS_SPH_EXCL  5

                float4x4 _CrossSectionMatrices[8];
                float    _CrossSectionTypes[8];
                int      _NumCrossSections;

                bool IsCutout(float3 p)
                {
                    float4 pp = float4(p - 0.5, 1);
                    bool cut = false;
                    for (int i = 0; i < _NumCrossSections && !cut; i++)
                    {
                        int   t = (int)_CrossSectionTypes[i];
                        float3 s = mul(_CrossSectionMatrices[i], pp).xyz;
                        if      (t == CS_PLANE)    cut = s.z > 0;
                        else if (t == CS_BOX_INCL)  cut = any(abs(s) > 0.5);
                        else if (t == CS_BOX_EXCL)  cut = all(abs(s) <= 0.5);
                        else if (t == CS_SPH_INCL)  cut = length(s) > 0.5;
                        else if (t == CS_SPH_EXCL)  cut = length(s) < 0.5;
                    }
                    return cut;
                }
            #endif

            // =================================================================
            // Section 4 : Phase Functions  [IMPROVED: clamped output]
            // =================================================================

            float HenyeyGreenstein(float cosTheta, float g)
            {
                float g2 = g * g;
                float d  = 1.0 + g2 - 2.0 * g * cosTheta;
                return INV_4PI * (1.0 - g2) / pow(max(d, 1e-5), 1.5);
            }

            // [FIXED] Headlight-aware phase evaluation
            //  When light = view direction, cosTheta is always -1.
            //  Forward-scatter HG returns very low values at cosTheta=-1,
            //  making the entire volume dim.  Detect this case and blend
            //  toward isotropic to maintain usable brightness.
            float EvaluatePhase(float cosTheta, bool isHeadlight)
            {
                float fwd  = HenyeyGreenstein(cosTheta, _Anisotropy);
                float back = HenyeyGreenstein(cosTheta, _BackScatterAniso);
                float phase = lerp(fwd, back, _BackScatterBlend);
                phase *= 4.0 * PI;

                if (isHeadlight)
                {
                    // Blend toward isotropic (1.0) for headlight so volumes
                    // remain visible regardless of anisotropy settings
                    float iso = 1.0;
                    phase = lerp(phase, iso, 0.7);
                }

                return min(phase, _PhaseIntensityClamp);
            }

            // =================================================================
            // Section 5 : Extinction  [IMPROVED: multi-scatter compensation]
            //
            //  Single-scattering underestimates brightness in optically thick
            //  tissue because it ignores light that scatters multiple times
            //  before reaching the camera.
            //
            //  Approximation (Fong et al. / Wrenninge):
            //    Each successive bounce attenuates the phase function by g
            //    and reduces extinction.  We sum a geometric series:
            //
            //      L_ms ≈ L_ss × (1 + a + a² + ...) = L_ss / (1 - a)
            //
            //  where a = strength × g_atten.  In practice we apply this as
            //  a boost to the in-scattered radiance.
            // =================================================================

            float ComputeExtinction(float tfAlpha)
            {
                return -log(max(1.0 - tfAlpha, 1e-4)) * _ExtinctionScale;
            }

            // [NEW] Multi-scatter energy boost factor.
            //  transmittance: current accumulated transmittance along view ray
            //  Returns a multiplier ≥ 1.0 to apply to in-scattered radiance.
            float MultiScatterBoost(float transmittance)
            {
                // As transmittance drops (deeper in volume), multi-scatter
                // contributes more.  The phase attenuation factor controls
                // how quickly higher-order bounces lose directionality.
                float opticalDepth = -log(max(transmittance, 1e-4));
                float a = _MultiScatterStrength * _MultiScatterPhaseAtten;
                // Ramp up contribution with optical depth
                float depthFactor = saturate(opticalDepth * 0.5);
                return 1.0 + a * depthFactor / (1.0 - a + 1e-4);
            }

            // =================================================================
            // Section 6 : Volume Shadows  [IMPROVED: colored absorption]
            //
            //  Beer-Lambert with per-channel absorption.  Tissue absorbs
            //  shorter wavelengths more than red → shadows through tissue
            //  take on a warm tint, matching the reference.
            // =================================================================

            #if defined(SHADOW_ON)
                // [IMPROVED] Returns float3 color transmittance instead of scalar
                float3 ComputeShadowTransmittance(float3 pos, float3 lightDir)
                {
                    float3 T = float3(1, 1, 1);
                    int    N = (int)_ShadowSteps;
                    float  ds = _ShadowMaxDist / (float)N;

                    float j = frac(sin(dot(pos, float3(12.9898,78.233,45.164)))
                    * 43758.5453);

                    [loop] for (int i = 1; i <= N; i++)
                    {
                        float3 sp = pos + lightDir * ds * ((float)i + j * 0.5);

                        if (any(sp < 0) || any(sp > 1)) break;

                        #if defined(CROSS_SECTION_ON)
                            if (IsCutout(sp)) continue;
                        #endif

                        float w = ApplyWindowing(DensityToHU(
                        tex3Dlod(_VolumeDataTexture,
                        float4(sp, 0)).r));
                        if (w < _MinVal || w > _MaxVal) continue;

                        // [IMPROVED] Sample full TF (1D for perf) to get color
                        float4 tfShadow = tex2Dlod(_TransferFunctionTexture,
                        float4(w, 0, 0, 0));

                        float ext = ComputeExtinction(tfShadow.a)
                        * _ShadowIntensity;

                        // [NEW] Per-channel absorption: tissue color modulates
                        // which wavelengths are absorbed.  Pure white tissue
                        // absorbs all channels equally (grayscale shadow);
                        // red tissue absorbs blue/green more → warm shadow.
                        float3 absorb = lerp(float3(1,1,1),
                        1.0 - tfShadow.rgb + 0.01,
                        _ShadowColorAbsorption);
                        T *= exp(-ext * ds * absorb);

                        if (all(T < 0.01)) break;
                    }
                    return T;
                }

                // Scalar version for backward compat / SUR mode
                float ComputeShadowTransmittanceScalar(float3 pos, float3 lightDir)
                {
                    float3 T = ComputeShadowTransmittance(pos, lightDir);
                    return dot(T, float3(0.299, 0.587, 0.114)); // luminance
                }
            #endif

            // =================================================================
            // Section 7 : Volumetric Ambient Occlusion  [IMPROVED: TF-aware]
            // =================================================================

            #if defined(AO_ON)
                float ComputeVolumetricAO(float3 pos, float3 normal)
                {
                    static const float3 dirs[6] = {
                        float3( 1, 0, 0), float3(-1, 0, 0),
                        float3( 0, 1, 0), float3( 0,-1, 0),
                        float3( 0, 0, 1), float3( 0, 0,-1)
                    };

                    float occ = 0;
                    float wt  = 0;
                    int   steps = (int)_AOSteps;

                    [unroll] for (int d = 0; d < 6; d++)
                    {
                        float dw = max(dot(dirs[d], normal), 0.05);

                        [loop] for (int s = 1; s <= steps; s++)
                        {
                            float r = _AORadius * (float)s;
                            float3 sp = pos + dirs[d] * r;

                            if (any(sp < 0) || any(sp > 1)) continue;

                            // [IMPROVED] Use windowed density × TF alpha so that
                            // regions made transparent by TF don't cause occlusion
                            float raw = tex3Dlod(_VolumeDataTexture,
                            float4(sp, 0)).r;
                            float w = ApplyWindowing(DensityToHU(raw));

                            float tfAlpha = 0;
                            if (w >= _MinVal && w <= _MaxVal)
                            {
                                tfAlpha = tex2Dlod(_TransferFunctionTexture,
                                float4(w, 0, 0, 0)).a;
                            }

                            float distW = 1.0 / (float)s;
                            occ += tfAlpha * dw * distW;
                            wt  += dw * distW;
                        }
                    }

                    occ /= max(wt, 1e-4);
                    return 1.0 - saturate(occ * _AOStrength);
                }
            #endif

            // =================================================================
            // Section 8 : Hybrid Lighting  [IMPROVED: multi-scatter + SSS]
            //
            //  Additions:
            //  1. Multi-scatter boost for volumetric component
            //  2. Subsurface scattering back-light approximation
            //  3. Colored shadow integration
            //  4. Better energy conservation for surface component
            // =================================================================

            // [NEW] Subsurface scattering approximation (GDC wrap/distort)
            //  Models light transmitting through thin tissue regions.
            //  Uses the inverted normal + light distortion trick.
            float3 SubsurfaceScatter(float3 normal, float3 lightDir,
            float3 viewDir, float3 albedo,
            float3 shadowT)
            {
                // Distorted back-facing light vector
                float3 sssLight = lightDir + normal * _SubsurfaceDistortion;
                float  sssDot   = saturate(dot(viewDir, -sssLight));
                float  sss      = pow(sssDot, _SubsurfacePower)
                * _SubsurfaceStrength;

                // Tint by subsurface color and albedo
                float3 sssColor = _SubsurfaceColor.rgb * albedo;

                // Shadow transmittance modulates SSS — light must reach
                // the back side of the tissue
                return sssColor * sss * shadowT;
            }

            float3 ComputeHybridLighting(
            float3 albedo,
            float3 normal,
            float  normGradMag,
            float3 lightDir,
            float3 viewDir,
            float3 rayDir,
            float3 shadowT,
            float  ao,
            float  msBoost,
            bool   isHeadlight)    // ← add this
            {
                float3 lightCol = GetLightColor() * _LightIntensity;

                float cosTheta = dot(lightDir, rayDir);
                float phase    = EvaluatePhase(cosTheta, isHeadlight);  // ← pass it

                // [IMPROVED] Apply multi-scatter boost to volumetric term
                float3 volDirect = lightCol * phase * shadowT * msBoost;

                // Hemisphere ambient
                float hemi = 0.5;
                if (normGradMag > 0.01)
                hemi = dot(normal, float3(0, 1, 0)) * 0.5 + 0.5;
                float3 ambCol = lerp(_AmbientGroundColor.rgb,
                _AmbientSkyColor.rgb, hemi);
                float3 ambient = ambCol * _AmbientIntensity;

                float3 volLight = volDirect + ambient;

                // ---- surface component (strong gradient = boundary) ---------
                float3 surfLight = ambient;

                if (normGradMag > 0.01)
                {
                    // Wrapped diffuse
                    float NdotL = dot(normal, lightDir);
                    float diff  = max((NdotL + _DiffuseWrap)
                    / (1.0 + _DiffuseWrap), 0.0);

                    // Blinn-Phong specular
                    float3 H    = normalize(lightDir + viewDir);
                    float NdotH = max(dot(normal, H), 0.0);
                    float spec  = pow(NdotH, _Shininess) * _SpecularStrength;

                    // [IMPROVED] Energy conservation: diffuse + spec shouldn't
                    // exceed 1.0 before light color multiplication
                    float surfBRDF = min(diff + spec, 1.5);

                    // Fresnel rim
                    float NdotV = max(dot(normal, viewDir), 0.0);
                    float rim   = pow(1.0 - NdotV, _FresnelPower)
                    * _FresnelStrength;

                    surfLight += lightCol * surfBRDF * shadowT;
                    surfLight += lightCol * rim * 0.5; // rim not shadowed fully
                }

                // ---- subsurface scattering ----------------------------------
                // [NEW] Applies to both volumetric and surface regimes but
                // strongest at mid-gradient (tissue surfaces/boundaries)
                float3 sss = float3(0,0,0);
                if (_SubsurfaceStrength > 0.001)
                {
                    sss = SubsurfaceScatter(normal, lightDir, viewDir,
                    albedo, shadowT);
                    // Weight SSS by an inverted surface blend — strongest
                    // for semi-transparent tissue, weakest for hard bone
                    float sssWeight = 1.0 - smoothstep(
                    _SurfaceBlendEnd, _SurfaceBlendEnd + 0.2,
                    normGradMag);
                    sss *= sssWeight;
                }

                // ---- blend volumetric ↔ surface ----------------------------
                float blend = smoothstep(_SurfaceBlendStart,
                _SurfaceBlendEnd,
                normGradMag);
                float3 lighting = lerp(volLight, surfLight, blend);

                lighting *= ao;

                return albedo * lighting + sss;
            }

            // =================================================================
            // Section 9 : Tone Mapping  (unchanged)
            // =================================================================

            #if defined(TONEMAP_ON)
                float3 ACESFilm(float3 x)
                {
                    return saturate((x * (2.51 * x + 0.03))
                    / (x * (2.43 * x + 0.59) + 0.14));
                }

                float3 ApplyToneMapping(float3 c)
                {
                    c *= _Exposure;
                    c  = ACESFilm(c);
                    c  = saturate(lerp(0.5, c, _Contrast));
                    float lum = dot(c, float3(0.299, 0.587, 0.114));
                    c  = lerp(lum.xxx, c, _Saturation);
                    return c;
                }
            #endif

            // =================================================================
            // Section 10 : Animation  (unchanged)
            // =================================================================

            float3 GetAnimationDisplacement(float3 pos, float density)
            {
                #if defined(ENABLE_ANIMATION)
                    float3 off   = pos - 0.5;
                    float  ph    = sin(_Time.y * _AnimationSpeed) * 0.5 + 0.5;
                    float  scale = saturate(1.0 - density * 2.0);
                    return normalize(off + 0.001) * ph
                    * _AnimationAmplitude * scale;
                #else
                    return 0;
                #endif
            }

            // =================================================================
            // Section 11 : Vertex / Fragment  [IMPROVED: multi-scatter tracking]
            // =================================================================

            Varyings vert(Attributes a)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(a);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex      = TransformObjectToHClip(a.vertex.xyz);
                o.uv          = a.uv;
                o.vertexLocal = a.vertex.xyz;
                o.normal      = TransformObjectToWorldNormal(a.normal.xyz);
                return o;
            }

            FragmentOutput frag(Varyings input)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input)
                FragmentOutput output = (FragmentOutput)0;

                // ---- ray setup ----------------------------------------------
                float3 camLocal = mul(unity_WorldToObject,
                float4(_WorldSpaceCameraPos, 1)).xyz;
                float3 rayDir   = GetViewRayDir(input.vertexLocal);
                float3 viewDir  = -rayDir;
                float3 lightDir = GetLightDirection(viewDir);

                float3 rayOrigin = (unity_OrthoParams.w == 0)
                ? camLocal + 0.5
                : input.vertexLocal + 0.5;

                float2 isect = IntersectAABB(rayOrigin, rayDir,
                float3(0,0,0), float3(1,1,1));
                isect.x = max(isect.x, 0);

                // ---- quick debug outputs ------------------------------------
                #if defined(_DEBUG_RAY_DIR)
                    output.color = float4(rayDir * 0.5 + 0.5, 1);
                    return output;
                #elif defined(_DEBUG_RAY_ORIGIN)
                    output.color = float4(rayOrigin, 1);
                    return output;
                #elif defined(_DEBUG_INTERSECTION)
                    output.color = (isect.x > isect.y)
                    ? float4(1,0,0,1)
                    : float4(0, isect.y - isect.x, 0, 1);
                    return output;
                #elif defined(_DEBUG_PHASE)
                    float ct = dot(lightDir, rayDir);
                    float ph = EvaluatePhase(ct);
                    ph = saturate(ph * 0.25);
                    output.color = float4(ph.xxx, 1);
                    return output;
                #endif

                if (isect.x > isect.y) discard;

                // ---- march parameters ---------------------------------------
                float3 rayStart = rayOrigin + rayDir * isect.x;
                float3 rayEnd   = rayOrigin + rayDir * isect.y;
                float  rayLen   = isect.y - isect.x;

                int   sampleRate = (int)(_MaxRayMarchingSteps
                * _SampleRateMultiplier);
                float baseStep   = 1.732 / sampleRate;
                int   numSteps   = (int)clamp(ceil(rayLen / baseStep),
                1, sampleRate);
                float stepSize   = rayLen / (float)numSteps;

                float jitter = tex2D(_NoiseTexture, input.uv * 10).r;
                // Fallback: if noise texture is solid white (unassigned),
                // generate procedural jitter from screen position
                if (jitter > 0.99)
                {
                    float2 sp = input.vertex.xy;
                    jitter = frac(sin(dot(sp, float2(12.9898, 78.233)))
                    * 43758.5453);
                }
                rayStart += rayDir * stepSize * jitter;

                // ---- center-point debug outputs -----------------------------
                // ---- FIXED debug: march ray to find first hit ---------------
                #if defined(_DEBUG_DENSITY) || defined(_DEBUG_WINDOWED) \
                    || defined(_DEBUG_TF_COLOR) || defined(_DEBUG_NORMALS) \
                    || defined(_DEBUG_GRAD_MAG) || defined(_DEBUG_EXTINCTION) \
                    || defined(_DEBUG_AO) || defined(_DEBUG_SHADOW) \
                    || defined(_DEBUG_MULTI_SCATTER) || defined(_DEBUG_SUBSURFACE)
                    {
                        // March along the ray to find first opaque sample
                        float3 dp       = float3(0,0,0);
                        float  dr       = 0;
                        float  dw       = 0;
                        bool   dbgFound = false;

                        int dbgMaxSteps = min(numSteps, 256);
                        [loop] for (int dbgI = 0; dbgI < dbgMaxSteps; dbgI++)
                        {
                            float dbgT = ((float)dbgI + 0.5) / (float)numSteps;
                            float3 dbgP = lerp(rayStart, rayEnd, dbgT);

                            if (any(dbgP < 0) || any(dbgP > 1)) continue;

                            #if defined(CROSS_SECTION_ON)
                                if (IsCutout(dbgP)) continue;
                            #endif

                            float dbgRaw = SampleDensity(dbgP);
                            float dbgW   = ApplyWindowing(DensityToHU(dbgRaw));

                            if (dbgW < _MinVal || dbgW > _MaxVal) continue;

                            float4 dbgTF = tex2Dlod(_TransferFunctionTexture,
                            float4(dbgW, 0, 0, 0));
                            if (dbgTF.a > 0.01)
                            {
                                dp       = dbgP;
                                dr       = dbgRaw;
                                dw       = dbgW;
                                dbgFound = true;
                                break;
                            }
                        }

                        if (!dbgFound) discard;

                        float3 dg   = SampleGradient(dp);
                        float  dm   = length(dg);
                        float  dn   = saturate(dm / _MaxGradientMagnitude);
                        float3 dnrm = dm > 0.001 ? dg / dm : float3(0,0,1);

                        #if defined(_DEBUG_DENSITY)
                            output.color = float4(dr.xxx, 1);
                        #elif defined(_DEBUG_WINDOWED)
                            output.color = float4(dw.xxx, 1);
                        #elif defined(_DEBUG_TF_COLOR)
                            output.color = tex2Dlod(_TransferFunctionTexture,
                            float4(dw, 0, 0, 0));
                        #elif defined(_DEBUG_NORMALS)
                            output.color = float4(dnrm * 0.5 + 0.5, 1);
                        #elif defined(_DEBUG_GRAD_MAG)
                            output.color = float4(dn.xxx, 1);
                        #elif defined(_DEBUG_EXTINCTION)
                            {
                                float4 dtf = tex2Dlod(_TransferFunctionTexture,
                                float4(dw, 0, 0, 0));
                                float  de  = ComputeExtinction(dtf.a);
                                output.color = float4(saturate(de * 0.01).xxx, 1);
                            }
                        #elif defined(_DEBUG_SHADOW)
                            #if defined(SHADOW_ON)
                                float3 ds = ComputeShadowTransmittance(dp, lightDir);
                                output.color = float4(ds, 1);
                            #else
                                output.color = float4(1, 0, 1, 1);
                            #endif
                        #elif defined(_DEBUG_AO)
                            #if defined(AO_ON)
                                float da = ComputeVolumetricAO(dp, dnrm);
                                output.color = float4(da.xxx, 1);
                            #else
                                output.color = float4(1, 0, 1, 1);
                            #endif
                        #elif defined(_DEBUG_MULTI_SCATTER)
                            float msb = MultiScatterBoost(0.5);
                            output.color = float4(saturate(msb * 0.3).xxx, 1);
                        #elif defined(_DEBUG_SUBSURFACE)
                            {
                                float3 sss = SubsurfaceScatter(dnrm, lightDir,
                                viewDir, float3(1,1,1),
                                float3(1,1,1));
                                output.color = float4(saturate(sss), 1);
                            }
                        #endif
                        return output;
                    }
                #endif

                // ---- state --------------------------------------------------
                float  transmittance = 1.0;
                float3 accumColor    = float3(0, 0, 0);
                float  tDepth        = 1.0;
                bool   depthSet      = false;

                #if _MODE_MIP
                    float  maxDensity    = 0;
                    float3 maxDensityPos = rayStart;
                #endif

                bool isHeadlight = false;
                #if !defined(USE_MAIN_LIGHT)
                    isHeadlight = true;
                #endif

                // =============================================================
                // MAIN RAY MARCH
                // =============================================================
                [loop] for (int iStep = 0; iStep < numSteps; iStep++)
                {
                    float t = ((float)iStep + 0.5) / (float)numSteps;
                    float3 currPos = lerp(rayStart, rayEnd, t);

                    if (any(currPos < 0) || any(currPos > 1)) continue;

                    #if defined(CROSS_SECTION_ON)
                        if (IsCutout(currPos)) continue;
                    #endif

                    // ---- sample position (with optional animation) ----------
                    float3 samplePos = currPos;
                    #if defined(ENABLE_ANIMATION)
                        {
                            float3 disp = GetAnimationDisplacement(currPos, 0.5);
                            float3 ap   = currPos - disp;
                            if (all(ap >= 0) && all(ap <= 1)) samplePos = ap;
                        }
                    #endif

                    // ---- density & windowing --------------------------------
                    float raw = tex3Dlod(_VolumeDataTexture,
                    float4(samplePos, 0)).r;
                    float windowed = ApplyWindowing(DensityToHU(raw));

                    if (windowed < _MinVal || windowed > _MaxVal) continue;

                    // ---- gradient & transfer function ------------------------
                    float3 gradient;
                    float  gradMag;
                    float  normGrad;
                    float4 tfColor;

                    #if defined(_TF_TEX2D)
                        gradient = SampleGradient(samplePos);
                        gradMag  = length(gradient);
                        normGrad = saturate(gradMag / _MaxGradientMagnitude);
                        tfColor  = tex2Dlod(_TransferFunctionTexture,
                        float4(windowed, normGrad, 0, 0));
                    #else
                        tfColor  = tex2Dlod(_TransferFunctionTexture,
                        float4(windowed, 0, 0, 0));
                    #endif

                    if (tfColor.a < 0.001) continue;

                    // Deferred gradient for 1-D TF
                    #if !defined(_TF_TEX2D)
                        gradient = SampleGradient(samplePos);
                        gradMag  = length(gradient);
                        normGrad = saturate(gradMag / _MaxGradientMagnitude);
                    #endif

                    // =========================================================
                    #if _MODE_DVR
                        // =========================================================
                        {
                            // ---- extinction & step opacity ----------------------
                            float ext   = ComputeExtinction(tfColor.a);
                            float stepT = exp(-ext * stepSize);
                            float stepA = 1.0 - stepT;

                            // ---- normal -----------------------------------------
                            float3 N = viewDir;
                            if (gradMag > 0.001)
                            {
                                N = gradient / gradMag;
                                if (dot(N, viewDir) < 0) N = -N;
                            }

                            // ---- shadow -----------------------------------------
                            float3 shadowT = float3(1, 1, 1);
                            #if defined(SHADOW_ON)
                                if (stepA > 0.005)
                                shadowT = ComputeShadowTransmittance(
                                samplePos, lightDir);
                            #endif

                            // ---- AO (only at surface-like boundaries) -----------
                            float ao = 1.0;
                            #if defined(AO_ON)
                                if (normGrad > _SurfaceBlendStart)
                                ao = ComputeVolumetricAO(samplePos, N);
                            #endif

                            // ---- multi-scatter boost ----------------------------
                            float msBoost = MultiScatterBoost(transmittance);

                            // ---- lighting ---------------------------------------
                            float3 lit = ComputeHybridLighting(
                            tfColor.rgb, N, normGrad,
                            lightDir, viewDir, rayDir,
                            shadowT, ao, msBoost, isHeadlight);  // DVR mode

                            // ---- accumulate (emission-absorption) ---------------
                            accumColor   += transmittance * lit * stepA;
                            transmittance *= stepT;

                            // ---- depth write ------------------------------------
                            if (!depthSet
                            && (1.0 - transmittance) > _DepthAlphaThreshold)
                            {
                                tDepth   = t;
                                depthSet = true;
                            }

                            if (transmittance < TRANSMITTANCE_THRESHOLD) break;
                        }

                        // =========================================================
                    #elif _MODE_SUR
                        // =========================================================
                        {
                            if (gradMag > _MinGradient * _MaxGradientMagnitude)
                            {
                                float3 N = gradient / gradMag;
                                if (dot(N, viewDir) < 0) N = -N;

                                float3 shadowT = float3(1, 1, 1);
                                #if defined(SHADOW_ON)
                                    shadowT = ComputeShadowTransmittance(
                                    samplePos, lightDir);
                                #endif

                                float ao = 1.0;
                                #if defined(AO_ON)
                                    ao = ComputeVolumetricAO(samplePos, N);
                                #endif

                                accumColor = ComputeHybridLighting(
                                tfColor.rgb, N, normGrad,
                                lightDir, viewDir, rayDir,
                                shadowT, ao, 1.0, isHeadlight);
                                transmittance = 0;
                                tDepth = t;
                                break;
                            }
                        }

                        // =========================================================
                    #elif _MODE_MIP
                        // =========================================================
                        {
                            if (windowed > maxDensity)
                            {
                                maxDensity    = windowed;
                                maxDensityPos = currPos;
                                tDepth        = t;
                            }
                        }
                    #endif
                }
                // ================== end march ================================

                #if _MODE_MIP
                    accumColor    = maxDensity.xxx;
                    transmittance = (maxDensity > 0.01) ? 0 : 1;
                #endif

                float finalAlpha = 1.0 - transmittance;

                #if defined(TONEMAP_ON)
                    accumColor = ApplyToneMapping(accumColor);
                #endif

                if (finalAlpha < _ClipAlphaThreshold) discard;

                output.color = saturate(float4(accumColor, finalAlpha));

                #if DEPTH_WRITE_ON
                    {
                        float3 dp;
                        #if _MODE_MIP
                            dp = maxDensityPos - 0.5;
                        #else
                            dp = lerp(rayStart, rayEnd, tDepth) - 0.5;
                        #endif
                        output.depth = LocalToDepth(dp);
                    }
                #endif

                return output;
            }

            ENDHLSL
        }
    }
}