using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Linearized BVH structure for GPU traversal.
/// </summary>
public class LinearBVH
{
    // GPU-compatible node structure (128 bytes aligned)
    public struct LinearBVHNode
    {
        public Vector3 boundsMin;
        public int leftOrTriangleOffset; // If leaf: triangle start index, else: left child index
        public Vector3 boundsMax;
        public int triangleCount; // If leaf: triangle count (>0). If internal: negative right child index (-index)
    }

    // GPU-compatible triangle structure
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
    /// Builds a linearized BVH from a MeshBVH for GPU use.
    /// </summary>
    public void BuildFromBVH(MeshBVH bvh)
    {
        if (bvh.Root == null)
        {
            nodes = new LinearBVHNode[0];
            gpuTriangles = new GPUTriangle[0];
            return;
        }

        // Count nodes first
        nodeCount = CountNodes(bvh.Root);
        nodes = new LinearBVHNode[nodeCount];

        // Collect all triangles referenced by leaves
        List<GPUTriangle> triangleList = new List<GPUTriangle>();
        int nodeIndex = 0;

        LinearizeNode(bvh, bvh.Root, ref nodeIndex, triangleList);

        gpuTriangles = triangleList.ToArray();

        Debug.Log($"LinearBVH built: {nodeCount} nodes, {gpuTriangles.Length} triangles");
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

        LinearBVHNode linearNode = new LinearBVHNode();
        linearNode.boundsMin = node.bounds.min;
        linearNode.boundsMax = node.bounds.max;

        if (node.IsLeaf)
        {
            linearNode.leftOrTriangleOffset = triangleList.Count;
            linearNode.triangleCount = node.triangleIndices.Length;

            // Add triangles
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
            // Internal node
            // Left child is immediately after this node
            int leftIndex = LinearizeNode(bvh, node.left, ref nodeIndex, triangleList);
            // Right child follows left subtree
            int rightIndex = LinearizeNode(bvh, node.right, ref nodeIndex, triangleList);

            linearNode.leftOrTriangleOffset = leftIndex;
            linearNode.triangleCount = -rightIndex;
        }

        nodes[myIndex] = linearNode;
        return myIndex;
    }

    /// <summary>
    /// Creates compute buffers for GPU use.
    /// </summary>
    public void CreateBuffers(out ComputeBuffer nodeBuffer, out ComputeBuffer triangleBuffer)
    {
        if (nodes == null || nodes.Length == 0)
        {
            nodeBuffer = null;
            triangleBuffer = null;
            return;
        }

        // Node buffer: 2 * Vector3 (24 bytes) + 2 * int (8 bytes) = 32 bytes
        nodeBuffer = new ComputeBuffer(nodes.Length, 32);
        nodeBuffer.SetData(nodes);

        // Triangle buffer: 3 * Vector4 (48 bytes) 
        if (gpuTriangles != null && gpuTriangles.Length > 0)
        {
            triangleBuffer = new ComputeBuffer(gpuTriangles.Length, 48);
            triangleBuffer.SetData(gpuTriangles);
        }
        else
        {
            // Create dummy buffer
            triangleBuffer = new ComputeBuffer(1, 48);
        }
    }
}