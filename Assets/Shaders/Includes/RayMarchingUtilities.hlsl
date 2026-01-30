#ifndef CUSTOM_RAY_MARCHING_UTILITIES_HLSL
#define CUSTOM_RAY_MARCHING_UTILITIES_HLSL
#define JITTER_FACTOR 5.0
#define AMBIENT_LIGHTING_FACTOR 0.5
#define OPACITY_THRESHOLD (1.0 - 1.0 / 255.0)

// Get the light direction (using main light or view direction, based on setting)
float3 GetLightDirection(float3 viewDir)
{
    #if SHADERGRAPH_PREVIEW
        return normalize(float3(1.0, 1.0, 0.0));
    #else
        // check if mainlight available, if not, return view direction
        #if defined(USE_MAIN_LIGHT)
            return normalize(mul(unity_WorldToObject, _MainLightPosition.xyz));
        #else

            return viewDir;
        #endif
    #endif
}

float3 CalculateLighting(float3 col, float3 normal, float3 lightDir, float3 eyeDir, float specularIntensity)
{
    normal *= (step(0.0, dot(normal, eyeDir)) * 2.0 - 1.0);
    float ndotl = max(lerp(0.0f, 1.5f, dot(normal, lightDir)), AMBIENT_LIGHTING_FACTOR);
    float3 diffuse = ndotl * col;
    float3 v = eyeDir;
    float3 r = normalize(reflect(-lightDir, normal));
    float rdotv = max( dot( r, v ), 0.0 );
    float3 specular = pow(rdotv, 32.0f) * float3(1.0f, 1.0f, 1.0f) * specularIntensity;
    return diffuse + specular;
}

// ------------------------------------------------------
// ----------------- UTILITY FUNCTIONS -------------------
half3 GetViewRayDir(half3 positionOS, half3 cameraPositionWS)
{
    if(unity_OrthoParams.w == 0)
    {
        // Perspective
        return normalize(TransformWorldToObject(cameraPositionWS) - positionOS);
    }
    else
    {
        // Orthographic
        half3 camfwd = mul((half3x3)unity_CameraToWorld, half3(0,0,-1));
        half4 camfwdobjspace = mul(unity_WorldToObject, camfwd);
        return normalize(camfwdobjspace.xyz);
    }
}

// ------------------------------------------------------
half2 IntersectAABBOrigin(half3 rayOrigin, half3 rayDir, half3 boxMin, half3 boxMax)
{
    half3 t0 = (boxMin - rayOrigin) / rayDir;
    half3 t1 = (boxMax - rayOrigin) / rayDir;
    half3 tmin = min(t0, t1);
    half3 tmax = max(t0, t1);
    half tNear = max(max(tmin.x, tmin.y), tmin.z);
    half tFar = min(min(tmax.x, tmax.y), tmax.z);
    return half2(tNear, tFar);
}

half2 IntersectAABB(half3 rayOrigin, half3 rayDir, half3 boxMin, half3 boxMax)
{
    half3 tMin = (boxMin - rayOrigin) / rayDir;
    half3 tMax = (boxMax - rayOrigin) / rayDir;
    half3 t1 = min(tMin, tMax);
    half3 t2 = max(tMin, tMax);
    half tNear = max(max(t1.x, t1.y), t1.z);
    half tFar = min(min(t2.x, t2.y), t2.z);
    half distToBox = max(0, tNear);
    half distInsideBox = max(0, tFar - distToBox);
    return half2(distToBox, distInsideBox);
}

// ------------------------------------------------------

half3 CalculateNormal(half3 positionOS)
{
    half3 dx = ddx(positionOS);
    half3 dy = ddy(positionOS);
    half3 normal = normalize(cross(dx, dy));
    return normal;
}

