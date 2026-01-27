// MarchingCubesGPU.cs
using UnityEngine;
using UnityEngine.Rendering;
using System;
using System.Collections.Generic;

/// <summary>
/// GPU-accelerated Marching Cubes implementation.
/// Extracts mesh from SDF volume using compute shaders.
/// </summary>
public class MarchingCubesGPU : IDisposable
{
    private ComputeShader _marchingCubesShader;
    private ComputeBuffer _edgeTableBuffer;
    private ComputeBuffer _triangleTableBuffer;
    private ComputeBuffer _vertexBuffer;
    private ComputeBuffer _normalBuffer;
    private ComputeBuffer _triangleBuffer;
    private ComputeBuffer _counterBuffer;

    private int _clearKernel;
    private int _generateKernel;
    private int _buildKernel;

    private int _maxVertices;
    private int _maxTriangles;

    private bool _disposed;

    // Cached arrays for mesh building
    private Vector3[] _vertices;
    private Vector3[] _normals;
    private int[] _triangles;
    private int[] _counterData = new int[2];

    public MarchingCubesGPU(ComputeShader shader, int maxVertices = 500000)
    {
        _marchingCubesShader = shader;
        _maxVertices = maxVertices;
        _maxTriangles = maxVertices; // Worst case: same number of triangle indices

        CacheKernels();
        CreateBuffers();
        UploadTables();
    }

    private void CacheKernels()
    {
        _clearKernel = _marchingCubesShader.FindKernel("ClearCounters");
        _generateKernel = _marchingCubesShader.FindKernel("GenerateVertices");
        _buildKernel = _marchingCubesShader.FindKernel("BuildTriangles");
    }

    private void CreateBuffers()
    {
        _vertexBuffer = new ComputeBuffer(_maxVertices, sizeof(float) * 3);
        _normalBuffer = new ComputeBuffer(_maxVertices, sizeof(float) * 3);
        _triangleBuffer = new ComputeBuffer(_maxTriangles, sizeof(int));
        _counterBuffer = new ComputeBuffer(2, sizeof(int));

        _vertices = new Vector3[_maxVertices];
        _normals = new Vector3[_maxVertices];
        _triangles = new int[_maxTriangles];
    }

    private void UploadTables()
    {
        _edgeTableBuffer = MarchingCubesTables.CreateEdgeTableBuffer();
        _triangleTableBuffer = MarchingCubesTables.CreateTriangleTableBuffer();
    }

    /// <summary>
    /// Extract mesh from SDF volume.
    /// </summary>
    /// <param name="sdfVolume">The SDF volume to extract from</param>
    /// <param name="isoLevel">The iso-surface level (typically 0)</param>
    /// <returns>Generated mesh or null if no surface found</returns>
    public Mesh ExtractMesh(SDFVolume sdfVolume, float isoLevel = 0f)
    {
        // Clear counters
        _counterData[0] = 0;
        _counterData[1] = 0;
        _counterBuffer.SetData(_counterData);

        // Set parameters
        SetShaderParameters(sdfVolume, isoLevel);

        // Clear
        _marchingCubesShader.SetBuffer(_clearKernel, "_Counter", _counterBuffer);
        _marchingCubesShader.Dispatch(_clearKernel, 1, 1, 1);

        // Generate vertices
        DispatchGenerate(sdfVolume.Resolution);

        // Read back counter
        _counterBuffer.GetData(_counterData);
        int vertexCount = _counterData[0];
        int triangleCount = _counterData[1];

        if (vertexCount == 0 || triangleCount == 0)
        {
            return null;
        }

        // Clamp to buffer size
        vertexCount = Mathf.Min(vertexCount, _maxVertices);
        triangleCount = Mathf.Min(triangleCount, _maxTriangles);

        // Read back data
        _vertexBuffer.GetData(_vertices, 0, 0, vertexCount);
        _normalBuffer.GetData(_normals, 0, 0, vertexCount);
        _triangleBuffer.GetData(_triangles, 0, 0, triangleCount);

        // Build mesh
        return BuildMesh(vertexCount, triangleCount);
    }

