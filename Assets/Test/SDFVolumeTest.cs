using UnityEngine;
using UnityEngine.Rendering;
using System;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class SDFVolumeTest : MonoBehaviour
{
    [Header("SDF Volume Settings")]
    public int VoxelResolution = 64;
    public Vector3Int Resolution => new Vector3Int(VoxelResolution, VoxelResolution, VoxelResolution);
    public Bounds WorldBounds;

    [Header("Bounds Padding")]
    [Range(0f, 0.5f)]
    [Tooltip("Padding around mesh bounds (0 = tight fit, 0.1 = 10% padding)")]
    public float BoundsPadding = 0.1f;

    [Header("Origin Mesh")]
    public GameObject SourceMeshObject;

    [Header("Compute Shaders")]
    public ComputeShader SDFGeneratorShader;
    public ComputeShader BVHSortShader;

    [Header("SDF Band Width")]
    [Tooltip("Narrow band width for SDF generation. -1 = auto (5x voxel size). Set to large value (999f) for solid interior.")]
    public float NarrowBandWidth = -1f;

    [Header("Realtime Update")]
    public bool RealTimeUpdate = false;
    public bool AllowEditModeUpdate = false;
    [Min(0f)]
    public float MinUpdateInterval = 0.1f;
    public bool RealtimeUseGpuSort = false;
    [Min(1)]
    public int MaxTrianglesForGpuSort = 50000;
    [Min(1)]
    public int MaxRealtimeResolutionPerAxis = 128;

    [Header("Debug")]
    public bool ShowDebugInfo = false;
    public GameObject DebugObject;

    public float VoxelSize => (WorldBounds.size.x / Resolution.x +
                               WorldBounds.size.y / Resolution.y +
                               WorldBounds.size.z / Resolution.z) / 3f;

    public RenderTexture SDFTexture => _sdfTex;

    private RenderTexture _sdfTex;
    private int _initializeKernel;
    private int _meshToSDFKernel;
    private int _finalizeKernel;

    // Double buffering for BVH data to prevent GPU race conditions
    private class BVHBufferSet
    {
        public ComputeBuffer bvhNodeBuffer;
        public ComputeBuffer triangleBuffer;
        public int frameCreated;
        public bool inUse;

        public void Release()
        {
            if (bvhNodeBuffer != null)
            {
                bvhNodeBuffer.Release();
                bvhNodeBuffer = null;
            }
            if (triangleBuffer != null)
            {
                triangleBuffer.Release();
                triangleBuffer = null;
            }
            inUse = false;
        }
    }

    private const int BufferPoolSize = 3; // Triple buffering
    private BVHBufferSet[] _bufferPool;
    private int _currentBufferIndex = 0;
    private int _frameCount = 0;
    private const int FramesBeforeReuse = 3; // Wait 3 frames before reusing a buffer

    private MeshBVH _meshBVH;
    private LinearBVH _linearBVH;
    private readonly GpuBVHSorter _gpuBVHSorter = new GpuBVHSorter();

    private Mesh _lastMesh;
    private int _lastVertexCount;
    private int _lastTriangleCount;
    private Matrix4x4 _lastTransformMatrix;
    private float _nextUpdateTime;
    private bool _realtimeBlockedLogged;
    private bool _isGenerating = false;

    private void OnEnable()
    {
        InitializeBufferPool();
    }

    private void InitializeBufferPool()
    {
        if (_bufferPool == null)
        {
            _bufferPool = new BVHBufferSet[BufferPoolSize];
            for (int i = 0; i < BufferPoolSize; i++)
            {
                _bufferPool[i] = new BVHBufferSet();
            }
        }
    }

    public void Run()
    {
        if (_isGenerating) return;

        _isGenerating = true;
        try
        {
            InitializeSDFVolume();
            GenerateSDFVolume(true); // Allow GPU sort for manual runs
            FinalizeSDFVolume();
            CacheSourceState();

            if (DebugObject != null)
            {
                DebugObject.GetComponent<MeshRenderer>().sharedMaterial.SetTexture("_DataTex", _sdfTex);
                DebugObject.transform.position = WorldBounds.center;
                DebugObject.transform.rotation = Quaternion.identity;
                DebugObject.transform.localScale = WorldBounds.size;
            }
        }
        finally
        {
            _isGenerating = false;
        }
    }

    private void Update()
    {
        _frameCount++;

        if (!RealTimeUpdate)
            return;

        if (!Application.isPlaying && !AllowEditModeUpdate)
            return;

        if (_isGenerating)
            return;

        if (Resolution.x > MaxRealtimeResolutionPerAxis ||
            Resolution.y > MaxRealtimeResolutionPerAxis ||
            Resolution.z > MaxRealtimeResolutionPerAxis)
        {
            if (!_realtimeBlockedLogged)
            {
                Debug.LogWarning($"Realtime SDF update skipped: resolution exceeds MaxRealtimeResolutionPerAxis ({MaxRealtimeResolutionPerAxis}).");
                _realtimeBlockedLogged = true;
            }
            return;
        }
        _realtimeBlockedLogged = false;

        if (SourceMeshObject == null || SDFGeneratorShader == null)
            return;

        float currentTime = Application.isPlaying ? Time.time : (float)EditorApplication.timeSinceStartup;
        if (MinUpdateInterval > 0f && currentTime < _nextUpdateTime)
            return;

        if (!HasSourceChanged())
            return;

        _isGenerating = true;
        try
        {
            InitializeSDFVolume();
            // For real-time updates, use CPU sort by default (more stable)
            // GPU sort only if explicitly enabled and triangle count is reasonable
            bool useGpuSort = RealtimeUseGpuSort &&
                              BVHSortShader != null &&
                              GetTriangleCount() <= MaxTrianglesForGpuSort;
            GenerateSDFVolume(useGpuSort);
            FinalizeSDFVolume();

            _nextUpdateTime = currentTime + MinUpdateInterval;
            if (DebugObject != null)
            {
                DebugObject.GetComponent<MeshRenderer>().sharedMaterial.SetTexture("_DataTex", _sdfTex);
                DebugObject.transform.position = WorldBounds.center;
                DebugObject.transform.rotation = Quaternion.identity;
                DebugObject.transform.localScale = WorldBounds.size;
            }

        }
        finally
        {
            _isGenerating = false;
        }
    }

    private int GetTriangleCount()
    {
        if (SourceMeshObject == null) return 0;
        var mf = SourceMeshObject.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return 0;
        return mf.sharedMesh.triangles.Length / 3;
    }

    private void OnDisable()
    {
        ReleaseAllBuffers();
        if (_sdfTex != null)
        {
            _sdfTex.Release();
            _sdfTex = null;
        }
    }

    private void OnDestroy()
    {
        ReleaseAllBuffers();
        if (_sdfTex != null)
        {
            _sdfTex.Release();
            _sdfTex = null;
        }
    }

    private void ReleaseAllBuffers()
    {
        if (_bufferPool != null)
        {
            for (int i = 0; i < _bufferPool.Length; i++)
            {
                _bufferPool[i]?.Release();
            }
        }
    }

    private BVHBufferSet GetAvailableBufferSet()
    {
        InitializeBufferPool();

        // Find a buffer set that's not in use or old enough to reuse
        for (int i = 0; i < BufferPoolSize; i++)
        {
            int idx = (_currentBufferIndex + i) % BufferPoolSize;
            var bufferSet = _bufferPool[idx];

            if (!bufferSet.inUse || (_frameCount - bufferSet.frameCreated) >= FramesBeforeReuse)
            {
                bufferSet.Release();
                _currentBufferIndex = (idx + 1) % BufferPoolSize;
                return bufferSet;
            }
        }

        // Force reuse the oldest one if all are in use
        var oldest = _bufferPool[_currentBufferIndex];
        oldest.Release();
        _currentBufferIndex = (_currentBufferIndex + 1) % BufferPoolSize;
        return oldest;
    }

    public void InitializeSDFVolume()
    {
        if (SourceMeshObject == null) return;

        var meshFilter = SourceMeshObject.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null) return;

        Bounds bounds = meshFilter.sharedMesh.bounds;
        Vector3 padding = bounds.size * BoundsPadding;
        bounds.Expand(padding * 2);
        WorldBounds = TransformBounds(bounds, SourceMeshObject.transform.localToWorldMatrix);

        CreateSDFTexture();

        _initializeKernel = SDFGeneratorShader.FindKernel("InitializeSDF");
        _meshToSDFKernel = SDFGeneratorShader.FindKernel("MeshToSDF");
        _finalizeKernel = SDFGeneratorShader.FindKernel("FinalizeSDF");

        SetCommonParameters(SDFGeneratorShader);
        SDFGeneratorShader.SetFloat("_InitialValue", 1f);
        SDFGeneratorShader.SetTexture(_initializeKernel, "_SDFVolume", _sdfTex);

        DispatchCompute(SDFGeneratorShader, _initializeKernel);
    }

    private void GenerateSDFVolume(bool allowGpuSort = true)
    {
        if (SourceMeshObject == null) return;

        var meshFilter = SourceMeshObject.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null) return;

        Mesh sourceMesh = meshFilter.sharedMesh;
        Transform sourceTransform = SourceMeshObject.transform;

        int triangleCount = sourceMesh.triangles.Length / 3;
        if (triangleCount == 0) return;

        _linearBVH = new LinearBVH();

        int[] sortedTriangleIndices = null;

        // GPU sorting path
        if (allowGpuSort && BVHSortShader != null && triangleCount <= MaxTrianglesForGpuSort)
        {
            Bounds meshBounds = TransformBounds(sourceMesh.bounds, sourceTransform.localToWorldMatrix);
            Vector3[] localVertices = sourceMesh.vertices;
            Vector3[] worldVertices = new Vector3[localVertices.Length];
            for (int i = 0; i < localVertices.Length; i++)
            {
                worldVertices[i] = sourceTransform.TransformPoint(localVertices[i]);
            }

            if (!_gpuBVHSorter.TrySortTriangles(BVHSortShader, worldVertices, sourceMesh.triangles, meshBounds, out sortedTriangleIndices))
            {
                sortedTriangleIndices = null;
            }
        }

        // Build BVH
        if (sortedTriangleIndices != null && sortedTriangleIndices.Length > 0)
        {
            _linearBVH.BuildFromMesh(sourceMesh, sourceTransform, sortedTriangleIndices);
        }
        else
        {
            // CPU path with Morton sort
            _meshBVH = new MeshBVH();
            _meshBVH.Build(sourceMesh, sourceTransform);
            _linearBVH.BuildFromBVH(_meshBVH);
        }

        if (_linearBVH.NodeCount == 0)
        {
            if (ShowDebugInfo) Debug.LogWarning("LinearBVH has no nodes, skipping SDF generation");
            return;
        }

        // Get a buffer set from the pool
        BVHBufferSet bufferSet = GetAvailableBufferSet();
        _linearBVH.CreateBuffers(out bufferSet.bvhNodeBuffer, out bufferSet.triangleBuffer);

        if (bufferSet.bvhNodeBuffer == null || bufferSet.triangleBuffer == null)
        {
            if (ShowDebugInfo) Debug.LogWarning("Failed to create BVH buffers");
            return;
        }

        bufferSet.frameCreated = _frameCount;
        bufferSet.inUse = true;

        GenerateSDFFromMesh(bufferSet.bvhNodeBuffer, bufferSet.triangleBuffer, _linearBVH.NodeCount, NarrowBandWidth);
    }

    private void GenerateSDFFromMesh(ComputeBuffer bvhNodesBuffer, ComputeBuffer trianglesBuffer,
                                     int nodeCount, float narrowBandWidth = -1f)
    {
        if (SDFGeneratorShader == null) return;
        if (bvhNodesBuffer == null || trianglesBuffer == null) return;

        if (narrowBandWidth < 0)
            narrowBandWidth = VoxelSize * 5f;

        SetCommonParameters(SDFGeneratorShader);
        SDFGeneratorShader.SetBuffer(_meshToSDFKernel, "_BVHNodes", bvhNodesBuffer);
        SDFGeneratorShader.SetBuffer(_meshToSDFKernel, "_Triangles", trianglesBuffer);
        SDFGeneratorShader.SetInt("_BVHNodeCount", nodeCount);
        SDFGeneratorShader.SetFloat("_NarrowBandWidth", narrowBandWidth);
        SDFGeneratorShader.SetTexture(_meshToSDFKernel, "_SDFVolume", _sdfTex);

        DispatchCompute(SDFGeneratorShader, _meshToSDFKernel);
    }

    public void FinalizeSDFVolume()
    {
        if (SDFGeneratorShader == null || _sdfTex == null) return;

        SetCommonParameters(SDFGeneratorShader);
        SDFGeneratorShader.SetTexture(_finalizeKernel, "_SDFVolume", _sdfTex);

        DispatchCompute(SDFGeneratorShader, _finalizeKernel);
    }

    private void CreateSDFTexture()
    {
        // Clamp resolution to valid range
        VoxelResolution = Mathf.Clamp(VoxelResolution, 4, 512);


        if (_sdfTex != null &&
            _sdfTex.width == Resolution.x &&
            _sdfTex.height == Resolution.y &&
            _sdfTex.volumeDepth == Resolution.z)
        {
            return;
        }

        if (_sdfTex != null)
        {
            _sdfTex.Release();
        }

        _sdfTex = new RenderTexture(Resolution.x, Resolution.y, 0, RenderTextureFormat.RFloat)
        {
            dimension = TextureDimension.Tex3D,
            volumeDepth = Resolution.z,
            enableRandomWrite = true,
            filterMode = FilterMode.Trilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "SDFVolume"
        };
        _sdfTex.Create();
    }

    private bool HasSourceChanged()
    {
        if (SourceMeshObject == null) return false;

        MeshFilter meshFilter = SourceMeshObject.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null)
            return false;

        Mesh mesh = meshFilter.sharedMesh;
        bool meshChanged = mesh != _lastMesh ||
                           mesh.vertexCount != _lastVertexCount ||
                           mesh.triangles.Length != _lastTriangleCount;

        bool transformChanged = SourceMeshObject.transform.localToWorldMatrix != _lastTransformMatrix;

        if (!meshChanged && !transformChanged)
            return false;

        CacheSourceState();
        return true;
    }

    private void CacheSourceState()
    {
        if (SourceMeshObject == null) return;

        MeshFilter meshFilter = SourceMeshObject.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null) return;

        _lastMesh = meshFilter.sharedMesh;
        _lastVertexCount = _lastMesh.vertexCount;
        _lastTriangleCount = _lastMesh.triangles.Length;
        _lastTransformMatrix = SourceMeshObject.transform.localToWorldMatrix;
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

    private float[] ReadSDFValues(int resX, int resY, int resZ)
    {
        var request = AsyncGPUReadback.Request(_sdfTex);
        request.WaitForCompletion();

        if (request.hasError)
        {
            Debug.LogError("GPU readback error.");
            return null;
        }

        var data = request.GetData<float>();
        int expectedFull = resX * resY * resZ;

        if (data.Length == expectedFull)
        {
            float[] full = new float[expectedFull];
            data.CopyTo(full);
            return full;
        }

        // Fallback: read slice by slice
        int expectedSlice = resX * resY;
        float[] values = new float[expectedFull];
        RenderTexture sliceRT = RenderTexture.GetTemporary(resX, resY, 0, RenderTextureFormat.RFloat);
        sliceRT.filterMode = FilterMode.Point;

        for (int z = 0; z < resZ; z++)
        {
            Graphics.CopyTexture(_sdfTex, z, 0, sliceRT, 0, 0);
            var sliceRequest = AsyncGPUReadback.Request(sliceRT);
            sliceRequest.WaitForCompletion();

            if (sliceRequest.hasError)
            {
                Debug.LogError("GPU readback error on slice.");
                RenderTexture.ReleaseTemporary(sliceRT);
                return null;
            }

            var sliceData = sliceRequest.GetData<float>();
            if (sliceData.Length != expectedSlice)
            {
                RenderTexture.ReleaseTemporary(sliceRT);
                return null;
            }

            int offset = z * expectedSlice;
            for (int i = 0; i < expectedSlice; i++)
            {
                values[offset + i] = sliceData[i];
            }
        }

        RenderTexture.ReleaseTemporary(sliceRT);
        return values;
    }

    public void SaveSDFVolumeDataAsset()
    {
#if UNITY_EDITOR
        if (_sdfTex == null)
        {
            Debug.LogWarning("No SDF texture to save. Run generation first.");
            return;
        }

        // Force GPU to finish
        GL.Flush();

        float[] sdfValues = ReadSDFValues(Resolution.x, Resolution.y, Resolution.z);
        if (sdfValues == null)
        {
            Debug.LogError("Failed to read SDF values from GPU.");
            return;
        }

        Texture3D texture3D = new Texture3D(Resolution.x, Resolution.y, Resolution.z, TextureFormat.RFloat, false)
        {
            filterMode = FilterMode.Trilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        Color[] colors = new Color[sdfValues.Length];
        for (int i = 0; i < sdfValues.Length; i++)
        {
            colors[i] = new Color(sdfValues[i], 0, 0, 1f);
        }

        texture3D.SetPixels(colors);
        texture3D.Apply();

        string texture3DPath = "Assets/Resources/SDFVolumeTexture3D.asset";
        AssetDatabase.CreateAsset(texture3D, texture3DPath);
        AssetDatabase.SaveAssets();
        Debug.Log($"SDF Volume Texture3D saved to {texture3DPath}");
#endif
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(SDFVolumeTest))]
public class SDFVolumeTestEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        SDFVolumeTest sdfVolumeTest = (SDFVolumeTest)target;

        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Generate SDF Volume"))
            {
                sdfVolumeTest.Run();
                SceneView.RepaintAll();
            }

            if (GUILayout.Button("Save As Asset"))
            {
                sdfVolumeTest.SaveSDFVolumeDataAsset();
            }
        }
    }
}
#endif