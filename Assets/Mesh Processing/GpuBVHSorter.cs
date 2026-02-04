using UnityEngine;
using UnityEngine.Rendering;
using System;

public class GpuBVHSorter
{
    private const int ThreadGroupSize = 256;
    
    // Cached buffers to avoid allocation every frame
    private ComputeBuffer _verticesBuffer;
    private ComputeBuffer _trianglesBuffer;
    private ComputeBuffer _codesBuffer;
    private ComputeBuffer _indicesBuffer;
    private int _lastTriangleCount;
    private int _lastVertexCount;
    private int _lastSortCount;
    
    // For async readback
    private int[] _cachedSortResult;
    private bool _sortResultReady;
    private int _cachedTriangleCount;

    public bool TrySortTriangles(ComputeShader sortShader, Vector3[] worldVertices, int[] triangles, Bounds bounds, out int[] sortedTriangleIndices)
    {
        sortedTriangleIndices = null;

        if (sortShader == null || worldVertices == null || triangles == null)
            return false;

        int triangleCount = triangles.Length / 3;
        if (triangleCount <= 0)
            return false;

        int sortCount = Mathf.NextPowerOfTwo(triangleCount);

        // Prepare triangle indices
        Vector3Int[] triIndices = new Vector3Int[triangleCount];
        for (int i = 0; i < triangleCount; i++)
        {
            triIndices[i] = new Vector3Int(
                triangles[i * 3],
                triangles[i * 3 + 1],
                triangles[i * 3 + 2]
            );
        }

        try
        {
            // Create or resize buffers as needed
            EnsureBuffers(worldVertices.Length, triangleCount, sortCount);

            _verticesBuffer.SetData(worldVertices);
            _trianglesBuffer.SetData(triIndices);

            // Initialize codes and indices
            uint[] codesInit = new uint[sortCount];
            uint[] indicesInit = new uint[sortCount];
            for (uint i = 0; i < sortCount; i++)
            {
                codesInit[i] = uint.MaxValue;
                indicesInit[i] = (uint)(i < triangleCount ? i : uint.MaxValue);
            }
            _codesBuffer.SetData(codesInit);
            _indicesBuffer.SetData(indicesInit);

            int computeMortonKernel = sortShader.FindKernel("ComputeMorton");
            int bitonicSortKernel = sortShader.FindKernel("BitonicSort");

            sortShader.SetInt("_TriangleCount", triangleCount);
            sortShader.SetVector("_BoundsMin", bounds.min);
            sortShader.SetVector("_BoundsMax", bounds.max);

            sortShader.SetBuffer(computeMortonKernel, "_Vertices", _verticesBuffer);
            sortShader.SetBuffer(computeMortonKernel, "_Triangles", _trianglesBuffer);
            sortShader.SetBuffer(computeMortonKernel, "_MortonCodes", _codesBuffer);
            sortShader.SetBuffer(computeMortonKernel, "_SortedTriangleIndices", _indicesBuffer);

            int groupCount = Mathf.CeilToInt(triangleCount / (float)ThreadGroupSize);
            sortShader.Dispatch(computeMortonKernel, groupCount, 1, 1);

            sortShader.SetInt("_SortCount", sortCount);
            sortShader.SetBuffer(bitonicSortKernel, "_MortonCodes", _codesBuffer);
            sortShader.SetBuffer(bitonicSortKernel, "_SortedTriangleIndices", _indicesBuffer);

            int sortGroupCount = Mathf.CeilToInt(sortCount / (float)ThreadGroupSize);
            for (int k = 2; k <= sortCount; k <<= 1)
            {
                sortShader.SetInt("_SortK", k);
                for (int j = k >> 1; j > 0; j >>= 1)
                {
                    sortShader.SetInt("_SortJ", j);
                    sortShader.Dispatch(bitonicSortKernel, sortGroupCount, 1, 1);
                }
            }

            // Use async readback with wait - this is safer than GetData
            var request = AsyncGPUReadback.Request(_indicesBuffer);
            request.WaitForCompletion();

            if (request.hasError)
            {
                Debug.LogError("GPU readback error in BVH sorter");
                return false;
            }

            var data = request.GetData<uint>();
            sortedTriangleIndices = new int[triangleCount];
            for (int i = 0; i < triangleCount; i++)
            {
                uint idx = data[i];
                // Validate index
                if (idx < triangleCount)
                {
                    sortedTriangleIndices[i] = (int)idx;
                }
                else
                {
                    sortedTriangleIndices[i] = i; // Fallback to original order
                }
            }

            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"GPU BVH sort failed: {e.Message}");
            return false;
        }
        // Note: We don't release buffers here - they're reused
    }

    private void EnsureBuffers(int vertexCount, int triangleCount, int sortCount)
    {
        if (_verticesBuffer == null || _lastVertexCount < vertexCount)
        {
            _verticesBuffer?.Release();
            _verticesBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 3);
            _lastVertexCount = vertexCount;
        }

        if (_trianglesBuffer == null || _lastTriangleCount < triangleCount)
        {
            _trianglesBuffer?.Release();
            _trianglesBuffer = new ComputeBuffer(triangleCount, sizeof(int) * 3);
            _lastTriangleCount = triangleCount;
        }

        if (_codesBuffer == null || _lastSortCount < sortCount)
        {
            _codesBuffer?.Release();
            _indicesBuffer?.Release();
            _codesBuffer = new ComputeBuffer(sortCount, sizeof(uint));
            _indicesBuffer = new ComputeBuffer(sortCount, sizeof(uint));
            _lastSortCount = sortCount;
        }
    }

    public void Release()
    {
        _verticesBuffer?.Release();
        _trianglesBuffer?.Release();
        _codesBuffer?.Release();
        _indicesBuffer?.Release();
        
        _verticesBuffer = null;
        _trianglesBuffer = null;
        _codesBuffer = null;
        _indicesBuffer = null;
        
        _lastVertexCount = 0;
        _lastTriangleCount = 0;
        _lastSortCount = 0;
    }

    ~GpuBVHSorter()
    {
        Release();
    }
}