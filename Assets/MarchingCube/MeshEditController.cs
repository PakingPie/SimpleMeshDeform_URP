// MeshEditController.cs
using UnityEngine;
using System;
using System.Collections.Generic;

/// <summary>
/// Main controller for mesh editing operations.
/// Provides high-level API for cutting and deformation.
/// </summary>
public class MeshEditController : MonoBehaviour
{
    [Header("Compute Shaders")]
    [SerializeField] private ComputeShader _sdfGeneratorShader;
    [SerializeField] private ComputeShader _sdfOperationsShader;
    [SerializeField] private ComputeShader _marchingCubesShader;

    [Header("Settings")]
    [SerializeField] private Vector3Int _sdfResolution = new Vector3Int(64, 64, 64);
    [SerializeField] private float _boundsPadding = 0.1f;
    [SerializeField] private bool _useAsyncMeshGeneration = true;

    [Header("Debug")]
    [SerializeField] private bool _showSDFBounds = false;

    // Core components
    private SDFVolume _sdfVolume;
    private MarchingCubesGPU _marchingCubes;
    private SDFOperations _sdfOperations;
    private UVTransfer _uvTransfer;

    // State
    private Mesh _originalMesh;
    private Mesh _currentMesh;
    private MeshFilter _meshFilter;
    private MeshCollider _meshCollider;
    private bool _isInitialized;
    private bool _isDirty;

    // Add these fields at the class level (near other private fields)
    private MeshBVH _meshBVH;
    private LinearBVH _linearBVH;


    // Events
    public event Action<Mesh> OnMeshUpdated;
    public event Action OnOperationComplete;

    // Public properties
    public bool IsInitialized => _isInitialized;
    public Mesh CurrentMesh => _currentMesh;
    public SDFVolume SDFVolume => _sdfVolume;
    public Bounds MeshBounds => _sdfVolume?.WorldBounds ?? default;

    private void Awake()
    {
        _meshFilter = GetComponent<MeshFilter>();
        _meshCollider = GetComponent<MeshCollider>();
    }

    private void OnDestroy()
    {
        Cleanup();
    }

    /// <summary>
    /// Initialize the editor with a mesh.
    /// </summary>
    public void Initialize(Mesh mesh = null)
    {
        if (mesh == null && _meshFilter != null)
        {
            mesh = _meshFilter.sharedMesh;
        }

        if (mesh == null)
        {
            Debug.LogError("MeshEditController: No mesh to initialize with.");
            return;
        }

        Cleanup();

        // Store original mesh
        _originalMesh = mesh;
        _currentMesh = Instantiate(mesh);
        _currentMesh.name = mesh.name + "_Editable";

        // Calculate bounds with padding
        Bounds bounds = mesh.bounds;
        Vector3 padding = Vector3.one * _boundsPadding;
        bounds.Expand(padding * 2);

        // Transform to world space
        if (transform != null)
        {
            bounds = TransformBounds(bounds, transform.localToWorldMatrix);
        }

        // Create SDF volume
        _sdfVolume = new SDFVolume(_sdfResolution, bounds, _sdfGeneratorShader, _sdfOperationsShader);

        // Create marching cubes extractor
        _marchingCubes = new MarchingCubesGPU(_marchingCubesShader);

        // Create operations handler
        _sdfOperations = new SDFOperations(_sdfOperationsShader);

        // Create UV transfer
        _uvTransfer = new UVTransfer(_originalMesh);

        // Generate initial SDF from mesh
        GenerateSDFFromMesh();

        _isInitialized = true;

        // Apply to mesh filter
        if (_meshFilter != null)
        {
            _meshFilter.sharedMesh = _currentMesh;
        }
    }

    /// <summary>
    /// Cut the mesh using a primitive shape.
    /// </summary>
    public void Cut(SDFOperations.ToolType toolType, Vector3 position,
                   Vector3 rotation, Vector3 scale, float blendRadius = 0f)
    {
        if (!_isInitialized)
        {
            Debug.LogWarning("MeshEditController not initialized.");
            return;
        }

        // Transform to local space if needed
        if (transform != null)
        {
            position = transform.InverseTransformPoint(position);
            // Note: rotation and scale may need adjustment based on your needs
        }

        _sdfOperations.ApplyCSG(_sdfVolume, SDFOperations.CSGOperation.Subtract,
                                toolType, position, rotation, scale, blendRadius);

        _isDirty = true;
        RegenerateMesh();
    }