// ------------------------------------------------------
// ----------------- VOLUME RENDERING -------------------
void VolumeRenderOrigin_float(float3 positionOS, float3 cameraPositionWS, float rawDepth, float rayMarchingSteps, float noise,
UnityTexture3D dataTex, UnityTexture3D gradientTexture, UnityTexture2D transferFunctionTexture,
float originWindowCenter, float originWindowWidth, float windowCenter, float windowWidth,
float minVal, float maxVal,
float lightingGradientThresholdStart, float lightingGradientThresholdEnd,
out float4 color, out float outDepth)
{
    float3 rd = GetViewRayDir(positionOS, cameraPositionWS);
    float3 ro = positionOS + float3(0.5, 0.5, 0.5);
    // Find intersections with axis aligned boundinng box (the volume)
    float2 inters = IntersectAABBOrigin(ro, rd, float3(0, 0, 0), float3(1, 1, 1));
    // Check if camera is inside AABB
    float3 farPos = ro + rd * inters.y - float3(0.5, 0.5, 0.5);
    float4 clipPos = TransformObjectToHClip(farPos);
    inters += min(clipPos.w, 0.0);
    float3 rend = ro + rd * inters.y;
    rd = -rd;
    float3 temp = ro;
    ro = rend;
    rend = temp;

    float stepSize = 1.732 / rayMarchingSteps;
    uint numSteps = (uint)clamp(abs(inters.x - inters.y) / stepSize, 1, rayMarchingSteps);
    float numStepsRecip = 1.0 / numSteps;

    ro += JITTER_FACTOR * rd * stepSize * noise;

    color = 0;

    float tDepth = numStepsRecip * (numSteps - 1);
    UNITY_LOOP
    for(int iStep = 0; iStep < numSteps; iStep ++)
    {
        const float t = iStep * numStepsRecip;
        const float3 currPos = lerp(ro, rend, t);
        float density = tex3D(dataTex, float4(currPos, 0));

        float hounsfield = density * originWindowCenter + originWindowCenter - originWindowWidth/2;
        float lower_bound = windowCenter - (windowWidth / 2);
        float upper_bound = windowCenter + (windowWidth / 2);
        if (hounsfield < lower_bound) density = lower_bound;
        else if (hounsfield > upper_bound) density = upper_bound;
        density = (hounsfield - lower_bound) / windowWidth;

        if(density < minVal || density > maxVal) continue;
        float4 src = tex2Dlod(transferFunctionTexture, float4(density, 0.0f, 0.0f, 0.0f));
        if(src.a == 0.0) continue;

        float3 gradient = tex3D(gradientTexture, float4(currPos, 0)).rgb;
        float gradMag = length(gradient);
        float gradMagNorm = gradMag / 1.75f;
        float factor = smoothstep(lightingGradientThresholdStart, lightingGradientThresholdEnd, gradMag);
        float3 shaded = CalculateLighting(src.rgb, gradient / gradMag, GetLightDirection(-rd), -rd, 0.3f);
        src.rgb = lerp(src.rgb, shaded, factor);
        src.a = 1.0f - pow(1.0 - src.a, 1.0);
        src.rgb *= src.a;
        color = (1.0f - color.a) * src + color;
        if(color.a > 0.15 && t < tDepth) tDepth = t;

        if(color.a > OPACITY_THRESHOLD) break;
    }

    tDepth += (step(color.a, 0.0) * 1000.0); // Write large depth if no hit
    const float3 depthPos = lerp(ro, rend, tDepth) - float3(0.5f, 0.5f, 0.5f);
    clipPos = TransformObjectToHClip(depthPos);
    #if defined(SHADER_API_GLCORE) || defined(SHADER_API_OPENGL) || defined(SHADER_API_GLES) || defined(SHADER_API_GLES3)
        outDepth = (clipPos.z / clipPos.w) * 0.5 + 0.5;
    #else
        outDepth = clipPos.z / clipPos.w;
    #endif
}

void VolumeRender_float(float3 positionOS, float3 cameraPositionWS, float rawDepth, float rayMarchingSteps,
UnityTexture3D dataTex, UnityTexture3D gradientTexture, UnityTexture2D transferFunctionTexture,
float originWindowCenter, float originWindowWidth, float windowCenter, float windowWidth,
float minVal, float maxVal,
float lightingGradientThresholdStart, float lightingGradientThresholdEnd,
out float4 color)
{
    float3 ro = positionOS + 0.5;

    float3 viewDirWS = 0;
    if(unity_OrthoParams.w == 0)
    {
        // Perspective
        viewDirWS = normalize(TransformWorldToObject(cameraPositionWS - positionOS));
    }
    else
    {
        // Orthographic
        float3 camfwd = mul((float3x3)unity_CameraToWorld, float3(0,0,-1));
        float4 camfwdobjspace = mul(unity_WorldToObject, camfwd);
        viewDirWS = normalize(camfwdobjspace);
    }

    float2 rayToContainer = IntersectAABB(ro, viewDirWS, float3(0, 0, 0), float3(1, 1, 1));
    float distToBox = rayToContainer.x;
    float distInsideBox = rayToContainer.y;
    float depthEyeLinear = LinearEyeDepth(rawDepth, _ZBufferParams);
    float distLimit = min(depthEyeLinear - distToBox, distInsideBox);
    float distTravelled = 0;

    float3 entry = ro + viewDirWS * distToBox;

    color = 0;

    float stepSize = length(float3(1, 1, 1)) / rayMarchingSteps;
    uint numSteps = (uint)clamp(distInsideBox / stepSize, 1, rayMarchingSteps);

    UNITY_LOOP
    for (uint j = 0; j < numSteps; j++)
    {
        if(distLimit > distTravelled)
        { 
            float3 rayMarchPos = entry + (viewDirWS * distTravelled);

            float4 samplePos = float4(rayMarchPos, 0);
            float density = tex3D(dataTex, samplePos).r;

            float hounsfield = density * originWindowCenter + originWindowCenter - originWindowWidth/2;
            float lower_bound = windowCenter - (windowWidth / 2);
            float upper_bound = windowCenter + (windowWidth / 2);
            if (hounsfield < lower_bound) density = lower_bound;
            else if (hounsfield > upper_bound) density = upper_bound;
            density = (hounsfield - lower_bound) / windowWidth;

            if (density < minVal || density > maxVal) 
            {
                distTravelled += stepSize;
                continue;
            }

            float4 src = tex2Dlod(transferFunctionTexture, float4(density, 0.0f, 0.0f, 0.0f));
            if(src.a == 0.0)
            {
                distTravelled += stepSize;
                continue;
            }

            float3 gradient = tex3D(gradientTexture, samplePos).rgb;
            float gradientMagnitude = length(gradient);
            float gradientMagnitudeNormalized = gradientMagnitude / 1.75f;
            float factor = smoothstep(lightingGradientThresholdStart, lightingGradientThresholdEnd, gradientMagnitude);
            float3 lightDir = GetLightDirection(-viewDirWS);
            float3 shaded = CalculateLighting(src.rgb, gradient / gradientMagnitude, lightDir, viewDirWS, 0.3f);

            src.rgb = lerp(src.rgb, shaded, factor);

            src.a = 1.0f - pow(1.0 - src.a, 1.0);
            src.rgb *= src.a;
            color = (1.0f - color.a) * src + color;

            if (color.a > OPACITY_THRESHOLD) {
                break;
            }
        }
        distTravelled += stepSize;
    }
}


#endif