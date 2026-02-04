using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Linearized BVH structure for GPU traversal.
/// </summary>
public class LinearBVH
{
    // GPU-compatible node structure
    public struct LinearBVHNode
    {
        public Vector3 boundsMin;
        public int leftOrTriangleOffset; // If leaf: triangle start index, else: left child index
        public Vector3 boundsMax;
        public int rightChildOrCount;    // If leaf: triangle count (>0). If internal: right child index (stored as negative: -index)
    }

    // GPU-compatible triangle structure (48 bytes)
    public struct GPUTriangle
    {
        public Vector3 v0;
        public float pad0;
        public Vector3 v1;
        public float pad1;
        public Vector3 v2;
        public float pad2;
    }

    private LinearBVHNode[] nodes;
    private GPUTriangle[] gpuTriangles;
    private int nodeCount;

    public LinearBVHNode[] Nodes => nodes;
    public GPUTriangle[] GPUTriangles => gpuTriangles;
    public int NodeCount => nodeCount;

    /// <summary>
    /// Builds a linearized BVH directly from a mesh using an optional sorted triangle index order.
    /// </summary>
    public void BuildFromMesh(Mesh mesh, Transform transform, int[] sortedTriangleIndices = null, int maxTrianglesPerLeaf = 4)
    {
        if (mesh == null)
        {
            nodes = new LinearBVHNode[0];
            gpuTriangles = new GPUTriangle[0];
            nodeCount = 0;
            return;
        }

        maxTrianglesPerLeaf = Mathf.Max(1, maxTrianglesPerLeaf);

        Vector3[] localVertices = mesh.vertices;
        int[] meshTriangles = mesh.triangles;
        int triangleCount = meshTriangles.Length / 3;

        if (triangleCount == 0)
        {
            nodes = new LinearBVHNode[0];
            gpuTriangles = new GPUTriangle[0];
            nodeCount = 0;
            return;
        }

        Vector3[] worldVertices = new Vector3[localVertices.Length];
        for (int i = 0; i < localVertices.Length; i++)
        {
            worldVertices[i] = transform.TransformPoint(localVertices[i]);
        }

        if (sortedTriangleIndices == null || sortedTriangleIndices.Length != triangleCount)
        {
            sortedTriangleIndices = new int[triangleCount];
            for (int i = 0; i < triangleCount; i++)
            {
                sortedTriangleIndices[i] = i;
            }
        }

        // Precompute triangle bounds
        Vector3[] triBoundsMin = new Vector3[triangleCount];
        Vector3[] triBoundsMax = new Vector3[triangleCount];
        for (int i = 0; i < triangleCount; i++)
        {
            int i0 = meshTriangles[i * 3];
            int i1 = meshTriangles[i * 3 + 1];
            int i2 = meshTriangles[i * 3 + 2];

            Vector3 v0 = worldVertices[i0];
            Vector3 v1 = worldVertices[i1];
            Vector3 v2 = worldVertices[i2];

            triBoundsMin[i] = Vector3.Min(v0, Vector3.Min(v1, v2));
            triBoundsMax[i] = Vector3.Max(v0, Vector3.Max(v1, v2));
        }

        // Count nodes first
        nodeCount = CountNodesForRange(triangleCount, maxTrianglesPerLeaf);
        nodes = new LinearBVHNode[nodeCount];

        List<GPUTriangle> triangleList = new List<GPUTriangle>(triangleCount);
        int nodeIndex = 0;
        BuildNodeFromRange(sortedTriangleIndices, triBoundsMin, triBoundsMax, worldVertices, meshTriangles, 
                          0, triangleCount, maxTrianglesPerLeaf, ref nodeIndex, triangleList);

        gpuTriangles = triangleList.ToArray();
    }

    /// <summary>
    /// Builds a linearized BVH from a MeshBVH for GPU use.
    /// </summary>
    public void BuildFromBVH(MeshBVH bvh)
    {
        if (bvh == null || bvh.Root == null)
        {
            nodes = new LinearBVHNode[0];
            gpuTriangles = new GPUTriangle[0];
            nodeCount = 0;
            return;
        }

        nodeCount = CountNodes(bvh.Root);
        nodes = new LinearBVHNode[nodeCount];

        List<GPUTriangle> triangleList = new List<GPUTriangle>();
        int nodeIndex = 0;

        LinearizeNode(bvh, bvh.Root, ref nodeIndex, triangleList);

        gpuTriangles = triangleList.ToArray();
    }

    private int CountNodes(MeshBVH.BVHNode node)
    {
        if (node == null) return 0;
        return 1 + CountNodes(node.left) + CountNodes(node.right);
    }

