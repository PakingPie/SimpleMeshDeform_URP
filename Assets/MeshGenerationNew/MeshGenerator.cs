using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class MeshGenerator : MonoBehaviour
{
    [Header("Input")]
    public MeshFilter SourceMeshFilter;

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

    public void Run()
    {
        if (SourceMeshFilter == null || SourceMeshFilter.sharedMesh == null)
        {
            Debug.LogWarning("Missing SourceMeshFilter or mesh.");
            return;
        }

        if (MarchingCubesComputeShader == null || SDFGeneratorShader == null)
        {
            Debug.LogWarning("Missing compute shaders (MarchingCubesComputeShader or SDFGeneratorShader).");
            return;
        }

        EnsureCubicResolution();
        CacheKernels();
        CreateBuffers();
        BuildSDFVolume();
        PopulatePointBufferFromSDF();
        GenerateMesh();

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

        Mesh mesh = SourceMeshFilter.sharedMesh;
        if (mesh == null)
        {
            mesh = new Mesh();
        }
        else
        {
            mesh.Clear();
        }

        var vertices = new Vector3[numTris * 3];
        var meshTriangles = new int[numTris * 3];

        for (int i = 0; i < numTris; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                meshTriangles[i * 3 + j] = i * 3 + j;
                vertices[i * 3 + j] = tris[i][j];
            }
        }

        mesh.vertices = vertices;
        mesh.triangles = meshTriangles;

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.name = SourceMeshFilter.sharedMesh.name + "_SDF";
        SourceMeshFilter.sharedMesh = mesh;
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
        Bounds bounds = SourceMeshFilter.sharedMesh.bounds;
        bounds.Expand(Vector3.one * (Padding * 2f));
        WorldBounds = TransformBounds(bounds, SourceMeshFilter.transform.localToWorldMatrix);

        CreateSDFTexture();

        SetCommonParameters(SDFGeneratorShader);
        SDFGeneratorShader.SetFloat("_InitialValue", 1f);
        SDFGeneratorShader.SetTexture(_initializeKernel, "_SDFVolume", _sdfTex);
        DispatchCompute(SDFGeneratorShader, _initializeKernel);

        _meshBVH = new MeshBVH();
        _meshBVH.Build(SourceMeshFilter.sharedMesh, SourceMeshFilter.transform);
        _linearBVH = new LinearBVH();
        _linearBVH.BuildFromBVH(_meshBVH);

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