    /// <summary>
    /// Cut the mesh using another mesh.
    /// </summary>
    public void CutWithMesh(Mesh cuttingMesh, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        if (!_isInitialized)
        {
            Debug.LogWarning("MeshEditController not initialized.");
            return;
        }

        // Create temporary SDF for cutting mesh
        Bounds cutBounds = TransformBounds(cuttingMesh.bounds,
            Matrix4x4.TRS(position, rotation, scale));

        SDFVolume cutVolume = new SDFVolume(_sdfResolution / 2, cutBounds,
            _sdfGeneratorShader, _sdfOperationsShader);

        // Generate SDF from cutting mesh
        // This requires BVH generation for the cutting mesh
        // For now, use primitive approximation
        Debug.LogWarning("Mesh cutting not fully implemented. Using bounding box.");

        // Apply CSG
        _sdfOperations.ApplyCSGMesh(_sdfVolume, SDFOperations.CSGOperation.Subtract, cutVolume);

        cutVolume.Dispose();

        _isDirty = true;
        RegenerateMesh();
    }

    /// <summary>
    /// Apply push/pull deformation.
    /// </summary>
    public void Deform(SDFOperations.DeformationType type, Vector3 position,
                       Vector3 direction, float radius, float strength, float falloff = 2f)
    {
        if (!_isInitialized)
        {
            Debug.LogWarning("MeshEditController not initialized.");
            return;
        }

        // Transform to local space
        if (transform != null)
        {
            position = transform.InverseTransformPoint(position);
            direction = transform.InverseTransformDirection(direction);
        }

        _sdfOperations.ApplyDeformation(_sdfVolume, type, position, direction,
                                         radius, strength, falloff);

        _isDirty = true;
    }

    /// <summary>
    /// Apply deformation along a stroke.
    /// </summary>
    public void DeformStroke(SDFOperations.DeformationType type, Vector3[] worldPoints,
                             Vector3 direction, float radius, float strength, float falloff = 2f)
    {
        if (!_isInitialized)
        {
            Debug.LogWarning("MeshEditController not initialized.");
            return;
        }

        // Transform points to local space
        Vector3[] localPoints = new Vector3[worldPoints.Length];
        for (int i = 0; i < worldPoints.Length; i++)
        {
            localPoints[i] = transform != null
                ? transform.InverseTransformPoint(worldPoints[i])
                : worldPoints[i];
        }

        Vector3 localDir = transform != null
            ? transform.InverseTransformDirection(direction)
            : direction;

        _sdfOperations.ApplyStroke(_sdfVolume, type, localPoints, localDir,
                                   radius, strength, falloff);

        _isDirty = true;
    }

    /// <summary>
    /// Commit deformation changes and regenerate mesh.
    /// </summary>
    public void CommitDeformation()
    {
        if (_isDirty)
        {
            RegenerateMesh();
            _isDirty = false;
        }
    }

    /// <summary>
    /// Regenerate mesh from current SDF state.
    /// </summary>
    public void RegenerateMesh()
    {
        if (!_isInitialized) return;

        if (_useAsyncMeshGeneration)
        {
            _marchingCubes.ExtractMeshAsync(_sdfVolume, 0f, OnMeshExtracted);
        }
        else
        {
            Mesh newMesh = _marchingCubes.ExtractMesh(_sdfVolume, 0f);
            OnMeshExtracted(newMesh);
        }
    }

    private void OnMeshExtracted(Mesh mesh)
    {
        if (mesh == null)
        {
            Debug.LogWarning("Mesh extraction produced no geometry.");
            return;
        }

        // Transfer UVs from original mesh
        _uvTransfer.TransferUVs(mesh);

        // Update current mesh
        if (_currentMesh != null)
        {
            DestroyImmediate(_currentMesh);
        }

        _currentMesh = mesh;
        _currentMesh.name = _originalMesh.name + "_Edited";

        // Update mesh filter
        if (_meshFilter != null)
        {
            _meshFilter.sharedMesh = _currentMesh;
        }

        // Update mesh collider
        if (_meshCollider != null)
        {
            _meshCollider.sharedMesh = _currentMesh;
        }

        OnMeshUpdated?.Invoke(_currentMesh);
        OnOperationComplete?.Invoke();
    }