    /// <summary>
    /// Extract mesh asynchronously using AsyncGPUReadback.
    /// </summary>
    public void ExtractMeshAsync(SDFVolume sdfVolume, float isoLevel, Action<Mesh> callback)
    {
        // Clear counters
        _counterData[0] = 0;
        _counterData[1] = 0;
        _counterBuffer.SetData(_counterData);

        // Set parameters
        SetShaderParameters(sdfVolume, isoLevel);

        // Clear
        _marchingCubesShader.SetBuffer(_clearKernel, "_Counter", _counterBuffer);
        _marchingCubesShader.Dispatch(_clearKernel, 1, 1, 1);

        // Generate vertices
        DispatchGenerate(sdfVolume.Resolution);

        // Async readback
        AsyncGPUReadback.Request(_counterBuffer, (counterRequest) =>
        {
            if (counterRequest.hasError)
            {
                Debug.LogError("Counter readback failed");
                callback?.Invoke(null);
                return;
            }

            var counterData = counterRequest.GetData<int>();
            int vertexCount = Mathf.Min(counterData[0], _maxVertices);
            int triangleCount = Mathf.Min(counterData[1], _maxTriangles);

            if (vertexCount == 0 || triangleCount == 0)
            {
                callback?.Invoke(null);
                return;
            }

            // Read vertices
            AsyncGPUReadback.Request(_vertexBuffer, vertexCount * sizeof(float) * 3, 0, (vertRequest) =>
            {
                if (vertRequest.hasError)
                {
                    callback?.Invoke(null);
                    return;
                }

                var verts = vertRequest.GetData<Vector3>();
                
                // Read normals
                AsyncGPUReadback.Request(_normalBuffer, vertexCount * sizeof(float) * 3, 0, (normRequest) =>
                {
                    if (normRequest.hasError)
                    {
                        callback?.Invoke(null);
                        return;
                    }

                    var norms = normRequest.GetData<Vector3>();
                    
                    // Read triangles
                    AsyncGPUReadback.Request(_triangleBuffer, triangleCount * sizeof(int), 0, (triRequest) =>
                    {
                        if (triRequest.hasError)
                        {
                            callback?.Invoke(null);
                            return;
                        }

                        var tris = triRequest.GetData<int>();
                        
                        // Build mesh on main thread
                        Mesh mesh = new Mesh();
                        mesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                        mesh.SetVertices(verts.ToArray(), 0, vertexCount);
                        mesh.SetNormals(norms.ToArray(), 0, vertexCount);
                        mesh.SetTriangles(tris.ToArray(), 0, triangleCount, 0);
                        mesh.RecalculateBounds();
                        
                        callback?.Invoke(mesh);
                    });
                });
            });
        });
    }

    private void SetShaderParameters(SDFVolume sdfVolume, float isoLevel)
    {
        _marchingCubesShader.SetTexture(_generateKernel, "_SDFVolume", sdfVolume.VolumeTexture);
        _marchingCubesShader.SetVector("_VolumeMin", sdfVolume.WorldBounds.min);
        _marchingCubesShader.SetVector("_VolumeMax", sdfVolume.WorldBounds.max);
        _marchingCubesShader.SetInts("_VolumeResolution", 
            sdfVolume.Resolution.x, sdfVolume.Resolution.y, sdfVolume.Resolution.z);
        _marchingCubesShader.SetFloat("_IsoLevel", isoLevel);

        _marchingCubesShader.SetBuffer(_generateKernel, "_EdgeTable", _edgeTableBuffer);
        _marchingCubesShader.SetBuffer(_generateKernel, "_TriangleTable", _triangleTableBuffer);
        _marchingCubesShader.SetBuffer(_generateKernel, "_Vertices", _vertexBuffer);
        _marchingCubesShader.SetBuffer(_generateKernel, "_Normals", _normalBuffer);
        _marchingCubesShader.SetBuffer(_generateKernel, "_Triangles", _triangleBuffer);
        _marchingCubesShader.SetBuffer(_generateKernel, "_Counter", _counterBuffer);
    }

    private void DispatchGenerate(Vector3Int resolution)
    {
        int threadGroupsX = Mathf.CeilToInt((resolution.x - 1) / 8f);
        int threadGroupsY = Mathf.CeilToInt((resolution.y - 1) / 8f);
        int threadGroupsZ = Mathf.CeilToInt((resolution.z - 1) / 8f);
        
        _marchingCubesShader.Dispatch(_generateKernel, 
            Mathf.Max(1, threadGroupsX), 
            Mathf.Max(1, threadGroupsY), 
            Mathf.Max(1, threadGroupsZ));
    }

    private Mesh BuildMesh(int vertexCount, int triangleCount)
    {
        Mesh mesh = new Mesh();
        mesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;

        // Copy to correctly sized arrays
        Vector3[] finalVerts = new Vector3[vertexCount];
        Vector3[] finalNorms = new Vector3[vertexCount];
        int[] finalTris = new int[triangleCount];

        Array.Copy(_vertices, finalVerts, vertexCount);
        Array.Copy(_normals, finalNorms, vertexCount);
        Array.Copy(_triangles, finalTris, triangleCount);

        mesh.vertices = finalVerts;
        mesh.normals = finalNorms;
        mesh.triangles = finalTris;
        mesh.RecalculateBounds();

        return mesh;
    }

    /// <summary>
    /// Resize internal buffers if needed for higher resolution volumes.
    /// </summary>
    public void EnsureCapacity(int maxVertices)
    {
        if (maxVertices <= _maxVertices) return;

        _maxVertices = maxVertices;
        _maxTriangles = maxVertices;

        // Dispose old buffers
        _vertexBuffer?.Dispose();
        _normalBuffer?.Dispose();
        _triangleBuffer?.Dispose();

        // Create new buffers
        CreateBuffers();
    }

    public void Dispose()
    {
        if (_disposed) return;

        _edgeTableBuffer?.Dispose();
        _triangleTableBuffer?.Dispose();
        _vertexBuffer?.Dispose();
        _normalBuffer?.Dispose();
        _triangleBuffer?.Dispose();
        _counterBuffer?.Dispose();

        _disposed = true;
    }
}