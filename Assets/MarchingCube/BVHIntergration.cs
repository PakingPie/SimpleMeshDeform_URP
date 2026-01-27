// BVHIntegration.cs
using UnityEngine;
using System;
using System.Runtime.InteropServices;

/// <summary>
/// Bridge class to integrate with existing LinearBVH implementation.
/// Provides GPU buffers for SDF generation from BVH-accelerated meshes.
/// </summary>
public class BVHIntegration : IDisposable
{
    /// <summary>
    /// Triangle data structure matching compute shader layout.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GPUTriangle
    {
        public Vector3 V0;
        public Vector3 V1;
        public Vector3 V2;
        public Vector3 Normal;
        public Vector2 UV0;
        public Vector2 UV1;
        public Vector2 UV2;

        public static int Stride => sizeof(float) * 17;
    }

    /// <summary>
    /// BVH node structure matching compute shader layout.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GPUBVHNode
    {
        public Vector3 BoundsMin;
        public int LeftFirst;      // Left child index or first primitive index
        public Vector3 BoundsMax;
        public int PrimitiveCount; // 0 = internal node, >0 = leaf node

        public static int Stride => sizeof(float) * 6 + sizeof(int) * 2;
    }

    private ComputeBuffer _triangleBuffer;
    private ComputeBuffer _bvhNodeBuffer;
    private GPUTriangle[] _triangles;
    private GPUBVHNode[] _bvhNodes;
    
    private bool _disposed;

    public ComputeBuffer TriangleBuffer => _triangleBuffer;
    public ComputeBuffer BVHNodeBuffer => _bvhNodeBuffer;
    public int TriangleCount => _triangles?.Length ?? 0;
    public int NodeCount => _bvhNodes?.Length ?? 0;

    /// <summary>
    /// Build GPU buffers from a mesh using your existing LinearBVH.
    /// </summary>
    /// <param name="mesh">Source mesh</param>
    /// <param name="transform">Optional transform to apply</param>
    public void BuildFromMesh(Mesh mesh, Matrix4x4? transform = null)
    {
        Matrix4x4 matrix = transform ?? Matrix4x4.identity;
        
        Vector3[] vertices = mesh.vertices;
        Vector3[] normals = mesh.normals;
        Vector2[] uvs = mesh.uv;
        int[] indices = mesh.triangles;

        int triangleCount = indices.Length / 3;
        _triangles = new GPUTriangle[triangleCount];

        // Build triangle array
        for (int t = 0; t < triangleCount; t++)
        {
            int i0 = indices[t * 3];
            int i1 = indices[t * 3 + 1];
            int i2 = indices[t * 3 + 2];

            Vector3 v0 = matrix.MultiplyPoint3x4(vertices[i0]);
            Vector3 v1 = matrix.MultiplyPoint3x4(vertices[i1]);
            Vector3 v2 = matrix.MultiplyPoint3x4(vertices[i2]);

            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            Vector3 normal = Vector3.Cross(edge1, edge2).normalized;

            _triangles[t] = new GPUTriangle
            {
                V0 = v0,
                V1 = v1,
                V2 = v2,
                Normal = normal,
                UV0 = uvs != null && uvs.Length > i0 ? uvs[i0] : Vector2.zero,
                UV1 = uvs != null && uvs.Length > i1 ? uvs[i1] : Vector2.zero,
                UV2 = uvs != null && uvs.Length > i2 ? uvs[i2] : Vector2.zero
            };
        }

        // Build BVH
        BuildBVH();

        // Create GPU buffers
        CreateBuffers();
    }

    /// <summary>
    /// Build GPU buffers from existing LinearBVH instance.
    /// Call this if you already have a LinearBVH built.
    /// </summary>
    public void BuildFromExistingBVH(object linearBVH, Mesh mesh)
    {
        // This method would extract data from your existing LinearBVH implementation
        // The exact implementation depends on your LinearBVH class structure
        
        // Example assuming LinearBVH has these properties:
        // - Nodes: array of BVH nodes
        // - Triangles: array of triangle indices
        
        /*
        LinearBVH bvh = (LinearBVH)linearBVH;
        
        // Convert nodes
        _bvhNodes = new GPUBVHNode[bvh.NodeCount];
        for (int i = 0; i < bvh.NodeCount; i++)
        {
            var node = bvh.Nodes[i];
            _bvhNodes[i] = new GPUBVHNode
            {
                BoundsMin = node.Bounds.min,
                BoundsMax = node.Bounds.max,
                LeftFirst = node.LeftFirst,
                PrimitiveCount = node.PrimitiveCount
            };
        }
        
        // Convert triangles in BVH order
        _triangles = new GPUTriangle[bvh.TriangleCount];
        // ... convert triangles in the order specified by BVH
        */
        
        Debug.LogWarning("BuildFromExistingBVH requires implementation matching your LinearBVH class.");
        
        // Fallback to rebuilding
        BuildFromMesh(mesh);
    }

