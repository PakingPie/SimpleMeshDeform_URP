using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class SDFBooleanVolumeInteractor : MonoBehaviour
{
    [Header("Target Volume")]
    public SDFVolumeTest TargetVolume;
    public ComputeShader SDFGeneratorShader;
    public ComputeShader SDFOperationsShader;
    public ComputeShader BVHSortShader;

    [Header("Tool")]
    public GameObject ToolObject;

    [Header("CSG Operation")]
    public SDFOperations.CSGOperation Operation = SDFOperations.CSGOperation.Subtract;
    [Min(0f)]
    public float ToolBlend = 0f;

    [Header("Realtime")]
    public bool RealTimeUpdate = true;
    public bool AllowEditModeUpdate = false;
    [Min(0f)]
    public float MinUpdateInterval = 0.05f;
    public bool UseGpuSort = false;
    [Min(1)]
    public int MaxTrianglesForGpuSort = 50000;
    public bool ResetToBaseBeforeOperation = false;

    [Header("Tool Volume")]
    [Tooltip("Max resolution per axis for the tool SDF volume")]
    [Min(8)]
    public int MaxToolResolution = 128;

    [Header("Debug")]
    public bool ShowDebugInfo = false;

    public RenderTexture ToolSDFTexture => _toolVolume?.VolumeTexture;

    private SDFVolume _toolVolume;
    private RenderTexture _baseSdfTex;
    private MeshBVH _meshBVH = new MeshBVH();
    private LinearBVH _linearBVH = new LinearBVH();
    private readonly GpuBVHSorter _gpuSorter = new GpuBVHSorter();

    private Mesh _lastToolMesh;
    private int _lastToolVertexCount;
    private int _lastToolIndexCount; // CHANGED: was _lastToolTriangleCount
    private Matrix4x4 _lastToolTransform;

    private Mesh _lastSourceMesh;
    private int _lastSourceVertexCount;
    private int _lastSourceIndexCount; // CHANGED: was _lastSourceTriangleCount
    private Matrix4x4 _lastSourceTransform;

    private Vector3Int _lastResolution;
    private Bounds _lastBounds;

    private float _nextUpdateTime;
    private bool _kernelsCached;
    private int _unionKernel;
    private int _subtractKernel;
    private int _intersectKernel;

    private SDFOperations.CSGOperation _lastOperation;
    private float _lastBlend;

    // --- Helper: non-allocating index count ---
    private static int GetMeshIndexCount(Mesh mesh)
    {
        int total = 0;
        for (int i = 0; i < mesh.subMeshCount; i++)
            total += (int)mesh.GetIndexCount(i);
        return total;
    }

    private void OnEnable()
    {
        CacheKernels();
        _lastOperation = Operation;
        _lastBlend = ToolBlend;
    }

    private void OnDisable()
    {
        ReleaseResources();
    }

    private void OnDestroy()
    {
        ReleaseResources();
    }

    private void CacheKernels()
    {
        if (SDFOperationsShader == null)
            return;

        _unionKernel = SDFOperationsShader.FindKernel("CSGUnion");
        _subtractKernel = SDFOperationsShader.FindKernel("CSGSubtract");
        _intersectKernel = SDFOperationsShader.FindKernel("CSGIntersect");
        _kernelsCached = true;
    }

    private void LateUpdate()
    {
        if (!RealTimeUpdate)
            return;

        if (!Application.isPlaying && !AllowEditModeUpdate)
            return;

        if (TargetVolume == null || ToolObject == null || SDFGeneratorShader == null || SDFOperationsShader == null)
            return;

        if (!_kernelsCached)
            CacheKernels();

        float currentTime = Application.isPlaying ? Time.time : (float)EditorApplication.timeSinceStartup;
        if (MinUpdateInterval > 0f && currentTime < _nextUpdateTime)
            return;

        EnsureTargetInitialized();

        if (TargetVolume.SDFTexture == null)
            return;

        EnsureBaseTexture(); // CHANGED: was EnsureVolumes — no longer touches tool volume

        bool targetChanged = HasTargetChanged();
        if (targetChanged)
        {
            TargetVolume.Run();
            CopyTargetToBase();
        }

        bool toolChanged = HasToolChanged();
        if (toolChanged)
        {
            EnsureToolVolume(); // ADDED: creates/resizes tool volume with tight bounds
            GenerateToolSDF();
        }

        bool operationChanged = Operation != _lastOperation || !Mathf.Approximately(ToolBlend, _lastBlend);
        if (targetChanged || toolChanged || operationChanged)
        {
            ApplyCSG();
            _lastOperation = Operation;
            _lastBlend = ToolBlend;
            _nextUpdateTime = currentTime + MinUpdateInterval;
        }
    }

    private void EnsureTargetInitialized()
    {
        if (TargetVolume == null)
            return;

        if (TargetVolume.SDFTexture == null)
        {
            TargetVolume.Run();
            CopyTargetToBase();
            CacheTargetState();
        }
    }

    /// <summary>
    /// Ensures the base snapshot texture exists and matches the target.
    /// Does NOT manage the tool volume (that is handled by EnsureToolVolume).
    /// </summary>
    private void EnsureBaseTexture()
    {
        Vector3Int resolution = TargetVolume.Resolution;
        Bounds bounds = TargetVolume.WorldBounds;

        bool boundsChanged = !BoundsApproximatelyEqual(bounds, _lastBounds);
        bool resolutionChanged = resolution != _lastResolution;

        if (_baseSdfTex == null || boundsChanged || resolutionChanged)
        {
            CreateBaseTexture(resolution);
            CopyTargetToBase();
        }

        _lastBounds = bounds;
        _lastResolution = resolution;
    }

    /// <summary>
    /// Creates or resizes the tool SDF volume with tight bounds around the tool mesh.
    /// Resolution is chosen to match the target's voxel density, clamped to MaxToolResolution.
    /// </summary>
    private void EnsureToolVolume()
    {
        if (ToolObject == null || TargetVolume == null) return;

        MeshFilter mf = ToolObject.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return;

        // Compute world-space tool bounds
        Bounds toolWorldBounds = TransformBounds(mf.sharedMesh.bounds, ToolObject.transform.localToWorldMatrix);

        // Add padding so SDF at volume edges is clearly positive (outside)
        float voxelSize = TargetVolume.VoxelSize;
        if (voxelSize < 1e-6f) voxelSize = 0.01f;
        float padding = Mathf.Max(voxelSize * 4f, toolWorldBounds.size.magnitude * 0.05f);
        toolWorldBounds.Expand(padding * 2f);

        // Resolution to match target voxel density
        Vector3Int toolRes = new Vector3Int(
            Mathf.Clamp(Mathf.CeilToInt(toolWorldBounds.size.x / voxelSize), 8, MaxToolResolution),
            Mathf.Clamp(Mathf.CeilToInt(toolWorldBounds.size.y / voxelSize), 8, MaxToolResolution),
            Mathf.Clamp(Mathf.CeilToInt(toolWorldBounds.size.z / voxelSize), 8, MaxToolResolution)
        );

        if (_toolVolume == null)
        {
            _toolVolume = new SDFVolume(toolRes, toolWorldBounds, SDFGeneratorShader, SDFOperationsShader);
        }
        else
        {
            _toolVolume.SetBoundsAndResize(toolWorldBounds, toolRes);
        }
    }

    private void CreateBaseTexture(Vector3Int resolution)
    {
        if (_baseSdfTex != null)
        {
            _baseSdfTex.Release();
        }

        _baseSdfTex = new RenderTexture(resolution.x, resolution.y, 0, RenderTextureFormat.RFloat)
        {
            dimension = TextureDimension.Tex3D,
            volumeDepth = resolution.z,
            enableRandomWrite = true,
            filterMode = FilterMode.Trilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "SDFBaseCopy"
        };
        _baseSdfTex.Create();
    }

    private void CopyTargetToBase()
    {
        if (_baseSdfTex == null || TargetVolume == null || TargetVolume.SDFTexture == null)
            return;

        Graphics.CopyTexture(TargetVolume.SDFTexture, _baseSdfTex);
    }

    private void CopyBaseToTarget()
    {
        if (_baseSdfTex == null || TargetVolume == null || TargetVolume.SDFTexture == null)
            return;

        Graphics.CopyTexture(_baseSdfTex, TargetVolume.SDFTexture);
    }

    private bool HasTargetChanged()
    {
        if (TargetVolume == null || TargetVolume.SourceMeshObject == null)
            return false;

        MeshFilter meshFilter = TargetVolume.SourceMeshObject.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null)
            return false;

        Mesh mesh = meshFilter.sharedMesh;
        // CHANGED: non-allocating
        bool meshChanged = mesh != _lastSourceMesh ||
                           mesh.vertexCount != _lastSourceVertexCount ||
                           GetMeshIndexCount(mesh) != _lastSourceIndexCount;

        bool transformChanged = TargetVolume.SourceMeshObject.transform.localToWorldMatrix != _lastSourceTransform;

        if (!meshChanged && !transformChanged)
            return false;

        CacheTargetState();
        return true;
    }

    private void CacheTargetState()
    {
        if (TargetVolume == null || TargetVolume.SourceMeshObject == null)
            return;

        MeshFilter meshFilter = TargetVolume.SourceMeshObject.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null)
            return;

        _lastSourceMesh = meshFilter.sharedMesh;
        _lastSourceVertexCount = _lastSourceMesh.vertexCount;
        _lastSourceIndexCount = GetMeshIndexCount(_lastSourceMesh); // CHANGED: non-allocating
        _lastSourceTransform = TargetVolume.SourceMeshObject.transform.localToWorldMatrix;
    }

    private bool HasToolChanged()
    {
        if (ToolObject == null)
            return false;

        MeshFilter meshFilter = ToolObject.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null)
            return false;

        Mesh mesh = meshFilter.sharedMesh;
        // CHANGED: non-allocating
        bool meshChanged = mesh != _lastToolMesh ||
                           mesh.vertexCount != _lastToolVertexCount ||
                           GetMeshIndexCount(mesh) != _lastToolIndexCount;

        bool transformChanged = ToolObject.transform.localToWorldMatrix != _lastToolTransform;

        if (!meshChanged && !transformChanged)
            return false;

        CacheToolState();
        return true;
    }

    private void CacheToolState()
    {
        if (ToolObject == null)
            return;

        MeshFilter meshFilter = ToolObject.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null)
            return;

        _lastToolMesh = meshFilter.sharedMesh;
        _lastToolVertexCount = _lastToolMesh.vertexCount;
        _lastToolIndexCount = GetMeshIndexCount(_lastToolMesh); // CHANGED: non-allocating
        _lastToolTransform = ToolObject.transform.localToWorldMatrix;
    }

    private void GenerateToolSDF()
    {
        if (ToolObject == null || _toolVolume == null)
            return;

        MeshFilter meshFilter = ToolObject.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null)
            return;

        Mesh toolMesh = meshFilter.sharedMesh;
        int indexCount = GetMeshIndexCount(toolMesh); // CHANGED: non-allocating
        if (indexCount == 0)
            return;

        // --- CHANGED: cache mesh arrays once ---
        Vector3[] cachedVertices = toolMesh.vertices;
        int[] cachedTriangles = toolMesh.triangles;

        _toolVolume.Initialize(1f);

        int[] sortedTriangleIndices = null;
        int triangleCount = indexCount / 3;

        if (UseGpuSort && BVHSortShader != null && triangleCount <= MaxTrianglesForGpuSort)
        {
            Bounds meshBounds = TransformBounds(toolMesh.bounds, ToolObject.transform.localToWorldMatrix);
            Vector3[] worldVertices = new Vector3[cachedVertices.Length];
            for (int i = 0; i < cachedVertices.Length; i++)
            {
                worldVertices[i] = ToolObject.transform.TransformPoint(cachedVertices[i]);
            }

            if (!_gpuSorter.TrySortTriangles(BVHSortShader, worldVertices, cachedTriangles, meshBounds, out sortedTriangleIndices))
            {
                sortedTriangleIndices = null;
            }
        }

        // Build BVH using cached arrays
        if (sortedTriangleIndices != null && sortedTriangleIndices.Length > 0)
        {
            _linearBVH.BuildFromMesh(cachedVertices, cachedTriangles, ToolObject.transform, sortedTriangleIndices);
        }
        else
        {
            _meshBVH.Build(cachedVertices, cachedTriangles, ToolObject.transform);
            _linearBVH.BuildFromBVH(_meshBVH);
        }

        if (_linearBVH.NodeCount == 0)
        {
            if (ShowDebugInfo)
                Debug.LogWarning("Tool LinearBVH has no nodes; skipping tool SDF generation.");
            return;
        }

        ComputeBuffer nodeBuffer = null;
        ComputeBuffer triangleBuffer = null;

        try
        {
            _linearBVH.CreateBuffers(out nodeBuffer, out triangleBuffer);
            if (nodeBuffer == null || triangleBuffer == null)
            {
                if (ShowDebugInfo)
                    Debug.LogWarning("Failed to create tool BVH buffers.");
                return;
            }

            // Full bandwidth — tool SDF needs correct interior signs for CSG
            float fullBandWidth = _toolVolume.WorldBounds.size.magnitude * 2f;
            Bounds meshRootBounds = _linearBVH.RootBounds; // ADDED: pass mesh bounds
            _toolVolume.GenerateFromMesh(nodeBuffer, triangleBuffer, _linearBVH.NodeCount,
                                         fullBandWidth, meshRootBounds.min, meshRootBounds.max);
            _toolVolume.Finalize();
        }
        finally
        {
            nodeBuffer?.Release();
            triangleBuffer?.Release();
        }
    }

    private void ApplyCSG()
    {
        if (TargetVolume == null || TargetVolume.SDFTexture == null || _toolVolume == null || _toolVolume.VolumeTexture == null)
            return;

        if (ResetToBaseBeforeOperation)
        {
            CopyBaseToTarget();
        }

        int kernel = Operation switch
        {
            SDFOperations.CSGOperation.Union => _unionKernel,
            SDFOperations.CSGOperation.Subtract => _subtractKernel,
            SDFOperations.CSGOperation.Intersect => _intersectKernel,
            _ => _subtractKernel
        };

        SetVolumeParameters(SDFOperationsShader, TargetVolume.WorldBounds, TargetVolume.Resolution);
        SDFOperationsShader.SetFloat("_ToolBlend", ToolBlend);
        SDFOperationsShader.SetInt("_UseToolSDF", 1);
        SDFOperationsShader.SetVector("_ToolVolumeMin", _toolVolume.WorldBounds.min);
        SDFOperationsShader.SetVector("_ToolVolumeMax", _toolVolume.WorldBounds.max);
        SDFOperationsShader.SetInts("_ToolVolumeResolution",
            _toolVolume.Resolution.x, _toolVolume.Resolution.y, _toolVolume.Resolution.z);
        SDFOperationsShader.SetTexture(kernel, "_SDFVolume", TargetVolume.SDFTexture);
        SDFOperationsShader.SetTexture(kernel, "_ToolSDF", _toolVolume.VolumeTexture);

        DispatchCompute(TargetVolume.Resolution, kernel);
    }

    private void SetVolumeParameters(ComputeShader shader, Bounds bounds, Vector3Int resolution)
    {
        shader.SetVector("_VolumeMin", bounds.min);
        shader.SetVector("_VolumeMax", bounds.max);
        shader.SetInts("_VolumeResolution", resolution.x, resolution.y, resolution.z);
    }

    private void DispatchCompute(Vector3Int resolution, int kernel)
    {
        int threadGroupsX = Mathf.CeilToInt(resolution.x / 8f);
        int threadGroupsY = Mathf.CeilToInt(resolution.y / 8f);
        int threadGroupsZ = Mathf.CeilToInt(resolution.z / 8f);

        SDFOperationsShader.Dispatch(kernel,
            Mathf.Max(1, threadGroupsX),
            Mathf.Max(1, threadGroupsY),
            Mathf.Max(1, threadGroupsZ));
    }

    private bool BoundsApproximatelyEqual(Bounds a, Bounds b)
    {
        return (a.center - b.center).sqrMagnitude < 1e-6f &&
               (a.size - b.size).sqrMagnitude < 1e-6f;
    }

    private Bounds TransformBounds(Bounds localBounds, Matrix4x4 matrix)
    {
        Vector3 min = localBounds.min;
        Vector3 max = localBounds.max;

        Vector3[] corners = new Vector3[8]
        {
            matrix.MultiplyPoint3x4(new Vector3(min.x, min.y, min.z)),
            matrix.MultiplyPoint3x4(new Vector3(max.x, min.y, min.z)),
            matrix.MultiplyPoint3x4(new Vector3(min.x, max.y, min.z)),
            matrix.MultiplyPoint3x4(new Vector3(max.x, max.y, min.z)),
            matrix.MultiplyPoint3x4(new Vector3(min.x, min.y, max.z)),
            matrix.MultiplyPoint3x4(new Vector3(max.x, min.y, max.z)),
            matrix.MultiplyPoint3x4(new Vector3(min.x, max.y, max.z)),
            matrix.MultiplyPoint3x4(new Vector3(max.x, max.y, max.z))
        };

        Bounds result = new Bounds(corners[0], Vector3.zero);
        for (int i = 1; i < 8; i++)
        {
            result.Encapsulate(corners[i]);
        }

        return result;
    }

    private void ReleaseResources()
    {
        if (_baseSdfTex != null)
        {
            _baseSdfTex.Release();
            _baseSdfTex = null;
        }

        if (_toolVolume != null)
        {
            _toolVolume.Dispose();
            _toolVolume = null;
        }

        _gpuSorter.Release();
    }
}