    private int LinearizeNode(MeshBVH bvh, MeshBVH.BVHNode node, ref int nodeIndex, List<GPUTriangle> triangleList)
    {
        int myIndex = nodeIndex;
        nodeIndex++;

        LinearBVHNode linearNode = new LinearBVHNode
        {
            boundsMin = node.bounds.min,
            boundsMax = node.bounds.max
        };

        if (node.IsLeaf)
        {
            linearNode.leftOrTriangleOffset = triangleList.Count;
            linearNode.rightChildOrCount = node.triangleIndices.Length; // Positive = leaf triangle count

            foreach (int triIdx in node.triangleIndices)
            {
                GPUTriangle gpuTri = new GPUTriangle();
                int i0 = bvh.Triangles[triIdx * 3];
                int i1 = bvh.Triangles[triIdx * 3 + 1];
                int i2 = bvh.Triangles[triIdx * 3 + 2];

                gpuTri.v0 = bvh.WorldVertices[i0];
                gpuTri.v1 = bvh.WorldVertices[i1];
                gpuTri.v2 = bvh.WorldVertices[i2];

                triangleList.Add(gpuTri);
            }
        }
        else
        {
            // Internal node - left child immediately follows
            int leftIndex = LinearizeNode(bvh, node.left, ref nodeIndex, triangleList);
            int rightIndex = LinearizeNode(bvh, node.right, ref nodeIndex, triangleList);

            linearNode.leftOrTriangleOffset = leftIndex;
            linearNode.rightChildOrCount = -rightIndex; // Negative = internal node, stores right child index
        }

        nodes[myIndex] = linearNode;
        return myIndex;
    }

    private int CountNodesForRange(int count, int maxTrianglesPerLeaf)
    {
        if (count <= maxTrianglesPerLeaf)
            return 1;

        int leftCount = count / 2;
        int rightCount = count - leftCount;
        return 1 + CountNodesForRange(leftCount, maxTrianglesPerLeaf) + CountNodesForRange(rightCount, maxTrianglesPerLeaf);
    }

    private int BuildNodeFromRange(
        int[] sortedTriangleIndices,
        Vector3[] triBoundsMin,
        Vector3[] triBoundsMax,
        Vector3[] worldVertices,
        int[] meshTriangles,
        int start,
        int count,
        int maxTrianglesPerLeaf,
        ref int nodeIndex,
        List<GPUTriangle> triangleList)
    {
        int myIndex = nodeIndex;
        nodeIndex++;

        // Calculate bounds for this range
        Vector3 boundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 boundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

        for (int i = 0; i < count; i++)
        {
            int triIdx = sortedTriangleIndices[start + i];
            boundsMin = Vector3.Min(boundsMin, triBoundsMin[triIdx]);
            boundsMax = Vector3.Max(boundsMax, triBoundsMax[triIdx]);
        }

        LinearBVHNode linearNode = new LinearBVHNode
        {
            boundsMin = boundsMin,
            boundsMax = boundsMax
        };

        if (count <= maxTrianglesPerLeaf)
        {
            // Leaf node
            linearNode.leftOrTriangleOffset = triangleList.Count;
            linearNode.rightChildOrCount = count; // Positive = triangle count

            for (int i = 0; i < count; i++)
            {
                int triIdx = sortedTriangleIndices[start + i];
                int i0 = meshTriangles[triIdx * 3];
                int i1 = meshTriangles[triIdx * 3 + 1];
                int i2 = meshTriangles[triIdx * 3 + 2];

                GPUTriangle gpuTri = new GPUTriangle
                {
                    v0 = worldVertices[i0],
                    v1 = worldVertices[i1],
                    v2 = worldVertices[i2]
                };

                triangleList.Add(gpuTri);
            }
        }
        else
        {
            // Internal node
            int leftCount = count / 2;
            int rightCount = count - leftCount;

            int leftIndex = BuildNodeFromRange(sortedTriangleIndices, triBoundsMin, triBoundsMax, 
                                               worldVertices, meshTriangles, start, leftCount, 
                                               maxTrianglesPerLeaf, ref nodeIndex, triangleList);
            int rightIndex = BuildNodeFromRange(sortedTriangleIndices, triBoundsMin, triBoundsMax, 
                                                worldVertices, meshTriangles, start + leftCount, rightCount, 
                                                maxTrianglesPerLeaf, ref nodeIndex, triangleList);

            linearNode.leftOrTriangleOffset = leftIndex;
            linearNode.rightChildOrCount = -rightIndex; // Negative = right child index
        }

        nodes[myIndex] = linearNode;
        return myIndex;
    }

    /// <summary>
    /// Creates compute buffers for GPU use.
    /// </summary>
    public void CreateBuffers(out ComputeBuffer nodeBuffer, out ComputeBuffer triangleBuffer)
    {
        nodeBuffer = null;
        triangleBuffer = null;

        if (nodes == null || nodes.Length == 0)
        {
            return;
        }

        // Node buffer: 2 * Vector3 (24 bytes) + 2 * int (8 bytes) = 32 bytes
        nodeBuffer = new ComputeBuffer(nodes.Length, 32);
        nodeBuffer.SetData(nodes);

        // Triangle buffer: 3 * (Vector3 + float) = 48 bytes
        if (gpuTriangles != null && gpuTriangles.Length > 0)
        {
            triangleBuffer = new ComputeBuffer(gpuTriangles.Length, 48);
            triangleBuffer.SetData(gpuTriangles);
        }
        else
        {
            // Create minimal dummy buffer to avoid null binding
            triangleBuffer = new ComputeBuffer(1, 48);
            triangleBuffer.SetData(new GPUTriangle[1]);
        }
    }
}