    private void BuildBVH()
    {
        // Simple top-down BVH builder
        // Replace with your LinearBVH implementation for better performance
        
        int nodeCount = _triangles.Length * 2 - 1; // Max nodes for binary tree
        _bvhNodes = new GPUBVHNode[nodeCount];
        
        int[] triangleIndices = new int[_triangles.Length];
        for (int i = 0; i < triangleIndices.Length; i++)
        {
            triangleIndices[i] = i;
        }

        int nodeIndex = 0;
        BuildBVHRecursive(triangleIndices, 0, triangleIndices.Length, ref nodeIndex);
        
        // Trim unused nodes
        Array.Resize(ref _bvhNodes, nodeIndex);
    }

    private int BuildBVHRecursive(int[] indices, int start, int count, ref int nodeIndex)
    {
        int currentNode = nodeIndex++;
        
        // Calculate bounds for this node
        Bounds bounds = CalculateTriangleBounds(indices, start, count);
        
        _bvhNodes[currentNode].BoundsMin = bounds.min;
        _bvhNodes[currentNode].BoundsMax = bounds.max;

        if (count <= 4) // Leaf node threshold
        {
            _bvhNodes[currentNode].LeftFirst = start;
            _bvhNodes[currentNode].PrimitiveCount = count;
            
            // Reorder triangles to match indices
            ReorderTriangles(indices, start, count);
        }
        else
        {
            // Find split axis and position
            int axis = GetLongestAxis(bounds);
            int mid = start + count / 2;
            
            // Sort indices along split axis
            SortIndicesAlongAxis(indices, start, count, axis);
            
            // Build children
            int leftCount = mid - start;
            int rightCount = count - leftCount;
            
            int leftChild = BuildBVHRecursive(indices, start, leftCount, ref nodeIndex);
            int rightChild = BuildBVHRecursive(indices, mid, rightCount, ref nodeIndex);
            
            _bvhNodes[currentNode].LeftFirst = leftChild;
            _bvhNodes[currentNode].PrimitiveCount = 0; // Internal node
        }

        return currentNode;
    }

    private Bounds CalculateTriangleBounds(int[] indices, int start, int count)
    {
        Vector3 min = Vector3.positiveInfinity;
        Vector3 max = Vector3.negativeInfinity;

        for (int i = start; i < start + count; i++)
        {
            int triIndex = indices[i];
            GPUTriangle tri = _triangles[triIndex];
            
            min = Vector3.Min(min, Vector3.Min(tri.V0, Vector3.Min(tri.V1, tri.V2)));
            max = Vector3.Max(max, Vector3.Max(tri.V0, Vector3.Max(tri.V1, tri.V2)));
        }

        return new Bounds((min + max) * 0.5f, max - min);
    }

    private int GetLongestAxis(Bounds bounds)
    {
        Vector3 size = bounds.size;
        if (size.x > size.y && size.x > size.z) return 0;
        if (size.y > size.z) return 1;
        return 2;
    }

    private void SortIndicesAlongAxis(int[] indices, int start, int count, int axis)
    {
        // Simple insertion sort for small arrays, use quicksort for large
        Array.Sort(indices, start, count, new TriangleCentroidComparer(_triangles, axis));
    }

    private void ReorderTriangles(int[] indices, int start, int count)
    {
        // Reorder triangles to match BVH leaf order
        GPUTriangle[] temp = new GPUTriangle[count];
        
        for (int i = 0; i < count; i++)
        {
            temp[i] = _triangles[indices[start + i]];
        }
        
        for (int i = 0; i < count; i++)
        {
            _triangles[start + i] = temp[i];
            indices[start + i] = start + i;
        }
    }

    private class TriangleCentroidComparer : System.Collections.Generic.IComparer<int>
    {
        private GPUTriangle[] _triangles;
        private int _axis;

        public TriangleCentroidComparer(GPUTriangle[] triangles, int axis)
        {
            _triangles = triangles;
            _axis = axis;
        }

        public int Compare(int a, int b)
        {
            Vector3 centroidA = (_triangles[a].V0 + _triangles[a].V1 + _triangles[a].V2) / 3f;
            Vector3 centroidB = (_triangles[b].V0 + _triangles[b].V1 + _triangles[b].V2) / 3f;
            
            return centroidA[_axis].CompareTo(centroidB[_axis]);
        }
    }

    private void CreateBuffers()
    {
        DisposeBuffers();

        if (_triangles != null && _triangles.Length > 0)
        {
            _triangleBuffer = new ComputeBuffer(_triangles.Length, GPUTriangle.Stride);
            _triangleBuffer.SetData(_triangles);
        }

        if (_bvhNodes != null && _bvhNodes.Length > 0)
        {
            _bvhNodeBuffer = new ComputeBuffer(_bvhNodes.Length, GPUBVHNode.Stride);
            _bvhNodeBuffer.SetData(_bvhNodes);
        }
    }

    private void DisposeBuffers()
    {
        _triangleBuffer?.Dispose();
        _bvhNodeBuffer?.Dispose();
        _triangleBuffer = null;
        _bvhNodeBuffer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        DisposeBuffers();
        _disposed = true;
    }
}