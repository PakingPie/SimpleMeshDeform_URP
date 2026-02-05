// SDFOperations.cs
using UnityEngine;

/// <summary>
/// Provides CSG and deformation operations on SDF volumes.
/// </summary>
public class SDFOperations
{
    public enum CSGOperation
    {
        Union,
        Subtract,
        Intersect
    }

    public enum ToolType
    {
        Sphere = 0,
        Box = 1,
        Cylinder = 2
    }

    public enum DeformationType
    {
        Push,
        Pull,
        Smooth
    }

    private ComputeShader _operationsShader;
    
    private int _unionKernel;
    private int _subtractKernel;
    private int _intersectKernel;
    private int _pushKernel;
    private int _smoothKernel;

    public SDFOperations(ComputeShader operationsShader)
    {
        _operationsShader = operationsShader;
        CacheKernels();
    }

    private void CacheKernels()
    {
        _unionKernel = _operationsShader.FindKernel("CSGUnion");
        _subtractKernel = _operationsShader.FindKernel("CSGSubtract");
        _intersectKernel = _operationsShader.FindKernel("CSGIntersect");
        _pushKernel = _operationsShader.FindKernel("DeformPush");
        _smoothKernel = _operationsShader.FindKernel("DeformSmooth");
    }

    /// <summary>
    /// Apply CSG operation with a primitive tool.
    /// </summary>
    public void ApplyCSG(SDFVolume volume, CSGOperation operation, 
                         ToolType toolType, Vector3 position, 
                         Vector3 rotation, Vector3 scale, float blendRadius = 0f)
    {
        int kernel = operation switch
        {
            CSGOperation.Union => _unionKernel,
            CSGOperation.Subtract => _subtractKernel,
            CSGOperation.Intersect => _intersectKernel,
            _ => _subtractKernel
        };

        SetVolumeParameters(volume, kernel);
        SetToolParameters(toolType, position, rotation, scale, blendRadius);
        _operationsShader.SetInt("_UseToolSDF", 0);
        
        _operationsShader.SetTexture(kernel, "_SDFVolume", volume.VolumeTexture);
        
        DispatchCompute(volume.Resolution, kernel);
    }

    /// <summary>
    /// Apply CSG operation with a mesh (converted to SDF first).
    /// </summary>
    public void ApplyCSGMesh(SDFVolume volume, CSGOperation operation,
                             SDFVolume toolVolume, float blendRadius = 0f)
    {
        int kernel = operation switch
        {
            CSGOperation.Union => _unionKernel,
            CSGOperation.Subtract => _subtractKernel,
            CSGOperation.Intersect => _intersectKernel,
            _ => _subtractKernel
        };

        SetVolumeParameters(volume, kernel);
        _operationsShader.SetFloat("_ToolBlend", blendRadius);
        _operationsShader.SetInt("_UseToolSDF", 1);
        _operationsShader.SetVector("_ToolVolumeMin", toolVolume.WorldBounds.min);
        _operationsShader.SetVector("_ToolVolumeMax", toolVolume.WorldBounds.max);
        _operationsShader.SetInts("_ToolVolumeResolution", 
            toolVolume.Resolution.x, toolVolume.Resolution.y, toolVolume.Resolution.z);
        _operationsShader.SetTexture(kernel, "_SDFVolume", volume.VolumeTexture);
        _operationsShader.SetTexture(kernel, "_ToolSDF", toolVolume.VolumeTexture);
        
        DispatchCompute(volume.Resolution, kernel);
    }

    /// <summary>
    /// Apply deformation at a point.
    /// </summary>
    public void ApplyDeformation(SDFVolume volume, DeformationType type,
                                  Vector3 position, Vector3 direction,
                                  float radius, float strength, float falloff = 2f)
    {
        int kernel = type switch
        {
            DeformationType.Push => _pushKernel,
            DeformationType.Pull => _pushKernel, // Same kernel, negative strength
            DeformationType.Smooth => _smoothKernel,
            _ => _pushKernel
        };

        // Pull uses negative strength
        if (type == DeformationType.Pull)
        {
            strength = -strength;
        }

        SetVolumeParameters(volume, kernel);
        
        _operationsShader.SetVector("_BrushPosition", position);
        _operationsShader.SetVector("_BrushDirection", direction.normalized);
        _operationsShader.SetFloat("_BrushRadius", radius);
        _operationsShader.SetFloat("_BrushStrength", strength);
        _operationsShader.SetFloat("_BrushFalloff", falloff);
        
        _operationsShader.SetTexture(kernel, "_SDFVolume", volume.VolumeTexture);
        
        // For smooth operation, we need to read from a copy
        if (type == DeformationType.Smooth)
        {
            _operationsShader.SetTexture(kernel, "_SDFVolumeRead", volume.VolumeTexture);
        }
        
        DispatchCompute(volume.Resolution, kernel);
    }

    /// <summary>
    /// Apply continuous deformation along a stroke path.
    /// </summary>
    public void ApplyStroke(SDFVolume volume, DeformationType type,
                            Vector3[] strokePoints, Vector3 direction,
                            float radius, float strength, float falloff = 2f)
    {
        float pointSpacing = radius * 0.5f; // Overlap brushes for smooth stroke
        
        for (int i = 0; i < strokePoints.Length; i++)
        {
            // Reduce strength at endpoints for smoother strokes
            float pointStrength = strength;
            if (i == 0 || i == strokePoints.Length - 1)
            {
                pointStrength *= 0.5f;
            }
            
            ApplyDeformation(volume, type, strokePoints[i], direction, 
                           radius, pointStrength, falloff);
        }
    }

    private void SetVolumeParameters(SDFVolume volume, int kernel)
    {
        _operationsShader.SetVector("_VolumeMin", volume.WorldBounds.min);
        _operationsShader.SetVector("_VolumeMax", volume.WorldBounds.max);
        _operationsShader.SetInts("_VolumeResolution", 
            volume.Resolution.x, volume.Resolution.y, volume.Resolution.z);
    }

    private void SetToolParameters(ToolType toolType, Vector3 position, 
                                   Vector3 rotation, Vector3 scale, float blendRadius)
    {
        _operationsShader.SetVector("_ToolPosition", position);
        _operationsShader.SetVector("_ToolRotation", rotation * Mathf.Deg2Rad);
        _operationsShader.SetVector("_ToolScale", scale);
        _operationsShader.SetInt("_ToolType", (int)toolType);
        _operationsShader.SetFloat("_ToolBlend", blendRadius);
    }

    private void DispatchCompute(Vector3Int resolution, int kernel)
    {
        int threadGroupsX = Mathf.CeilToInt(resolution.x / 8f);
        int threadGroupsY = Mathf.CeilToInt(resolution.y / 8f);
        int threadGroupsZ = Mathf.CeilToInt(resolution.z / 8f);
        
        _operationsShader.Dispatch(kernel, 
            Mathf.Max(1, threadGroupsX), 
            Mathf.Max(1, threadGroupsY), 
            Mathf.Max(1, threadGroupsZ));
    }
}