    /// <summary>
    /// Reset to original mesh.
    /// </summary>
    public void Reset()
    {
        if (_originalMesh != null)
        {
            Initialize(_originalMesh);
        }
    }

    /// <summary>
    /// Get the final edited mesh.
    /// </summary>
    public Mesh GetFinalMesh()
    {
        if (_isDirty)
        {
            CommitDeformation();
        }

        Mesh finalMesh = Instantiate(_currentMesh);
        finalMesh.name = _originalMesh.name + "_Final";
        return finalMesh;
    }

    /// <summary>
    /// Generate SDF from the original mesh using BVH acceleration.
    /// </summary>
    private void GenerateSDFFromMesh()
    {
        if (_originalMesh == null || _sdfVolume == null)
        {
            Debug.LogError("Cannot generate SDF: missing mesh or volume.");
            return;
        }

        // Build BVH from mesh
        _meshBVH = new MeshBVH();
        _meshBVH.Build(_originalMesh, transform);

        // Linearize BVH for GPU traversal
        _linearBVH = new LinearBVH();
        _linearBVH.BuildFromBVH(_meshBVH);

        if (_linearBVH.NodeCount == 0)
        {
            Debug.LogWarning("BVH has no nodes. Mesh may be empty.");
            _sdfVolume.Initialize(1f);
            return;
        }

        // Create GPU buffers
        ComputeBuffer bvhNodeBuffer = null;
        ComputeBuffer triangleBuffer = null;

        try
        {
            _linearBVH.CreateBuffers(out bvhNodeBuffer, out triangleBuffer);

            if (bvhNodeBuffer == null || triangleBuffer == null)
            {
                Debug.LogError("Failed to create BVH compute buffers.");
                _sdfVolume.Initialize(1f);
                return;
            }

            // Initialize volume first (sets all values to positive/outside)
            _sdfVolume.Initialize(1f);

            // Generate SDF from mesh using BVH-accelerated distance queries
            _sdfVolume.GenerateFromMesh(bvhNodeBuffer, triangleBuffer, _linearBVH.NodeCount, 1f,
                                        _originalMesh.bounds.min, _originalMesh.bounds.max);

            // Optional: Finalize/smooth the SDF
            _sdfVolume.Finalize();

            Debug.Log($"SDF generated from mesh: {_linearBVH.NodeCount} BVH nodes, " +
                      $"{_linearBVH.GPUTriangles.Length} triangles, " +
                      $"resolution {_sdfVolume.Resolution}");
        }
        finally
        {
            // Clean up GPU buffers
            bvhNodeBuffer?.Release();
            triangleBuffer?.Release();
        }
    }

    private Bounds TransformBounds(Bounds localBounds, Matrix4x4 matrix)
    {
        Vector3[] corners = new Vector3[8];
        Vector3 min = localBounds.min;
        Vector3 max = localBounds.max;

        corners[0] = matrix.MultiplyPoint3x4(new Vector3(min.x, min.y, min.z));
        corners[1] = matrix.MultiplyPoint3x4(new Vector3(max.x, min.y, min.z));
        corners[2] = matrix.MultiplyPoint3x4(new Vector3(min.x, max.y, min.z));
        corners[3] = matrix.MultiplyPoint3x4(new Vector3(max.x, max.y, min.z));
        corners[4] = matrix.MultiplyPoint3x4(new Vector3(min.x, min.y, max.z));
        corners[5] = matrix.MultiplyPoint3x4(new Vector3(max.x, min.y, max.z));
        corners[6] = matrix.MultiplyPoint3x4(new Vector3(min.x, max.y, max.z));
        corners[7] = matrix.MultiplyPoint3x4(new Vector3(max.x, max.y, max.z));

        Bounds result = new Bounds(corners[0], Vector3.zero);
        for (int i = 1; i < 8; i++)
        {
            result.Encapsulate(corners[i]);
        }

        return result;
    }

    private void Cleanup()
    {
        _sdfVolume?.Dispose();
        _marchingCubes?.Dispose();

        // Clear BVH references
        _meshBVH = null;
        _linearBVH = null;

        if (_currentMesh != null && _currentMesh != _originalMesh)
        {
            DestroyImmediate(_currentMesh);
        }

        _isInitialized = false;
    }

    private void OnDrawGizmosSelected()
    {
        if (_showSDFBounds && _sdfVolume != null)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(_sdfVolume.WorldBounds.center, _sdfVolume.WorldBounds.size);
        }
    }
}