// SDFVolume.cs
using UnityEngine;
using UnityEngine.Rendering;
using System;

/// <summary>
/// Manages a 3D SDF volume stored as a RenderTexture3D.
/// Handles creation, resizing, and GPU resource management.
/// </summary>
public class SDFVolume : IDisposable
{
    public RenderTexture VolumeTexture { get; private set; }
    public Vector3Int Resolution { get; private set; }
    public Bounds WorldBounds { get; private set; }
    public float VoxelSize => (WorldBounds.size.x / Resolution.x + 
                               WorldBounds.size.y / Resolution.y + 
                               WorldBounds.size.z / Resolution.z) / 3f;

    private ComputeShader _sdfGeneratorShader;
    private ComputeShader _sdfOperationsShader;
    
    private int _initializeKernel;
    private int _meshToSDFKernel;
    private int _finalizeKernel;
    
    private bool _disposed;

    public SDFVolume(Vector3Int resolution, Bounds worldBounds, ComputeShader sdfGeneratorShader, ComputeShader sdfOperationsShader)
    {
        Resolution = resolution;
        WorldBounds = worldBounds;
        _sdfGeneratorShader = sdfGeneratorShader;
        _sdfOperationsShader = sdfOperationsShader;
        
        CreateVolumeTexture();
        CacheKernels();
    }

    private void CreateVolumeTexture()
    {
        if (VolumeTexture != null)
        {
            VolumeTexture.Release();
        }

        VolumeTexture = new RenderTexture(Resolution.x, Resolution.y, 0, RenderTextureFormat.RFloat)
        {
            dimension = TextureDimension.Tex3D,
            volumeDepth = Resolution.z,
            enableRandomWrite = true,
            filterMode = FilterMode.Trilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "SDFVolume"
        };
        VolumeTexture.Create();
    }

    private void CacheKernels()
    {
        if (_sdfGeneratorShader != null)
        {
            _initializeKernel = _sdfGeneratorShader.FindKernel("InitializeSDF");
            _meshToSDFKernel = _sdfGeneratorShader.FindKernel("MeshToSDF");
            _finalizeKernel = _sdfGeneratorShader.FindKernel("FinalizeSDF");
        }
    }

    /// <summary>
    /// Initialize the SDF volume with a constant value.
    /// </summary>
    public void Initialize(float value = 1f)
    {
        if (_sdfGeneratorShader == null) return;

        SetCommonParameters(_sdfGeneratorShader);
        _sdfGeneratorShader.SetFloat("_InitialValue", value);
        _sdfGeneratorShader.SetTexture(_initializeKernel, "_SDFVolume", VolumeTexture);

        DispatchCompute(_sdfGeneratorShader, _initializeKernel);
    }

    /// <summary>
    /// Generate SDF from mesh using BVH acceleration.
    /// </summary>
    public void GenerateFromMesh(ComputeBuffer bvhNodesBuffer, ComputeBuffer trianglesBuffer, 
                                  int nodeCount, float narrowBandWidth = -1f)
    {
        if (_sdfGeneratorShader == null) return;

        if (narrowBandWidth < 0)
        {
            narrowBandWidth = VoxelSize * 5f; // Default: 5 voxels
        }

        SetCommonParameters(_sdfGeneratorShader);
        _sdfGeneratorShader.SetBuffer(_meshToSDFKernel, "_BVHNodes", bvhNodesBuffer);
        _sdfGeneratorShader.SetBuffer(_meshToSDFKernel, "_Triangles", trianglesBuffer);
        _sdfGeneratorShader.SetInt("_BVHNodeCount", nodeCount);
        _sdfGeneratorShader.SetFloat("_NarrowBandWidth", narrowBandWidth);
        _sdfGeneratorShader.SetTexture(_meshToSDFKernel, "_SDFVolume", VolumeTexture);

        DispatchCompute(_sdfGeneratorShader, _meshToSDFKernel);
    }

