using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class MeshGenerator : MonoBehaviour
{
    [Header("Input")]
    public MeshFilter SourceMeshFilter;
    [Header("Output")]
    public MeshFilter OutputMeshFilter;

    [Header("SDF Volume Settings")]
    public Vector3Int Resolution = new Vector3Int(64, 64, 64);
    public Bounds WorldBounds;
    [Min(0f)]
    public float Padding = 0.1f;
    [Min(0.1f)]
    public float NarrowBandWidthMultiplier = 5f;

    [Header("Compute Shader Settings")]
    public ComputeShader MarchingCubesComputeShader;
    public ComputeShader SDFGeneratorShader;
    public ComputeShader BVHSortShader;

    [Header("Voxel Settings")]
    public float IsoLevel = 0f;

    private const int _threadGroupSize = 8;
    private ComputeBuffer _triangleBuffer;
    private ComputeBuffer _triCountBuffer;
    private ComputeBuffer _pointBuffer;

    private RenderTexture _sdfTex;
    private ComputeBuffer _bvhNodeBuffer;
    private ComputeBuffer _triangleInputBuffer;
    private int _initializeKernel;
    private int _meshToSDFKernel;
    private int _finalizeKernel;
    private int _marchKernel;

    private MeshBVH _meshBVH;
    private LinearBVH _linearBVH;
    private readonly GpuBVHSorter _gpuBVHSorter = new GpuBVHSorter();

    private Mesh _originalSourceMesh;
    private List<Triangle> _allTriangles = new List<Triangle>();
    
    // Region-based processing parameters
    private int _regionSize = 0;
    private bool _useRegionProcessing = false;

    public void Run()
    {
        if (SourceMeshFilter == null || SourceMeshFilter.sharedMesh == null)
        {
            Debug.LogWarning("Missing SourceMeshFilter or mesh.");
            return;
        }

        CacheOriginalSourceMesh();
        EnsureOutputMeshFilter();

        if (MarchingCubesComputeShader == null || SDFGeneratorShader == null)
        {
            Debug.LogWarning("Missing compute shaders (MarchingCubesComputeShader or SDFGeneratorShader).");
            return;
        }

        EnsureCubicResolution();
        CacheKernels();
        
        // Determine if region-based processing is needed
        int numVoxelsPerAxis = Resolution.x - 1;
        int numVoxels = numVoxelsPerAxis * numVoxelsPerAxis * numVoxelsPerAxis;
        long bufferSizeBytes = (long)numVoxels * 5 * 36;
        const long maxBufferSize = 2147483648;
        
        if (bufferSizeBytes > maxBufferSize)
        {
            _regionSize = Mathf.FloorToInt(Mathf.Pow(maxBufferSize / 36 / 5, 1f / 3f)) + 1;
            _useRegionProcessing = true;
            Debug.Log($"Resolution {Resolution.x}^3 requires region-based processing. Using region size: {_regionSize}^3");
        }
        
        BuildSDFVolume();
        
        if (_useRegionProcessing)
        {
            _allTriangles.Clear();
            GenerateMeshWithRegions();
        }
        else
        {
            CreateBuffers();
            PopulatePointBufferFromSDF();
            GenerateMesh();
        }

        if (!Application.isPlaying)
        {
            ReleaseBuffers();
            ReleaseSDFResources();
        }
    }

    public void GenerateMesh()
    {
        int numPointsPerAxis = Resolution.x;
        int numVoxelsPerAxis = numPointsPerAxis - 1;
        int numThreadsPerAxis = Mathf.CeilToInt(numVoxelsPerAxis / (float)_threadGroupSize);

        _triangleBuffer.SetCounterValue(0);

        MarchingCubesComputeShader.SetBuffer(_marchKernel, "points", _pointBuffer);
        MarchingCubesComputeShader.SetBuffer(_marchKernel, "triangles", _triangleBuffer);
        MarchingCubesComputeShader.SetInt("numPointsPerAxis", numPointsPerAxis);
        MarchingCubesComputeShader.SetFloat("isoLevel", IsoLevel);

        MarchingCubesComputeShader.Dispatch(_marchKernel, numThreadsPerAxis, numThreadsPerAxis, numThreadsPerAxis);

        ComputeBuffer.CopyCount(_triangleBuffer, _triCountBuffer, 0);
        int[] triCountArray = { 0 };
        _triCountBuffer.GetData(triCountArray);
        int numTris = triCountArray[0];

        Triangle[] tris = new Triangle[numTris];
        _triangleBuffer.GetData(tris, 0, 0, numTris);

        Mesh mesh = new Mesh();

        var vertices = new Vector3[numTris * 3];
        var meshTriangles = new int[numTris * 3];

        Transform meshTransform = OutputMeshFilter != null ? OutputMeshFilter.transform : SourceMeshFilter.transform;

        for (int i = 0; i < numTris; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                meshTriangles[i * 3 + j] = i * 3 + j;
                vertices[i * 3 + j] = meshTransform.InverseTransformPoint(tris[i][j]);
            }
        }

        mesh.vertices = vertices;
        mesh.triangles = meshTriangles;

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        string sourceName = _originalSourceMesh != null ? _originalSourceMesh.name : SourceMeshFilter.sharedMesh.name;
        mesh.name = sourceName + "_SDF";
        OutputMeshFilter.sharedMesh = mesh;
    }

    private void GenerateMeshWithRegions()
    {
        Transform meshTransform = OutputMeshFilter != null ? OutputMeshFilter.transform : SourceMeshFilter.transform;
        int regionsPerAxis = Mathf.CeilToInt(Resolution.x / (float)_regionSize);
        int totalRegions = regionsPerAxis * regionsPerAxis * regionsPerAxis;

        Debug.Log($"Processing {totalRegions} regions ({regionsPerAxis}x{regionsPerAxis}x{regionsPerAxis})...");

        for (int rz = 0; rz < regionsPerAxis; rz++)
        {
            for (int ry = 0; ry < regionsPerAxis; ry++)
            {
                for (int rx = 0; rx < regionsPerAxis; rx++)
                {
                    int regionIndex = rx + ry * regionsPerAxis + rz * regionsPerAxis * regionsPerAxis;
                    Debug.Log($"Processing region {regionIndex + 1}/{totalRegions}");

                    ProcessRegion(rx, ry, rz, meshTransform);
                }
            }
        }

        // Build final mesh from all collected triangles
        BuildMeshFromTriangles(meshTransform);
    }

    private void ProcessRegion(int regionX, int regionY, int regionZ, Transform meshTransform)
    {
        // Calculate region bounds in grid coordinates
        int startX = regionX * _regionSize;
        int startY = regionY * _regionSize;
        int startZ = regionZ * _regionSize;

        int endX = Mathf.Min(startX + _regionSize, Resolution.x);
        int endY = Mathf.Min(startY + _regionSize, Resolution.y);
        int endZ = Mathf.Min(startZ + _regionSize, Resolution.z);

        int regionResX = endX - startX;
        int regionResY = endY - startY;
        int regionResZ = endZ - startZ;

        int regionNumPoints = regionResX * regionResY * regionResZ;

        // Create temporary buffers for this region
        ComputeBuffer regionPointBuffer = new ComputeBuffer(regionNumPoints, sizeof(float) * 4);
        int regionMaxTriangles = (regionResX - 1) * (regionResY - 1) * (regionResZ - 1) * 5;
        ComputeBuffer regionTriangleBuffer = new ComputeBuffer(regionMaxTriangles, sizeof(float) * 3 * 3, ComputeBufferType.Append);
        ComputeBuffer regionTriCountBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);

        try
        {
            // Populate point buffer for this region from SDF
            Vector4[] regionPoints = new Vector4[regionNumPoints];
            float[] sdfValues = ReadSDFValues(Resolution.x, Resolution.y, Resolution.z);

            if (sdfValues != null)
            {
                int index = 0;
                for (int z = startZ; z < endZ; z++)
                {
                    for (int y = startY; y < endY; y++)
                    {
                        for (int x = startX; x < endX; x++)
                        {
                            int sdfIndex = x + y * Resolution.x + z * Resolution.x * Resolution.y;
                            float sdfValue = sdfValues[sdfIndex];

                            Vector3 t = new Vector3(
                                Resolution.x > 1 ? (float)x / (Resolution.x - 1) : 0f,
                                Resolution.y > 1 ? (float)y / (Resolution.y - 1) : 0f,
                                Resolution.z > 1 ? (float)z / (Resolution.z - 1) : 0f
                            );
                            Vector3 worldPos = WorldBounds.min + Vector3.Scale(t, WorldBounds.size);
                            regionPoints[index] = new Vector4(worldPos.x, worldPos.y, worldPos.z, sdfValue);
                            index++;
                        }
                    }
                }

                regionPointBuffer.SetData(regionPoints);

                // Run Marching Cubes on this region
                regionTriangleBuffer.SetCounterValue(0);

                MarchingCubesComputeShader.SetBuffer(_marchKernel, "points", regionPointBuffer);
                MarchingCubesComputeShader.SetBuffer(_marchKernel, "triangles", regionTriangleBuffer);
                MarchingCubesComputeShader.SetInt("numPointsPerAxis", regionResX);
                MarchingCubesComputeShader.SetFloat("isoLevel", IsoLevel);

                int numVoxelsPerAxis = regionResX - 1;
                int numThreadsPerAxis = Mathf.CeilToInt(numVoxelsPerAxis / (float)_threadGroupSize);
                MarchingCubesComputeShader.Dispatch(_marchKernel, numThreadsPerAxis, numThreadsPerAxis, numThreadsPerAxis);

                // Read triangle count and data
                ComputeBuffer.CopyCount(regionTriangleBuffer, regionTriCountBuffer, 0);
                int[] triCountArray = { 0 };
                regionTriCountBuffer.GetData(triCountArray);
                int numTris = triCountArray[0];

                if (numTris > 0)
                {
                    Triangle[] tris = new Triangle[numTris];
                    regionTriangleBuffer.GetData(tris, 0, 0, numTris);

                    // Add to global triangle list
                    for (int i = 0; i < numTris; i++)
                    {
                        _allTriangles.Add(tris[i]);
                    }
                }
            }
        }
        finally
        {
            // Clean up region buffers
            regionPointBuffer?.Release();
            regionTriangleBuffer?.Release();
            regionTriCountBuffer?.Release();
        }
    }

    private void BuildMeshFromTriangles(Transform meshTransform)
    {
        int numTris = _allTriangles.Count;
        if (numTris == 0)
        {
            Debug.LogWarning("No triangles generated from Marching Cubes.");
            return;
        }

        Mesh mesh = new Mesh();

        var vertices = new Vector3[numTris * 3];
        var meshTriangles = new int[numTris * 3];

        for (int i = 0; i < numTris; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                meshTriangles[i * 3 + j] = i * 3 + j;
                vertices[i * 3 + j] = meshTransform.InverseTransformPoint(_allTriangles[i][j]);
            }
        }

        mesh.vertices = vertices;
        mesh.triangles = meshTriangles;

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        string sourceName = _originalSourceMesh != null ? _originalSourceMesh.name : SourceMeshFilter.sharedMesh.name;
        mesh.name = sourceName + "_SDF";
        OutputMeshFilter.sharedMesh = mesh;

        Debug.Log($"Generated mesh with {numTris} triangles");
    }

    public void PopulatePointBufferFromSDF()
    {
        int resX = Resolution.x;
        int resY = Resolution.y;
        int resZ = Resolution.z;

        int numPoints = resX * resY * resZ;
        Vector4[] points = new Vector4[numPoints];

        float[] sdfValues = ReadSDFValues(resX, resY, resZ);
        if (sdfValues == null || sdfValues.Length != numPoints)
        {
            Debug.LogWarning("SDF readback returned unexpected data size.");
            return;
        }

        int index = 0;
        for (int z = 0; z < resZ; z++)
        {
            for (int y = 0; y < resY; y++)
            {
                for (int x = 0; x < resX; x++)
                {
                    float sdfValue = sdfValues[index];
                    Vector3 t = new Vector3(
                        resX > 1 ? (float)x / (resX - 1) : 0f,
                        resY > 1 ? (float)y / (resY - 1) : 0f,
                        resZ > 1 ? (float)z / (resZ - 1) : 0f
                    );
                    Vector3 worldPos = WorldBounds.min + Vector3.Scale(t, WorldBounds.size);
                    points[index] = new Vector4(worldPos.x, worldPos.y, worldPos.z, sdfValue);
                    index++;
                }
            }
        }

        _pointBuffer.SetData(points);
    }

    private float[] ReadSDFValues(int resX, int resY, int resZ)
    {
        var request = AsyncGPUReadback.Request(_sdfTex);
        request.WaitForCompletion();

        if (request.hasError)
        {
            Debug.LogWarning("SDF readback failed.");
            return null;
        }

        var data = request.GetData<float>();
        int expectedFull = resX * resY * resZ;
        int expectedSlice = resX * resY;

        if (data.Length == expectedFull)
        {
            float[] full = new float[expectedFull];
            data.CopyTo(full);
            return full;
        }

        if (data.Length != expectedSlice)
        {
            return null;
        }

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
                RenderTexture.ReleaseTemporary(sliceRT);
                Debug.LogWarning("SDF slice readback failed.");
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

    void CreateBuffers()
    {
        int numPoints = Resolution.x * Resolution.y * Resolution.z;
        int numVoxelsPerAxis = Resolution.x - 1;
        int numVoxels = numVoxelsPerAxis * numVoxelsPerAxis * numVoxelsPerAxis;
        int maxTriangleCount = numVoxels * 5;

        // Check if buffer size would exceed max compute buffer size (2.1 GB)
        long bufferSizeBytes = (long)maxTriangleCount * 36; // 36 bytes per triangle (3 verts * 3 floats * 4 bytes)
        const long maxBufferSize = 2147483648; // 2.1 GB
        
        if (bufferSizeBytes > maxBufferSize)
        {
            Debug.LogError($"Computed buffer size ({bufferSizeBytes} bytes) exceeds maximum allowed size ({maxBufferSize} bytes). " +
                $"Please reduce the Resolution. Current resolution: {Resolution.x}^3. Try using a smaller value.");
            return;
        }

        // Always create buffers in editor (since buffers are released immediately to prevent memory leak)
        // Otherwise, only create if null or if size has changed
        if (!Application.isPlaying || (_pointBuffer == null || numPoints != _pointBuffer.count))
        {
            if (Application.isPlaying)
            {
                ReleaseBuffers();
            }
            _triangleBuffer = new ComputeBuffer(maxTriangleCount, sizeof(float) * 3 * 3, ComputeBufferType.Append);
            _pointBuffer = new ComputeBuffer(numPoints, sizeof(float) * 4);
            _triCountBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);

        }
    }

    void ReleaseBuffers()
    {
        if (_triangleBuffer != null)
        {
            _triangleBuffer.Release();
            _pointBuffer.Release();
            _triCountBuffer.Release();
        }
    }

    private void CacheKernels()
    {
        _initializeKernel = SDFGeneratorShader.FindKernel("InitializeSDF");
        _meshToSDFKernel = SDFGeneratorShader.FindKernel("MeshToSDF");
        _finalizeKernel = SDFGeneratorShader.FindKernel("FinalizeSDF");
        _marchKernel = MarchingCubesComputeShader.FindKernel("March");
    }

    private void BuildSDFVolume()
    {
        Mesh sourceMesh = SourceMeshFilter.sharedMesh;
        Bounds bounds = sourceMesh.bounds;
        bounds.Expand(Vector3.one * (Padding * 2f));
        WorldBounds = TransformBounds(bounds, SourceMeshFilter.transform.localToWorldMatrix);

        CreateSDFTexture();

        SetCommonParameters(SDFGeneratorShader);
        SDFGeneratorShader.SetFloat("_InitialValue", 1f);
        SDFGeneratorShader.SetTexture(_initializeKernel, "_SDFVolume", _sdfTex);
        DispatchCompute(SDFGeneratorShader, _initializeKernel);

        _linearBVH = new LinearBVH();

        int[] sortedTriangleIndices = null;
        if (BVHSortShader != null)
        {
            Vector3[] localVertices = sourceMesh.vertices;
            Vector3[] worldVertices = new Vector3[localVertices.Length];
            for (int i = 0; i < localVertices.Length; i++)
            {
                worldVertices[i] = SourceMeshFilter.transform.TransformPoint(localVertices[i]);
            }

            _gpuBVHSorter.TrySortTriangles(BVHSortShader, worldVertices, sourceMesh.triangles, WorldBounds, out sortedTriangleIndices);
        }

        if (sortedTriangleIndices != null && sortedTriangleIndices.Length > 0)
        {
            _linearBVH.BuildFromMesh(sourceMesh, SourceMeshFilter.transform, sortedTriangleIndices);
        }
        else
        {
            _meshBVH = new MeshBVH();
            _meshBVH.Build(sourceMesh, SourceMeshFilter.transform);
            _linearBVH.BuildFromBVH(_meshBVH);
        }

        _bvhNodeBuffer = null;
        _triangleInputBuffer = null;
        _linearBVH.CreateBuffers(out _bvhNodeBuffer, out _triangleInputBuffer);

        float voxelSize = (WorldBounds.size.x / Resolution.x +
                           WorldBounds.size.y / Resolution.y +
                           WorldBounds.size.z / Resolution.z) / 3f;
        float narrowBandWidth = voxelSize * Mathf.Max(0.1f, NarrowBandWidthMultiplier);

        SetCommonParameters(SDFGeneratorShader);
        SDFGeneratorShader.SetBuffer(_meshToSDFKernel, "_BVHNodes", _bvhNodeBuffer);
        SDFGeneratorShader.SetBuffer(_meshToSDFKernel, "_Triangles", _triangleInputBuffer);
        SDFGeneratorShader.SetInt("_BVHNodeCount", _linearBVH.NodeCount);
        SDFGeneratorShader.SetFloat("_NarrowBandWidth", narrowBandWidth);
        SDFGeneratorShader.SetTexture(_meshToSDFKernel, "_SDFVolume", _sdfTex);
        DispatchCompute(SDFGeneratorShader, _meshToSDFKernel);

        SetCommonParameters(SDFGeneratorShader);
        SDFGeneratorShader.SetTexture(_finalizeKernel, "_SDFVolume", _sdfTex);
        DispatchCompute(SDFGeneratorShader, _finalizeKernel);

        _bvhNodeBuffer?.Release();
        _triangleInputBuffer?.Release();
    }

    private void CreateSDFTexture()
    {
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

    private void EnsureCubicResolution()
    {
        if (Resolution.x == Resolution.y && Resolution.x == Resolution.z)
        {
            return;
        }

        int max = Mathf.Max(Resolution.x, Mathf.Max(Resolution.y, Resolution.z));
        Debug.LogWarning($"MarchingCubes2.compute expects a cubic grid. Forcing resolution to {max}^3.");
        Resolution = new Vector3Int(max, max, max);
    }

    private void CacheOriginalSourceMesh()
    {
        if (_originalSourceMesh == null && SourceMeshFilter.sharedMesh != null)
        {
            _originalSourceMesh = SourceMeshFilter.sharedMesh;
        }
    }

    private void EnsureOutputMeshFilter()
    {
        if (OutputMeshFilter != null && OutputMeshFilter != SourceMeshFilter)
        {
            return;
        }

        Transform parent = SourceMeshFilter.transform;
        GameObject outputGO = new GameObject(SourceMeshFilter.gameObject.name + "_SDF_Output_Mesh");
        outputGO.transform.SetParent(parent, false);
        outputGO.transform.localPosition = Vector3.zero;
        outputGO.transform.localRotation = Quaternion.identity;
        outputGO.transform.localScale = Vector3.one;

        OutputMeshFilter = outputGO.AddComponent<MeshFilter>();
        var sourceRenderer = SourceMeshFilter.GetComponent<MeshRenderer>();
        if (sourceRenderer != null)
        {
            var outputRenderer = outputGO.AddComponent<MeshRenderer>();
            outputRenderer.sharedMaterials = sourceRenderer.sharedMaterials;
        }
    }

    private void ReleaseSDFResources()
    {
        if (_sdfTex != null)
        {
            _sdfTex.Release();
            _sdfTex = null;
        }
    }

    struct Triangle
    {
#pragma warning disable 649 // disable unassigned variable warning
        public Vector3 vertexC;
        public Vector3 vertexB;
        public Vector3 vertexA;

        public Vector3 this[int i]
        {
            get
            {
                switch (i)
                {
                    case 0:
                        return vertexA;
                    case 1:
                        return vertexB;
                    default:
                        return vertexC;
                }
            }
        }
    }

    private void OnDestroy()
    {
        ReleaseBuffers();
        ReleaseSDFResources();
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(MeshGenerator))]
public class MeshGeneratorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        MeshGenerator meshGenerator = (MeshGenerator)target;
        if (GUILayout.Button("Run"))
        {
            meshGenerator.Run();
        }
    }
}
#endif