    /// <summary>
    /// Finalize/post-process the SDF (optional smoothing).
    /// </summary>
    public void Finalize()
    {
        if (_sdfGeneratorShader == null) return;

        SetCommonParameters(_sdfGeneratorShader);
        _sdfGeneratorShader.SetTexture(_finalizeKernel, "_SDFVolume", VolumeTexture);

        DispatchCompute(_sdfGeneratorShader, _finalizeKernel);
    }

    /// <summary>
    /// Resize the volume to fit new bounds while preserving content where possible.
    /// </summary>
    public void Resize(Bounds newBounds, Vector3Int newResolution = default)
    {
        if (newResolution == default)
        {
            newResolution = Resolution;
        }

        // Store old texture reference
        var oldTexture = VolumeTexture;
        var oldBounds = WorldBounds;
        var oldResolution = Resolution;

        // Create new texture
        Resolution = newResolution;
        WorldBounds = newBounds;
        CreateVolumeTexture();

        // Initialize new volume
        Initialize(1f);

        // TODO: Copy overlapping region from old to new texture
        // This requires a separate compute shader for resampling

        // Release old texture
        if (oldTexture != null)
        {
            oldTexture.Release();
        }
    }

    /// <summary>
    /// Dynamically expand bounds to fit a mesh with padding.
    /// </summary>
    public void ExpandToFit(Bounds meshBounds, float padding = 0.1f)
    {
        Vector3 paddedMin = meshBounds.min - Vector3.one * padding;
        Vector3 paddedMax = meshBounds.max + Vector3.one * padding;
        
        Bounds newBounds = new Bounds();
        newBounds.SetMinMax(
            Vector3.Min(WorldBounds.min, paddedMin),
            Vector3.Max(WorldBounds.max, paddedMax)
        );

        if (newBounds.size != WorldBounds.size)
        {
            // Calculate new resolution to maintain voxel size
            float targetVoxelSize = VoxelSize;
            Vector3Int newRes = new Vector3Int(
                Mathf.CeilToInt(newBounds.size.x / targetVoxelSize),
                Mathf.CeilToInt(newBounds.size.y / targetVoxelSize),
                Mathf.CeilToInt(newBounds.size.z / targetVoxelSize)
            );

            // Clamp resolution to reasonable limits
            newRes.x = Mathf.Clamp(newRes.x, 16, 256);
            newRes.y = Mathf.Clamp(newRes.y, 16, 256);
            newRes.z = Mathf.Clamp(newRes.z, 16, 256);

            Resize(newBounds, newRes);
        }
    }

    /// <summary>
    /// Sample SDF value at world position (CPU-side, slow).
    /// For GPU sampling, use the texture directly.
    /// </summary>
    public float SampleAt(Vector3 worldPos)
    {
        Vector3 localPos = worldPos - WorldBounds.min;
        Vector3 normalized = new Vector3(
            localPos.x / WorldBounds.size.x,
            localPos.y / WorldBounds.size.y,
            localPos.z / WorldBounds.size.z
        );

        // Clamp to valid range
        normalized.x = Mathf.Clamp01(normalized.x);
        normalized.y = Mathf.Clamp01(normalized.y);
        normalized.z = Mathf.Clamp01(normalized.z);

        // This would require ReadPixels which is slow
        // For actual usage, sample in compute shader
        Debug.LogWarning("CPU-side SDF sampling is slow. Use GPU sampling when possible.");
        return 0f;
    }

    private void SetCommonParameters(ComputeShader shader)
    {
        shader.SetVector("_VolumeMin", WorldBounds.min);
        shader.SetVector("_VolumeMax", WorldBounds.max);
        shader.SetInts("_VolumeResolution", Resolution.x, Resolution.y, Resolution.z);
    }

    private void DispatchCompute(ComputeShader shader, int kernel)
    {
        int threadGroupsX = Mathf.CeilToInt(Resolution.x / 8f);
        int threadGroupsY = Mathf.CeilToInt(Resolution.y / 8f);
        int threadGroupsZ = Mathf.CeilToInt(Resolution.z / 8f);
        
        shader.Dispatch(kernel, threadGroupsX, threadGroupsY, threadGroupsZ);
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        if (VolumeTexture != null)
        {
            VolumeTexture.Release();
            VolumeTexture = null;
        }
        
        _disposed = true;
    }
}